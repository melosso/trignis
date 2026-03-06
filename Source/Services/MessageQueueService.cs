using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Trignis.MicrosoftSQL.Models;
using RabbitMQ.Client;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Amazon.SQS;
using Amazon.SQS.Model;
using Confluent.Kafka;
using Polly;
using Polly.CircuitBreaker;
using System.Collections.Concurrent;
using System.Threading;
using System.IO.Compression;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Trignis.MicrosoftSQL.Services;

public class MessageQueueService : IDisposable
{
    private readonly ILogger<MessageQueueService> _logger;
    private readonly ConcurrentDictionary<string, IConnection> _rabbitConnections = new();
    private readonly ConcurrentDictionary<string, IProducer<Null, string>> _kafkaProducers = new();
    private readonly ConcurrentDictionary<string, AsyncCircuitBreakerPolicy> _circuitBreakers = new();
    private readonly DeadLetterQueueMonitor _dlqMonitor;

    // Message size limits
    private const int RABBITMQ_MAX_SIZE = 128 * 1024 * 1024; // 128MB
    private const int AZURE_SB_STANDARD_MAX = 256 * 1024; // 256KB
    private const int AWS_SQS_MAX_SIZE = 256 * 1024; // 256KB
    private const int AZURE_EH_MAX_SIZE = 1 * 1024 * 1024; // 1MB
    private const int KAFKA_DEFAULT_MAX_SIZE = 1 * 1024 * 1024; // 1MB (Kafka server default)
    private const int BATCH_SIZE = 10; // For SQS batching
    private const int COMPRESSION_THRESHOLD = 1024; // Compress messages > 1KB

    public MessageQueueService(
        ILogger<MessageQueueService> logger,
        DeadLetterQueueMonitor dlqMonitor)
    {
        _logger = logger;
        _dlqMonitor = dlqMonitor;
    }

    public async Task SendToQueueAsync(ApiEndpoint endpoint, JsonElement data, CancellationToken cancellationToken = default)
    {
        if (endpoint.MessageQueue == null)
        {
            throw new InvalidOperationException("MessageQueue configuration is required");
        }

        var correlationId = Guid.NewGuid().ToString();
        var messageBody = JsonSerializer.Serialize(data);
        var messageSizeBytes = Encoding.UTF8.GetByteCount(messageBody);

        _logger.LogDebug("Sending message to {QueueType} (CorrelationId: {CorrelationId}, Size: {Size} bytes)", 
            endpoint.MessageQueueType, correlationId, messageSizeBytes);

        // Get or create circuit breaker for this endpoint
        var circuitBreaker = GetCircuitBreaker(endpoint.Key ?? "default");

        try
        {
            await circuitBreaker.ExecuteAsync(async (ct) =>
            {
                switch (endpoint.MessageQueueType?.ToLower())
                {
                    case "rabbitmq":
                        ValidateMessageSize(messageSizeBytes, RABBITMQ_MAX_SIZE, "RabbitMQ");
                        await SendToRabbitMQAsync(endpoint.MessageQueue, messageBody, correlationId, ct);
                        break;
                    case "azureservicebus":
                        // Try compression if message is too large
                        if (messageSizeBytes > AZURE_SB_STANDARD_MAX)
                        {
                            messageBody = await CompressMessageAsync(messageBody);
                            messageSizeBytes = Encoding.UTF8.GetByteCount(messageBody);
                            ValidateMessageSize(messageSizeBytes, AZURE_SB_STANDARD_MAX, "Azure Service Bus");
                        }
                        await SendToAzureServiceBusAsync(endpoint.MessageQueue, messageBody, correlationId, messageSizeBytes > COMPRESSION_THRESHOLD, ct);
                        break;
                    case "awssqs":
                        // Try compression if message is too large
                        if (messageSizeBytes > AWS_SQS_MAX_SIZE)
                        {
                            messageBody = await CompressMessageAsync(messageBody);
                            messageSizeBytes = Encoding.UTF8.GetByteCount(messageBody);
                            ValidateMessageSize(messageSizeBytes, AWS_SQS_MAX_SIZE, "AWS SQS");
                        }
                        await SendToAwsSqsAsync(endpoint.MessageQueue, messageBody, correlationId, messageSizeBytes > COMPRESSION_THRESHOLD, ct);
                        break;
                    case "azureeventhubs":
                        ValidateMessageSize(messageSizeBytes, AZURE_EH_MAX_SIZE, "Azure Event Hubs");
                        await SendToAzureEventHubsAsync(endpoint.MessageQueue, messageBody, correlationId, ct);
                        break;
                    case "kafka":
                        ValidateMessageSize(messageSizeBytes, KAFKA_DEFAULT_MAX_SIZE, "Kafka");
                        await SendToKafkaAsync(endpoint.MessageQueue, messageBody, correlationId, ct);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported message queue type: {endpoint.MessageQueueType}");
                }
            }, cancellationToken);

            _logger.LogDebug("Message sent successfully (CorrelationId: {CorrelationId}, Queue: {QueueType})", 
                correlationId, endpoint.MessageQueueType);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogError(ex, "Circuit breaker open for endpoint '{EndpointKey}' (CorrelationId: {CorrelationId})", 
                endpoint.Key, correlationId);
            throw new InvalidOperationException($"Message queue service is temporarily unavailable: {ex.Message}", ex);
        }
    }

