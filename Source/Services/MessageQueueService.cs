using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Trignis.MicrosoftSQL.Models;
using RabbitMQ.Client;
using Azure.Messaging.ServiceBus;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace Trignis.MicrosoftSQL.Services;

public class MessageQueueService
{
    private readonly ILogger<MessageQueueService> _logger;

    public MessageQueueService(ILogger<MessageQueueService> logger)
    {
        _logger = logger;
    }

    public async Task SendToQueueAsync(ApiEndpoint endpoint, JsonElement data)
    {
        if (endpoint.MessageQueue == null)
        {
            throw new InvalidOperationException("MessageQueue configuration is required");
        }

        var messageBody = JsonSerializer.Serialize(data);

        switch (endpoint.MessageQueueType?.ToLower())
        {
            case "rabbitmq":
                await SendToRabbitMQAsync(endpoint.MessageQueue, messageBody);
                break;
            case "azureservicebus":
                await SendToAzureServiceBusAsync(endpoint.MessageQueue, messageBody);
                break;
            case "awssqs":
                await SendToAwsSqsAsync(endpoint.MessageQueue, messageBody);
                break;
            default:
                throw new InvalidOperationException($"Unsupported message queue type: {endpoint.MessageQueueType}");
        }
    }

    private async Task SendToRabbitMQAsync(MessageQueueConfig config, string message)
    {
        if (string.IsNullOrEmpty(config.HostName))
        {
            throw new InvalidOperationException("RabbitMQ HostName is required");
        }

        IConnection? connection = null;
        IChannel? channel = null;

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = config.HostName,
                Port = config.Port,
                VirtualHost = config.VirtualHost ?? "/",
                UserName = config.Username ?? "guest",
                Password = config.Password ?? "guest"
            };

            connection = await factory.CreateConnectionAsync();
            channel = await connection.CreateChannelAsync();

            var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(message));
            var properties = new BasicProperties();
            if (!string.IsNullOrEmpty(config.Exchange))
            {
                var exchange = config.Exchange;
                var routingKey = config.RoutingKey ?? ""; // Empty string for fanout exchanges

                await channel.BasicPublishAsync(
                    exchange: exchange,
                    routingKey: routingKey,
                    mandatory: false,
                    basicProperties: properties,
                    body: body);

                _logger.LogInformation($" └─ Exported changes to RabbitMQ exchange '{exchange}'" +
                    (string.IsNullOrEmpty(routingKey) ? "" : $" with routing key '{routingKey}'"));
            }
            // Scenario 2: Publishing directly to a queue (default exchange)
            else if (!string.IsNullOrEmpty(config.QueueName))
            {
                var queueName = config.QueueName;

                // Declare queue to ensure it exists
                await channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                // Publish to default exchange ("") with queue name as routing key
                await channel.BasicPublishAsync(
                    exchange: "",
                    routingKey: queueName,
                    mandatory: false,
                    basicProperties: properties,
                    body: body);

                _logger.LogInformation($" └─ Exported changes to RabbitMQ queue '{queueName}'");
            }
            else
            {
                throw new InvalidOperationException("RabbitMQ requires either Exchange or QueueName to be configured");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send message to RabbitMQ (Host: {config.HostName}:{config.Port})");
            throw new InvalidOperationException($"RabbitMQ publish failed: {ex.Message}", ex);
        }
        finally
        {
            // Ensure resources are properly disposed
            if (channel != null)
            {
                try
                {
                    await channel.CloseAsync();
                    channel.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing RabbitMQ channel");
                }
            }

            if (connection != null)
            {
                try
                {
                    await connection.CloseAsync();
                    connection.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing RabbitMQ connection");
                }
            }
        }
    }

    private async Task SendToAzureServiceBusAsync(MessageQueueConfig config, string message)
    {
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            throw new InvalidOperationException("Azure Service Bus ConnectionString is required");
        }

        ServiceBusClient? client = null;
        ServiceBusSender? sender = null;

        try
        {
            client = new ServiceBusClient(config.ConnectionString);
            var queueOrTopic = config.QueueName ?? config.TopicName;

            if (string.IsNullOrEmpty(queueOrTopic))
            {
                throw new InvalidOperationException("Azure Service Bus requires either QueueName or TopicName to be configured");
            }

            sender = client.CreateSender(queueOrTopic);
            var busMessage = new ServiceBusMessage(message);
            await sender.SendMessageAsync(busMessage);

            _logger.LogInformation($" └─ Exported changes to Azure Service Bus {(config.QueueName != null ? "queue" : "topic")} '{queueOrTopic}'");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send message to Azure Service Bus");
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

    private async Task SendToAwsSqsAsync(MessageQueueConfig config, string message)
    {
        if (string.IsNullOrEmpty(config.QueueUrl))
        {
            throw new InvalidOperationException("AWS SQS QueueUrl is required");
        }

        try
        {
            var sqsConfig = new AmazonSQSConfig();
            if (!string.IsNullOrEmpty(config.Region))
            {
                sqsConfig.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(config.Region);
            }

            IAmazonSQS client;
            if (!string.IsNullOrEmpty(config.AccessKeyId) && !string.IsNullOrEmpty(config.SecretAccessKey))
            {
                client = new AmazonSQSClient(config.AccessKeyId, config.SecretAccessKey, sqsConfig);
            }
            else
            {
                // Use default credentials (IAM role, environment variables, etc.)
                client = new AmazonSQSClient(sqsConfig);
            }

            using (client)
            {
                var request = new SendMessageRequest
                {
                    QueueUrl = config.QueueUrl,
                    MessageBody = message
                };

                var response = await client.SendMessageAsync(request);
                _logger.LogInformation($" └─ Exported changes to AWS SQS queue (MessageId: {response.MessageId})");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send message to AWS SQS (Queue: {config.QueueUrl})");
            throw new InvalidOperationException($"AWS SQS publish failed: {ex.Message}", ex);
        }
    }
}