using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.ServiceBus;
using Amazon.SQS.Model;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Data.Sqlite;
using RabbitMQ.Client;
using Xunit;
using Xunit.Abstractions;

namespace Trignis.Tests.Stress;

/// <summary>Runs if TRIGNIS_LOAD is set and the parity databases and brokers are up (see Tests/load/run.sh).</summary>
public sealed class LoadTheoryAttribute : TheoryAttribute
{
    public LoadTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("TRIGNIS_LOAD") is null || ParitySource.ConnectionString("mssql") is null)
            Skip = "Set TRIGNIS_LOAD (see Tests/load/run.sh)";
    }
}

/// <summary>
/// Hundreds of thousands of inserts and updates to HTTP, file, RabbitMQ, Kafka, SQS, Service Bus and Event Hubs. Every change reaches every destination, with no dead letters.
/// </summary>
[Collection("SqliteTests")]
public sealed class LoadTests(ITestOutputHelper output)
{
    private const string Env = "load";
    private const string SqsUrl = "http://localhost:9324";
    private const string EmulatorKey = "SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
    private const string ServiceBus = $"Endpoint=sb://localhost:5673;{EmulatorKey}";
    private const string EventHubs = $"Endpoint=sb://localhost:5674;{EmulatorKey}";

    private static int Rows => int.TryParse(Environment.GetEnvironmentVariable("TRIGNIS_LOAD_ROWS"), out var n) ? n : 200_000;
    private const int Burst = 10_000;

    public static TheoryData<string> Providers() => new() { "mssql", "postgres" };

