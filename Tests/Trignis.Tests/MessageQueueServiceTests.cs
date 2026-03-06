using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Trignis.MicrosoftSQL.Models;
using Trignis.MicrosoftSQL.Services;
using Xunit;

namespace Trignis.Tests;

/// <summary>
/// Tests for MessageQueueService — covers dispatch logic, required-field guards,
/// and message size validation for all supported queue types.
///
/// These tests verify behaviour that does NOT require a live broker: all guards
/// that throw InvalidOperationException before a network connection is attempted.
/// </summary>
public class MessageQueueServiceTests : IDisposable
{
    private readonly MessageQueueService _svc;

    public MessageQueueServiceTests()
    {
        var logger = NullLogger<MessageQueueService>.Instance;
        var dlqLogger = NullLogger<DeadLetterQueueMonitor>.Instance;
        var config = new ConfigurationBuilder().Build();
        var dlqMonitor = new DeadLetterQueueMonitor(dlqLogger, config);
        _svc = new MessageQueueService(logger, dlqMonitor);
    }

    public void Dispose() => _svc.Dispose();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static JsonElement EmptyJson() =>
        JsonDocument.Parse("{}").RootElement;

    /// <summary>Produces a JsonElement whose serialized form exceeds 1 MB.</summary>
    private static JsonElement LargeJson()
    {
        // 1 MB = 1,048,576 bytes; use 1.1 M ASCII chars to ensure we exceed it
        var json = $"{{\"d\":\"{new string('x', 1_100_000)}\"}}";
        return JsonDocument.Parse(json).RootElement;
    }

    private static ApiEndpoint MqEndpoint(string type, MessageQueueConfig config, string key = "test") =>
        new() { Key = key, MessageQueueType = type, MessageQueue = config };

    // -------------------------------------------------------------------------
    // General dispatch
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NullMessageQueue_Throws()
    {
        var endpoint = new ApiEndpoint { MessageQueueType = "RabbitMQ", MessageQueue = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
    }

    [Fact]
    public async Task UnsupportedQueueType_Throws()
    {
        var endpoint = MqEndpoint("FancyQueue", new MessageQueueConfig());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("FancyQueue", ex.Message);
    }

    // -------------------------------------------------------------------------
    // RabbitMQ
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RabbitMq_MissingHostName_Throws()
    {
        var endpoint = MqEndpoint("RabbitMQ", new MessageQueueConfig
        {
            QueueName = "test-queue",
            // HostName intentionally omitted
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("HostName", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Azure Service Bus
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AzureServiceBus_MissingConnectionString_Throws()
    {
        var endpoint = MqEndpoint("AzureServiceBus", new MessageQueueConfig
        {
            QueueName = "change-tracking",
            // ConnectionString intentionally omitted
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("ConnectionString", ex.Message);
    }

    [Fact]
    public async Task AzureServiceBus_MissingQueueAndTopic_Throws()
    {
        // A valid-format connection string so ServiceBusClient can be constructed,
        // but neither QueueName nor TopicName is set.
        var endpoint = MqEndpoint("AzureServiceBus", new MessageQueueConfig
        {
            ConnectionString = "Endpoint=sb://fake-ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            // QueueName and TopicName intentionally omitted
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("QueueName or TopicName", ex.Message);
    }

    // -------------------------------------------------------------------------
    // AWS SQS
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AwsSqs_MissingQueueUrl_Throws()
    {
        var endpoint = MqEndpoint("AWSSQS", new MessageQueueConfig
        {
            Region = "us-east-1",
            // QueueUrl intentionally omitted
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("QueueUrl", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Azure Event Hubs
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AzureEventHubs_MissingConnectionString_Throws()
    {
        var endpoint = MqEndpoint("AzureEventHubs", new MessageQueueConfig
        {
            EventHubName = "change-tracking",
            // ConnectionString intentionally omitted
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("ConnectionString", ex.Message);
    }

    [Fact]
    public async Task AzureEventHubs_MissingEventHubName_Throws()
    {
        var endpoint = MqEndpoint("AzureEventHubs", new MessageQueueConfig
        {
            ConnectionString = "Endpoint=sb://fake-ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            // EventHubName intentionally omitted
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("EventHubName", ex.Message);
    }

    [Fact]
    public async Task AzureEventHubs_MessageTooLarge_Throws()
    {
        var endpoint = MqEndpoint("AzureEventHubs", new MessageQueueConfig
        {
            ConnectionString = "Endpoint=sb://fake-ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            EventHubName = "change-tracking",
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, LargeJson()));
        Assert.Contains("Azure Event Hubs", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Kafka
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Kafka_MissingBootstrapServers_Throws()
    {
        var endpoint = MqEndpoint("Kafka", new MessageQueueConfig
        {
            Topic = "db-changes",
            // BootstrapServers intentionally omitted
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("BootstrapServers", ex.Message);
    }

    [Fact]
    public async Task Kafka_MissingTopic_Throws()
    {
        var endpoint = MqEndpoint("Kafka", new MessageQueueConfig
        {
            BootstrapServers = "localhost:9092",
            // Topic intentionally omitted
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("Topic", ex.Message);
    }

    [Fact]
    public async Task Kafka_MessageTooLarge_Throws()
    {
        var endpoint = MqEndpoint("Kafka", new MessageQueueConfig
        {
            BootstrapServers = "localhost:9092",
            Topic = "db-changes",
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, LargeJson()));
        Assert.Contains("Kafka", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Case-insensitivity of MessageQueueType
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RabbitMq_TypeIsCaseInsensitive_ThrowsOnMissingHostName()
    {
        // "rabbitmq" and "RABBITMQ" should both route to the RabbitMQ handler
        var endpoint = MqEndpoint("RABBITMQ", new MessageQueueConfig { QueueName = "q" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("HostName", ex.Message);
    }

    [Fact]
    public async Task Kafka_TypeIsCaseInsensitive_ThrowsOnMissingBootstrapServers()
    {
        var endpoint = MqEndpoint("KAFKA", new MessageQueueConfig { Topic = "t" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SendToQueueAsync(endpoint, EmptyJson()));
        Assert.Contains("BootstrapServers", ex.Message);
    }
}