    private AsyncCircuitBreakerPolicy GetCircuitBreaker(string key)
    {
        return _circuitBreakers.GetOrAdd(key, _ =>
            Policy
                .Handle<Exception>()
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromMinutes(1),
                    onBreak: (exception, duration) =>
                    {
                        _logger.LogWarning("Circuit breaker opened for '{Key}' for {Duration}s due to: {Error}", 
                            key, duration.TotalSeconds, exception.Message);
                    },
                    onReset: () =>
                    {
                        _logger.LogInformation("Circuit breaker reset for '{Key}'", key);
                    }
                )
        );
    }

    private void ValidateMessageSize(int actualSize, int maxSize, string serviceName)
    {
        if (actualSize > maxSize)
        {
            throw new InvalidOperationException(
                $"Message size ({actualSize} bytes) exceeds {serviceName} limit ({maxSize} bytes) even after compression. " +
                $"Consider implementing message batching or splitting.");
        }
    }

    private async Task<string> CompressMessageAsync(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        using var outputStream = new MemoryStream();
        using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Optimal))
        {
            await gzipStream.WriteAsync(bytes, 0, bytes.Length);
        }
        var compressed = Convert.ToBase64String(outputStream.ToArray());
        
        _logger.LogDebug("Compressed message from {Original} to {Compressed} bytes ({Ratio:P2} reduction)", 
            bytes.Length, outputStream.Length, 1.0 - (double)outputStream.Length / bytes.Length);
        
        return compressed;
    }

    private async Task SendToRabbitMQAsync(MessageQueueConfig config, string message, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.HostName))
        {
            throw new InvalidOperationException("RabbitMQ HostName is required");
        }

        var connectionKey = $"{config.HostName}:{config.Port}:{config.VirtualHost}";
        IConnection connection;

        // Reuse connection (connection pooling)
        if (!_rabbitConnections.TryGetValue(connectionKey, out connection!) || !connection.IsOpen)
        {
            var factory = new ConnectionFactory
            {
                HostName = config.HostName,
                Port = config.Port,
                VirtualHost = config.VirtualHost ?? "/",
                UserName = config.Username ?? "guest",
                Password = config.Password ?? "guest",
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                RequestedConnectionTimeout = TimeSpan.FromSeconds(30),
                RequestedHeartbeat = TimeSpan.FromSeconds(60)
            };

            connection = await factory.CreateConnectionAsync(cancellationToken);
            _rabbitConnections[connectionKey] = connection;
            _logger.LogDebug("Created RabbitMQ connection to {Host}:{Port}{VHost}", 
                            config.HostName, config.Port, config.VirtualHost ?? "/");        }

        IChannel? channel = null;
        try
        {
            channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(message));
            var properties = new BasicProperties
            {
                Persistent = true,
                DeliveryMode = DeliveryModes.Persistent,
                ContentType = "application/json",
                Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                CorrelationId = correlationId,
                MessageId = Guid.NewGuid().ToString(),
                Headers = new Dictionary<string, object?>
                {
                    ["x-source"] = "trignis-change-tracking",
                    ["x-correlation-id"] = correlationId
                }
            };

            if (!string.IsNullOrEmpty(config.Exchange))
            {
                await channel.ExchangeDeclareAsync(
                    exchange: config.Exchange,
                    type: "topic",
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: cancellationToken);

                await channel.BasicPublishAsync(
                    exchange: config.Exchange,
                    routingKey: config.RoutingKey ?? "",
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: cancellationToken);

                _logger.LogDebug("Published to RabbitMQ exchange '{Exchange}' with key '{Key}' (CorrelationId: {CorrelationId})", 
                    config.Exchange, config.RoutingKey ?? "", correlationId);
            }
            else if (!string.IsNullOrEmpty(config.QueueName))
            {
                await channel.QueueDeclareAsync(
                    queue: config.QueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: cancellationToken);

                await channel.BasicPublishAsync(
                    exchange: "",
                    routingKey: config.QueueName,
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: cancellationToken);

                _logger.LogDebug("Published to RabbitMQ queue '{Queue}' (CorrelationId: {CorrelationId})", 
                    config.QueueName, correlationId);
            }
            else
            {
                throw new InvalidOperationException("RabbitMQ requires either Exchange or QueueName");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("RabbitMQ publish cancelled (CorrelationId: {CorrelationId})", correlationId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send to RabbitMQ (Host: {Host}, CorrelationId: {CorrelationId})", 
                config.HostName, correlationId);
            
            _rabbitConnections.TryRemove(connectionKey, out _);
            throw new InvalidOperationException($"RabbitMQ publish failed: {ex.Message}", ex);
        }
        finally
        {
            if (channel != null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await channel.CloseAsync(cancellationToken: cts.Token);
                    channel.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing RabbitMQ channel");
                }
            }
        }
    }

    private async Task SendToAzureServiceBusAsync(MessageQueueConfig config, string message, string correlationId, bool isCompressed, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            throw new InvalidOperationException("Azure Service Bus ConnectionString is required");
        }

        ServiceBusClient? client = null;
        ServiceBusSender? sender = null;

        try
        {
            var clientOptions = new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    Mode = ServiceBusRetryMode.Exponential,
                    MaxRetries = 3,
                    Delay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(60)
                }
            };

            client = new ServiceBusClient(config.ConnectionString, clientOptions);
            var queueOrTopic = config.QueueName ?? config.TopicName;

            if (string.IsNullOrEmpty(queueOrTopic))
            {
                throw new InvalidOperationException("Azure Service Bus requires QueueName or TopicName");
            }

            sender = client.CreateSender(queueOrTopic);
            
            var busMessage = new ServiceBusMessage(message)
            {
                ContentType = isCompressed ? "application/json+gzip" : "application/json",
                MessageId = Guid.NewGuid().ToString(),
                CorrelationId = correlationId,
                TimeToLive = TimeSpan.FromHours(24),
                ApplicationProperties =
                {
                    ["Source"] = "trignis-change-tracking",
                    ["CorrelationId"] = correlationId,
                    ["Compressed"] = isCompressed
                }
            };

            await sender.SendMessageAsync(busMessage, cancellationToken);

            _logger.LogDebug("Sent to Azure Service Bus {Type} '{Name}' (CorrelationId: {CorrelationId})",
                queueOrTopic == "queue" ? "queue" : "topic", queueOrTopic, correlationId);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageSizeExceeded)
        {
            _logger.LogError(ex, "Message exceeds Azure Service Bus size limit (CorrelationId: {CorrelationId})", correlationId);
            throw new InvalidOperationException(
                "Message too large for Azure Service Bus. Consider Premium tier or message splitting.", ex);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Azure Service Bus send cancelled (CorrelationId: {CorrelationId})", correlationId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send to Azure Service Bus (CorrelationId: {CorrelationId})", correlationId);
            throw new InvalidOperationException($"Azure Service Bus publish failed: {ex.Message}", ex);
        }
        finally
        {
            if (sender != null)
            {
                try
                {
                    await sender.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing Azure Service Bus sender");
                }
            }

            if (client != null)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing Azure Service Bus client");
                }
            }
        }
    }

    private async Task SendToAwsSqsAsync(MessageQueueConfig config, string message, string correlationId, bool isCompressed, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.QueueUrl))
        {
            throw new InvalidOperationException("AWS SQS QueueUrl is required");
        }

        IAmazonSQS? client = null;

        try
        {
            var sqsConfig = new AmazonSQSConfig
            {
                MaxErrorRetry = 3,
                Timeout = TimeSpan.FromSeconds(30)
            };

            if (!string.IsNullOrEmpty(config.Region))
            {
                var region = Amazon.RegionEndpoint.GetBySystemName(config.Region);
                if (region == null || region.DisplayName == "Unknown")
                {
                    throw new InvalidOperationException($"Invalid AWS region: {config.Region}");
                }
                sqsConfig.RegionEndpoint = region;
            }

            if (!string.IsNullOrEmpty(config.AccessKeyId) && !string.IsNullOrEmpty(config.SecretAccessKey))
            {
                client = new AmazonSQSClient(config.AccessKeyId, config.SecretAccessKey, sqsConfig);
                _logger.LogDebug("Using explicit AWS credentials");
            }
            else
            {
                client = new AmazonSQSClient(sqsConfig);
                _logger.LogDebug("Using default AWS credentials chain");
            }

            var request = new SendMessageRequest
            {
                QueueUrl = config.QueueUrl,
                MessageBody = message,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["ContentType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = isCompressed ? "application/json+gzip" : "application/json"
                    },
                    ["CorrelationId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = correlationId
                    },
                    ["Source"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "trignis-change-tracking"
                    },
                    ["Timestamp"] = new MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
                    },
                    ["Compressed"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = isCompressed.ToString()
                    }
                }
            };

            var response = await client.SendMessageAsync(request, cancellationToken);
            
            if ((int)response.HttpStatusCode < 200 || (int)response.HttpStatusCode >= 300)
            {
                throw new InvalidOperationException($"AWS SQS returned status code: {response.HttpStatusCode}");
            }

            _logger.LogDebug("Sent to AWS SQS (MessageId: {MessageId}, CorrelationId: {CorrelationId})",
                response.MessageId, correlationId);
        }
        catch (AmazonSQSException ex) when (ex.ErrorCode == "InvalidMessageContents")
        {
            _logger.LogError(ex, "Invalid message contents for AWS SQS (CorrelationId: {CorrelationId})", correlationId);
            throw new InvalidOperationException("Message contains invalid characters for SQS", ex);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AWS SQS send cancelled (CorrelationId: {CorrelationId})", correlationId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send to AWS SQS (Queue: {QueueUrl}, CorrelationId: {CorrelationId})", 
                config.QueueUrl, correlationId);
            throw new InvalidOperationException($"AWS SQS publish failed: {ex.Message}", ex);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task SendToAzureEventHubsAsync(MessageQueueConfig config, string message, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            throw new InvalidOperationException("Azure Event Hubs ConnectionString is required");
        }
        if (string.IsNullOrEmpty(config.EventHubName))
        {
            throw new InvalidOperationException("Azure Event Hubs EventHubName is required");
        }

        await using var producer = new EventHubProducerClient(config.ConnectionString, config.EventHubName);

        try
        {
            using var batch = await producer.CreateBatchAsync(cancellationToken);

            var eventData = new EventData(Encoding.UTF8.GetBytes(message));
            eventData.Properties["CorrelationId"] = correlationId;
            eventData.Properties["Source"] = "trignis-change-tracking";
            eventData.ContentType = "application/json";

            if (!batch.TryAdd(eventData))
            {
                throw new InvalidOperationException(
                    "Message too large for Azure Event Hubs batch. Consider splitting the payload.");
            }

            await producer.SendAsync(batch, cancellationToken);

            _logger.LogDebug("Sent to Azure Event Hubs '{EventHub}' (CorrelationId: {CorrelationId})",
                config.EventHubName, correlationId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Azure Event Hubs send cancelled (CorrelationId: {CorrelationId})", correlationId);
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to send to Azure Event Hubs '{EventHub}' (CorrelationId: {CorrelationId})",
                config.EventHubName, correlationId);
            throw new InvalidOperationException($"Azure Event Hubs publish failed: {ex.Message}", ex);
        }
    }

    private async Task SendToKafkaAsync(MessageQueueConfig config, string message, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.BootstrapServers))
        {
            throw new InvalidOperationException("Kafka BootstrapServers is required");
        }
        if (string.IsNullOrEmpty(config.Topic))
        {
            throw new InvalidOperationException("Kafka Topic is required");
        }

        var producerKey = $"{config.BootstrapServers}:{config.Topic}:{config.Username}";

        var producer = _kafkaProducers.GetOrAdd(producerKey, _ =>
        {
            var producerConfig = new ProducerConfig
            {
                BootstrapServers = config.BootstrapServers,
                MessageTimeoutMs = 30000,
                Acks = Acks.All
            };

            if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
            {
                producerConfig.SaslMechanism = config.SaslMechanism?.ToUpper() switch
                {
                    "SCRAM-SHA-256" => SaslMechanism.ScramSha256,
                    "SCRAM-SHA-512" => SaslMechanism.ScramSha512,
                    _ => SaslMechanism.Plain
                };
                producerConfig.SecurityProtocol = config.SecurityProtocol?.ToUpper() switch
                {
                    "SSL" => Confluent.Kafka.SecurityProtocol.Ssl,
                    "SASL_PLAINTEXT" => Confluent.Kafka.SecurityProtocol.SaslPlaintext,
                    _ => Confluent.Kafka.SecurityProtocol.SaslSsl
                };
                producerConfig.SaslUsername = config.Username;
                producerConfig.SaslPassword = config.Password;
            }
            else if (config.SecurityProtocol?.Equals("SSL", StringComparison.OrdinalIgnoreCase) == true)
            {
                producerConfig.SecurityProtocol = Confluent.Kafka.SecurityProtocol.Ssl;
            }

            _logger.LogDebug("Created Kafka producer for '{BootstrapServers}'", config.BootstrapServers);
            return new ProducerBuilder<Null, string>(producerConfig).Build();
        });

        try
        {
            var kafkaMessage = new Message<Null, string>
            {
                Value = message,
                Headers = new Confluent.Kafka.Headers
                {
                    { "content-type", Encoding.UTF8.GetBytes("application/json") },
                    { "correlation-id", Encoding.UTF8.GetBytes(correlationId) },
                    { "source", Encoding.UTF8.GetBytes("trignis-change-tracking") }
                }
            };

            var result = await producer.ProduceAsync(config.Topic, kafkaMessage, cancellationToken);

            _logger.LogDebug("Sent to Kafka topic '{Topic}' partition {Partition} offset {Offset} (CorrelationId: {CorrelationId})",
                config.Topic, result.Partition.Value, result.Offset.Value, correlationId);
        }
        catch (ProduceException<Null, string> ex)
        {
            _logger.LogError(ex, "Failed to send to Kafka topic '{Topic}' (CorrelationId: {CorrelationId})",
                config.Topic, correlationId);
            _kafkaProducers.TryRemove(producerKey, out _);
            producer.Dispose();
            throw new InvalidOperationException($"Kafka publish failed: {ex.Error.Reason}", ex);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Kafka send cancelled (CorrelationId: {CorrelationId})", correlationId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send to Kafka topic '{Topic}' (CorrelationId: {CorrelationId})",
                config.Topic, correlationId);
            _kafkaProducers.TryRemove(producerKey, out _);
            producer.Dispose();
            throw new InvalidOperationException($"Kafka publish failed: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _rabbitConnections)
        {
            try
            {
                if (kvp.Value.IsOpen)
                {
                    kvp.Value.CloseAsync().GetAwaiter().GetResult();
                }
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing RabbitMQ connection");
            }
        }
        _rabbitConnections.Clear();

        foreach (var kvp in _kafkaProducers)
        {
            try
            {
                kvp.Value.Flush(TimeSpan.FromSeconds(5));
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing Kafka producer");
            }
        }
        _kafkaProducers.Clear();
    }
}