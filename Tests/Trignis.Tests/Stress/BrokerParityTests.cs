using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trignis.Models;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests.Stress;

/// <summary>Runs if the brokers in Tests/brokers/compose.yml are up and TRIGNIS_BROKERS is set.</summary>
public sealed class BrokerFactAttribute : FactAttribute
{
    public BrokerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TRIGNIS_BROKERS") is null)
            Skip = "Set TRIGNIS_BROKERS (see Tests/brokers/run.sh)";
    }
}

/// <summary>The connection probe and a broker send agree for each broker type.</summary>
public sealed class BrokerParityTests
{
    private const string EmulatorKey = "SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
    private const string SqsUrl = "http://localhost:9324";

    private static readonly ConnectionHealthCheckService Prober = new(
        NullLogger<ConnectionHealthCheckService>.Instance,
        Options.Create(new GlobalSettings()),
        new EnvironmentConfigService(NullLogger<EnvironmentConfigService>.Instance,
            new EncryptionService(Directory.CreateTempSubdirectory("trignis-brokers").FullName)));

    static BrokerParityTests() => Environment.SetEnvironmentVariable("AWS_ENDPOINT_URL_SQS", SqsUrl);

    private static ApiEndpoint Endpoint(string type, MessageQueueConfig config) => new()
    {
        Key = Guid.NewGuid().ToString("N"),
        EnvironmentFile = "brokers.json",
        MessageQueueType = type,
        MessageQueue = config,
    };

    private static async Task<bool> SendSucceeds(ApiEndpoint endpoint)
    {
        await using var queues = new MessageQueueService(NullLogger<MessageQueueService>.Instance);
        using var data = JsonDocument.Parse("""[{"id":1}]""");
        try
        {
            await queues.SendToQueueAsync(endpoint, data.RootElement);
            return true;
        }
        // ExportService treats any exception as a failed send.
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task AssertParity(ApiEndpoint endpoint, bool expected)
    {
        Assert.Equal(expected, await Prober.CheckMessageQueueHealthAsync(endpoint));
        Assert.Equal(expected, await SendSucceeds(endpoint));
    }

    private static MessageQueueConfig Rabbit(int port) =>
        new() { HostName = "localhost", Port = port, Username = "trignis", Password = "trignis", QueueName = "trignis-queue" };

    [BrokerFact]
    public Task RabbitMq_ProbeAndSendSucceed() => AssertParity(Endpoint("rabbitmq", Rabbit(5672)), true);

    [BrokerFact]
    public Task RabbitMq_ProbeAndSendFail_ForAClosedPort() => AssertParity(Endpoint("rabbitmq", Rabbit(5999)), false);

    [BrokerFact]
    public async Task RabbitMq_DoesNotReuseAConnectionAcrossLogins()
    {
        await using var queues = new MessageQueueService(NullLogger<MessageQueueService>.Instance);
        using var data = JsonDocument.Parse("""[{"id":1}]""");
        await queues.SendToQueueAsync(Endpoint("rabbitmq", Rabbit(5672)), data.RootElement);

        var otherLogin = Rabbit(5672) with { Username = "intruder", Password = "wrong" };
        await Assert.ThrowsAnyAsync<Exception>(() => queues.SendToQueueAsync(Endpoint("rabbitmq", otherLogin), data.RootElement));
    }

    private static async Task<string> KafkaTopic()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = "localhost:9092" }).Build();
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = "trignis-topic", NumPartitions = 1, ReplicationFactor = 1 }]);
        }
        catch (CreateTopicsException ex) when (ex.Results[0].Error.Code == ErrorCode.TopicAlreadyExists)
        {
        }
        return "trignis-topic";
    }

    [BrokerFact]
    public async Task Kafka_ProbeAndSendSucceed() =>
        await AssertParity(Endpoint("kafka", new() { BootstrapServers = "localhost:9092", Topic = await KafkaTopic() }), true);

    [BrokerFact]
    public Task Kafka_ProbeAndSendFail_ForAMissingTopic() =>
        AssertParity(Endpoint("kafka", new() { BootstrapServers = "localhost:9092", Topic = "missing-topic" }), false);

    [BrokerFact]
    public async Task Kafka_OversizedPayload_IsSplitAndEveryRowArrives()
    {
        var topic = await KafkaTopic();
        await using var queues = new MessageQueueService(NullLogger<MessageQueueService>.Instance);
        var rows = Enumerable.Range(1, 40_000).Select(i => new { id = i, val = "row-" + i });
        await queues.SendToQueueAsync(Endpoint("kafka", new() { BootstrapServers = "localhost:9092", Topic = topic }),
            JsonSerializer.SerializeToElement(rows));

        using var consumer = new ConsumerBuilder<Ignore, string>(new ConsumerConfig
        {
            BootstrapServers = "localhost:9092", GroupId = Guid.NewGuid().ToString("N"), AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);
        var ids = new HashSet<long>();
        // Joining a new consumer group takes a few empty polls.
        for (var idle = 0; idle < 3;)
        {
            if (consumer.Consume(TimeSpan.FromSeconds(2)) is not { } message) { idle++; continue; }
            idle = 0;
            foreach (var row in JsonDocument.Parse(message.Message.Value).RootElement.EnumerateArray())
                ids.Add(row.GetProperty("id").GetInt64());
        }

        Assert.Equal(40_000, ids.Count(id => id <= 40_000));
    }

    private static async Task<string> SqsQueueUrl(string name)
    {
        using var sqs = new AmazonSQSClient("x", "x", new AmazonSQSConfig { ServiceURL = SqsUrl });
        return (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
    }

    private static MessageQueueConfig Sqs(string queueUrl) =>
        new() { QueueUrl = queueUrl, Region = "us-east-1", AccessKeyId = "x", SecretAccessKey = "x" };

    [BrokerFact]
    public async Task Sqs_ProbeAndSendSucceed() =>
        await AssertParity(Endpoint("awssqs", Sqs(await SqsQueueUrl("trignis-queue"))), true);

    [BrokerFact]
    public Task Sqs_ProbeAndSendFail_ForAMissingQueue() =>
        AssertParity(Endpoint("awssqs", Sqs($"{SqsUrl}/000000000000/missing-queue")), false);

    private static MessageQueueConfig ServiceBus(string queue) =>
        new() { ConnectionString = $"Endpoint=sb://localhost:5673;{EmulatorKey}", QueueName = queue };

    [BrokerFact]
    public Task ServiceBus_ProbeAndSendSucceed() => AssertParity(Endpoint("azureservicebus", ServiceBus("trignis-queue")), true);

    [BrokerFact]
    public Task ServiceBus_ProbeAndSendFail_ForAMissingQueue() => AssertParity(Endpoint("azureservicebus", ServiceBus("missing-queue")), false);

    private static MessageQueueConfig EventHubs(string hub) =>
        new() { ConnectionString = $"Endpoint=sb://localhost:5674;{EmulatorKey}", EventHubName = hub };

    [BrokerFact]
    public Task EventHubs_ProbeAndSendSucceed() => AssertParity(Endpoint("azureeventhubs", EventHubs("trignis-hub")), true);

    [BrokerFact]
    public Task EventHubs_ProbeAndSendFail_ForAMissingHub() => AssertParity(Endpoint("azureeventhubs", EventHubs("missing-hub")), false);
}