    [LoadTheory, MemberData(nameof(Providers))]
    public async Task Load_EveryChangeReachesEveryDestination(string provider)
    {
        Environment.SetEnvironmentVariable("AWS_ENDPOINT_URL_SQS", SqsUrl);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var source = await ParitySource.CreateAsync(provider, transactional: true);
        await using var http = new HttpSink();
        await using var host = new StressHost();
        var topic = await CreateKafkaTopicAsync($"load-{suffix}");
        var sqsQueue = await CreateSqsQueueAsync($"load-{suffix}");
        var rabbitQueue = $"load-{suffix}";
        var exportDir = Path.Combine(host.Root, "exports");
        await DrainServiceBusAsync(); // The emulator queue is shared between runs.
        var started = DateTimeOffset.UtcNow;

        host.WriteEnvJson(Env, $$"""
            {
              "Provider": "{{provider}}",
              "ConnectionStrings": { "src": "{{source.Connection}}" },
              "ChangeTracking": {
                "PollingIntervalSeconds": 1,
                "ExportToFile": true,
                "FilePath": "{{exportDir.Replace("\\", "\\\\")}}/{object}/changes-{timestamp}.json",
                "ExportToApi": true,
                "RetryCount": 0,
                "TrackingObjects": [{ "Name": "items", "Database": "src", "TableName": "{{source.Table}}", "StoredProcedureName": "{{source.Procedure}}", "InitialSyncMode": "Full" }],
                "ApiEndpoints": [
                  { "Key": "http", "Url": "{{http.Url}}" },
                  { "Key": "rabbit", "MessageQueueType": "RabbitMQ", "MessageQueue": { "HostName": "localhost", "Port": 5672, "Username": "trignis", "Password": "trignis", "QueueName": "{{rabbitQueue}}" } },
                  { "Key": "kafka", "MessageQueueType": "Kafka", "MessageQueue": { "BootstrapServers": "localhost:9092", "Topic": "{{topic}}" } },
                  { "Key": "sqs", "MessageQueueType": "AWSSQS", "MessageQueue": { "QueueUrl": "{{sqsQueue}}", "Region": "us-east-1", "AccessKeyId": "x", "SecretAccessKey": "x" } },
                  { "Key": "servicebus", "MessageQueueType": "AzureServiceBus", "MessageQueue": { "ConnectionString": "{{ServiceBus}}", "QueueName": "trignis-queue" } },
                  { "Key": "eventhubs", "MessageQueueType": "AzureEventHubs", "MessageQueue": { "ConnectionString": "{{EventHubs}}", "EventHubName": "trignis-hub" } }
                ]
              }
            }
            """);
        await host.StartAsync();

        var clock = Stopwatch.StartNew();
        var updates = 0;
        for (var written = 0; written < Rows; written += Burst)
        {
            await source.BulkInsertAsync(Math.Min(Burst, Rows - written));
            if (written % (Burst * 4) == 0 && written > 0)
            {
                await source.BulkUpdateAsync(Burst / 2, $"u{written}");
                updates += Burst / 2;
            }
            await Task.Delay(500);
        }
        var writeTime = clock.Elapsed;

        var target = await source.CurrentVersionAsync();
        while (await WatermarkAsync(host) is not { } mark || mark < target)
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(15)) Assert.Fail($"not caught up: watermark {await WatermarkAsync(host)}, source {target}");
            await Task.Delay(500);
        }
        var catchUp = clock.Elapsed;
        await Task.Delay(3000);
        await host.StopAsync();

        var delivered = new Dictionary<string, List<long>>
        {
            ["HTTP:http"] = http.Accepted.ToList(),
            ["FILE"] = Directory.GetFiles(exportDir, "*.json", SearchOption.AllDirectories).SelectMany(f => Ids(File.ReadAllText(f))).ToList(),
            ["MQ:rabbit"] = await DrainRabbitAsync(rabbitQueue),
            ["MQ:kafka"] = DrainKafka(topic),
            ["MQ:sqs"] = await DrainSqsAsync(sqsQueue),
            ["MQ:servicebus"] = await DrainServiceBusAsync(),
            ["MQ:eventhubs"] = await DrainEventHubsAsync(started),
        };
        var deadLetters = await DeadLettersAsync();

        var expected = Rows;
        output.WriteLine($"{provider}: {Rows:N0} inserts, {updates:N0} updates, written in {writeTime.TotalSeconds:F1}s, caught up after {catchUp.TotalSeconds:F1}s");
        output.WriteLine($"dead letters: {deadLetters.Count} ({string.Join(", ", deadLetters.SelectMany(d => d.Targets ?? ["<all>"]).GroupBy(t => t).Select(g => $"{g.Key} x{g.Count()}"))})");

        foreach (var (name, ids) in delivered)
        {
            var distinct = ids.ToHashSet();
            var parked = deadLetters.Where(d => d.Targets is null || d.Targets.Contains(name)).SelectMany(d => d.Ids).ToHashSet();
            var missing = Enumerable.Range(1, expected).Select(i => (long)i).Where(id => !distinct.Contains(id) && !parked.Contains(id)).ToList();
            output.WriteLine($"{name,-14} delivered {distinct.Count,8:N0} distinct, {ids.Count - distinct.Count,7:N0} duplicate, {parked.Count,8:N0} in dead letters, {missing.Count,6:N0} lost");
            Assert.True(missing.Count == 0, $"{name} lost {missing.Count} rows, first {string.Join(",", missing.Take(5))}");
        }

        // Updates: the final value of every row reached HTTP.
        var finalValues = await source.ValuesAsync();
        var httpRows = http.AcceptedRows.ToHashSet();
        Assert.All(finalValues, row => Assert.Contains((row.Key, row.Value), httpRows));
        Assert.Empty(deadLetters);
    }

    [LoadTheory, MemberData(nameof(Providers))]
    public async Task Drain_DeadLettersFromAnOutage_ReplayEveryRow(string provider)
    {
        var rows = Rows / 2;
        await using var source = await ParitySource.CreateAsync(provider, transactional: true);
        await using var http = new HttpSink { Status = 503 };
        await using var host = new StressHost();
        host.WriteEnvJson(Env, $$"""
            {
              "Provider": "{{provider}}",
              "ConnectionStrings": { "src": "{{source.Connection}}" },
              "ChangeTracking": {
                "PollingIntervalSeconds": 1, "ExportToFile": false, "ExportToApi": true, "RetryCount": 0,
                "TrackingObjects": [{ "Name": "items", "Database": "src", "TableName": "{{source.Table}}", "StoredProcedureName": "{{source.Procedure}}", "InitialSyncMode": "Full" }],
                "ApiEndpoints": [{ "Key": "http", "Url": "{{http.Url}}" }]
              }
            }
            """);
        await host.StartAsync();

        for (var written = 0; written < rows; written += Burst)
        {
            await source.BulkInsertAsync(Math.Min(Burst, rows - written));
            await Task.Delay(500);
        }
        var target = await source.CurrentVersionAsync();
        while (await WatermarkAsync(host) is not { } mark || mark < target) await Task.Delay(500);
        var parked = (await DeadLettersAsync()).Count;

        http.Status = 200;
        var clock = Stopwatch.StartNew();
        int replayed = 0;
        while ((await host.DeadLetters.GetDueForReplayAsync(int.MaxValue, 25)) is { Count: > 0 } due)
        {
            foreach (var record in due) await host.Replayer.ReplayAsync(record, CancellationToken.None);
            replayed += due.Count;
            if (clock.Elapsed > TimeSpan.FromMinutes(10)) Assert.Fail($"drain incomplete after {replayed} replays");
        }

        output.WriteLine($"{provider}: {rows:N0} rows in {parked} dead letters, drained in {clock.Elapsed.TotalSeconds:F1}s ({rows / clock.Elapsed.TotalSeconds:N0} rows/s)");
        Assert.Empty(await DeadLettersAsync());
        Assert.Equal(Enumerable.Range(1, rows).Select(i => (long)i), http.Accepted.Distinct().Order());
    }

    private static async Task<List<long>> DrainServiceBusAsync()
    {
        var ids = new List<long>();
        await using var client = new ServiceBusClient(ServiceBus);
        await using var receiver = client.CreateReceiver("trignis-queue", new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
        for (var idle = 0; idle < 2;)
        {
            var batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(2));
            if (batch.Count == 0) { idle++; continue; }
            idle = 0;
            foreach (var message in batch) ids.AddRange(Ids(Decode(message.Body.ToString())));
        }
        return ids;
    }

    private static async Task<List<long>> DrainEventHubsAsync(DateTimeOffset since)
    {
        var ids = new List<long>();
        await using var consumer = new EventHubConsumerClient(EventHubConsumerClient.DefaultConsumerGroupName, EventHubs, "trignis-hub");
        var options = new ReadEventOptions { MaximumWaitTime = TimeSpan.FromSeconds(3) };
        var idle = 0;
        await foreach (var item in consumer.ReadEventsFromPartitionAsync("0", Azure.Messaging.EventHubs.Consumer.EventPosition.FromEnqueuedTime(since), options))
        {
            // The emulator delivers recent events late.
            if (item.Data is null) { if (++idle == 3) break; continue; }
            idle = 0;
            ids.AddRange(Ids(item.Data.EventBody.ToString()));
        }
        return ids;
    }

    private static IEnumerable<long> Ids(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetInt64()).ToList();
    }

    // Oversized queue messages are sent as base64 gzip.
    private static string Decode(string body)
    {
        if (body.StartsWith('[')) return body;
        using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(Convert.FromBase64String(body)), System.IO.Compression.CompressionMode.Decompress);
        return new StreamReader(gzip).ReadToEnd();
    }

    private static async Task<long?> WatermarkAsync(StressHost host)
    {
        await using var conn = new SqliteConnection($"Data Source={host.StateDb}");
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LastVersion FROM LastVersions WHERE EnvironmentName = 'load' AND ObjectName = 'items'";
        return await cmd.ExecuteScalarAsync() as long?;
    }

    private sealed record DeadLetter(IReadOnlyList<string>? Targets, IReadOnlyList<long> Ids);

    private static async Task<List<DeadLetter>> DeadLettersAsync()
    {
        var result = new List<DeadLetter>();
        if (!File.Exists("sinkhole.db")) return result;
        await using var conn = new SqliteConnection("Data Source=sinkhole.db");
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FailedTargets, Data FROM DeadLetters";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new DeadLetter(
                reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<List<string>>(reader.GetString(0)),
                Ids(reader.GetString(1)).ToList()));
        return result;
    }

    private static async Task<List<long>> DrainRabbitAsync(string queue)
    {
        var ids = new List<long>();
        var factory = new ConnectionFactory { HostName = "localhost", UserName = "trignis", Password = "trignis" };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclarePassiveAsync(queue);
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
            ids.AddRange(Ids(System.Text.Encoding.UTF8.GetString(message.Body.Span)));
        return ids;
    }

    private static async Task<string> CreateKafkaTopicAsync(string name)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = "localhost:9092" }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = name, NumPartitions = 1, ReplicationFactor = 1 }]);
        return name;
    }

    private static List<long> DrainKafka(string topic)
    {
        var ids = new List<long>();
        using var consumer = new ConsumerBuilder<Ignore, string>(new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = $"load-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            FetchMaxBytes = 64 * 1024 * 1024,
        }).Build();
        consumer.Subscribe(topic);
        var idle = 0;
        while (idle < 3)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(2));
            if (result is null) { idle++; continue; }
            idle = 0;
            ids.AddRange(Ids(result.Message.Value));
        }
        consumer.Close();
        return ids;
    }

    private static async Task<string> CreateSqsQueueAsync(string name)
    {
        using var sqs = new AmazonSQSClient("x", "x", new AmazonSQSConfig { ServiceURL = SqsUrl });
        return (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
    }

    private static async Task<List<long>> DrainSqsAsync(string queueUrl)
    {
        var ids = new List<long>();
        using var sqs = new AmazonSQSClient("x", "x", new AmazonSQSConfig { ServiceURL = SqsUrl });
        while (true)
        {
            var batch = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            if (batch.Messages is not { Count: > 0 }) return ids;
            foreach (var message in batch.Messages) ids.AddRange(Ids(Decode(message.Body)));
            await sqs.DeleteMessageBatchAsync(queueUrl, batch.Messages.Select((m, i) => new DeleteMessageBatchRequestEntry(i.ToString(), m.ReceiptHandle)).ToList());
        }
    }
}
