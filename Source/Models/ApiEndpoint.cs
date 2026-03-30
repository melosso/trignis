using System.Collections.Generic;

namespace Trignis.MicrosoftSQL.Models;

public record class ApiEndpoint
{
    public string? Key { get; init; }
    public string? Url { get; init; }
    public ApiAuth? Auth { get; init; }
    public string? EnvironmentFile { get; init; }
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; init; }
    public bool EnableCompression { get; init; } = false;
    public string? MessageQueueType { get; init; } // "RabbitMQ", "AzureServiceBus", "AWSSQS"
    public MessageQueueConfig? MessageQueue { get; init; }
}

public record class ApiAuth
{
    public string? Type { get; init; } // "Bearer", "Basic", "ApiKey", "OAuth2ClientCredentials"
    public string? Token { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? ApiKey { get; init; }
    public string? HeaderName { get; init; }

    // OAuth 2.0 Client Credentials
    public string? TokenEndpoint { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? Scope { get; init; }
    public int? TokenExpirationSeconds { get; init; }
}

public record class MessageQueueConfig
{
    // RabbitMQ
    public string? HostName { get; init; }
    public int Port { get; init; } = 5672;
    public string? VirtualHost { get; init; } = "/";
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? QueueName { get; init; }
    public string? Exchange { get; init; }
    public string? RoutingKey { get; init; }

    // Azure Service Bus
    public string? ConnectionString { get; init; }
    public string? TopicName { get; init; }

    // AWS SQS
    public string? QueueUrl { get; init; }
    public string? Region { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretAccessKey { get; init; }

    // Azure Event Hubs (reuses ConnectionString)
    public string? EventHubName { get; init; }

    // Kafka
    public string? BootstrapServers { get; init; }
    public string? Topic { get; init; }
    public string? SecurityProtocol { get; init; }  // PLAINTEXT, SSL, SASL_PLAINTEXT, SASL_SSL
    public string? SaslMechanism { get; init; }      // PLAIN, SCRAM-SHA-256, SCRAM-SHA-512
}
