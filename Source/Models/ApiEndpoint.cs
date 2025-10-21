using System.Collections.Generic;

namespace Trignis.MicrosoftSQL.Models;

public class ApiEndpoint
{
    public string? Key { get; set; }
    public string? Url { get; set; }
    public ApiAuth? Auth { get; set; }
    public string? EnvironmentFile { get; set; }
    public Dictionary<string, string>? CustomHeaders { get; set; }
    public bool EnableCompression { get; set; } = false;
    public string? MessageQueueType { get; set; } // "RabbitMQ", "AzureServiceBus", "AWSSQS"
    public MessageQueueConfig? MessageQueue { get; set; }
}

public class ApiAuth
{
    public string? Type { get; set; } // "Bearer", "Basic", "ApiKey", "OAuth2ClientCredentials"
    public string? Token { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string? HeaderName { get; set; }
    
    // OAuth 2.0 Client Credentials
    public string? TokenEndpoint { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Scope { get; set; }
    public int? TokenExpirationSeconds { get; set; }
}

public class MessageQueueConfig
{
    // RabbitMQ
    public string? HostName { get; set; }
    public int Port { get; set; } = 5672;
    public string? VirtualHost { get; set; } = "/";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? QueueName { get; set; }
    public string? Exchange { get; set; }
    public string? RoutingKey { get; set; }
    
    // Azure Service Bus
    public string? ConnectionString { get; set; }
    public string? TopicName { get; set; }
    
    // AWS SQS
    public string? QueueUrl { get; set; }
    public string? Region { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
}