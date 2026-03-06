using System;
using System.Collections.Generic;
using Trignis.MicrosoftSQL.Helpers;
using Trignis.Tests.Helpers;
using Xunit;

namespace Trignis.Tests;

/// <summary>
/// Tests for ConfigurationValidator — covering all message queue types, HTTP endpoints,
/// and core environment/tracking-object validation.
/// </summary>
public class ConfigurationValidatorTests
{
    // Helper: builds config with a minimal valid environment + caller-supplied endpoint entries.
    private static Microsoft.Extensions.Configuration.IConfiguration WithEndpoint(
        Dictionary<string, string?> endpointEntries)
    {
        var entries = TestConfigurationBuilder.MinimalValidEnvironment();
        foreach (var kv in endpointEntries)
            entries[kv.Key] = kv.Value;
        return TestConfigurationBuilder.Build(entries);
    }

    private static string EP => TestConfigurationBuilder.EP;

    // -------------------------------------------------------------------------
    // Environment-level
    // -------------------------------------------------------------------------

    [Fact]
    public void NoEnvironments_Throws()
    {
        var config = TestConfigurationBuilder.Build(new Dictionary<string, string?>());
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void ValidEnvironmentNoEndpoints_Passes()
    {
        // ExportToApi not set → defaults to false → no endpoint validation needed
        var entries = new Dictionary<string, string?>
        {
            ["ChangeTracking:Environments:0:Name"] = "TestEnv",
            ["ChangeTracking:Environments:0:ConnectionStrings:TestDB"] = "Server=localhost;",
            ["ChangeTracking:Environments:0:ChangeTracking:TrackingObjects:0:Name"] = "Orders",
            ["ChangeTracking:Environments:0:ChangeTracking:TrackingObjects:0:Database"] = "TestDB",
            ["ChangeTracking:Environments:0:ChangeTracking:TrackingObjects:0:TableName"] = "dbo.Orders",
            ["ChangeTracking:Environments:0:ChangeTracking:TrackingObjects:0:StoredProcedureName"] = "sp_GetOrders",
        };
        var config = TestConfigurationBuilder.Build(entries);
        ConfigurationValidator.ValidateConfiguration(config); // should not throw
    }

    [Fact]
    public void TrackingObject_MissingName_Throws()
    {
        var entries = TestConfigurationBuilder.MinimalValidEnvironment();
        entries["ChangeTracking:Environments:0:ChangeTracking:TrackingObjects:0:Name"] = "";
        var config = TestConfigurationBuilder.Build(entries);
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void TrackingObject_MissingDatabase_Throws()
    {
        var entries = TestConfigurationBuilder.MinimalValidEnvironment();
        entries["ChangeTracking:Environments:0:ChangeTracking:TrackingObjects:0:Database"] = "";
        var config = TestConfigurationBuilder.Build(entries);
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void TrackingObject_MissingTableName_Throws()
    {
        var entries = TestConfigurationBuilder.MinimalValidEnvironment();
        entries["ChangeTracking:Environments:0:ChangeTracking:TrackingObjects:0:TableName"] = "";
        var config = TestConfigurationBuilder.Build(entries);
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void TrackingObject_MissingStoredProcedure_Throws()
    {
        var entries = TestConfigurationBuilder.MinimalValidEnvironment();
        entries["ChangeTracking:Environments:0:ChangeTracking:TrackingObjects:0:StoredProcedureName"] = "";
        var config = TestConfigurationBuilder.Build(entries);
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void TrackingObject_DatabaseNotInConnectionStrings_Throws()
    {
        var entries = TestConfigurationBuilder.MinimalValidEnvironment();
        // Point tracking object at a database that has no connection string
        entries["ChangeTracking:Environments:0:ChangeTracking:TrackingObjects:0:Database"] = "MissingDB";
        var config = TestConfigurationBuilder.Build(entries);
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void UnknownMessageQueueType_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "unknown_ep",
            [$"{EP}:MessageQueueType"] = "FancyQueue",
            [$"{EP}:MessageQueue:HostName"] = "localhost",
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void EndpointWithNoMessageQueueType_AndNoMessageQueueConfig_TreatedAsHttp()
    {
        // No MessageQueueType → treated as HTTP endpoint; missing URL is an error
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "broken_http",
            // No Url set
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    // -------------------------------------------------------------------------
    // HTTP endpoints
    // -------------------------------------------------------------------------

    [Fact]
    public void HttpEndpoint_ValidBearerAuth_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "webhook",
            [$"{EP}:Url"] = "https://api.example.com/webhook",
            [$"{EP}:Auth:Type"] = "Bearer",
            [$"{EP}:Auth:Token"] = "my-token",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void HttpEndpoint_MissingUrl_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "webhook",
            [$"{EP}:Auth:Type"] = "Bearer",
            [$"{EP}:Auth:Token"] = "my-token",
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void HttpEndpoint_InvalidUrl_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "webhook",
            [$"{EP}:Url"] = "not-a-valid-url",
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void HttpEndpoint_ValidBasicAuth_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "basic_api",
            [$"{EP}:Url"] = "https://api.example.com/sync",
            [$"{EP}:Auth:Type"] = "Basic",
            [$"{EP}:Auth:Username"] = "user",
            [$"{EP}:Auth:Password"] = "pass",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void HttpEndpoint_ValidApiKeyAuth_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "apikey_api",
            [$"{EP}:Url"] = "https://api.example.com/data",
            [$"{EP}:Auth:Type"] = "ApiKey",
            [$"{EP}:Auth:ApiKey"] = "secret-key",
            [$"{EP}:Auth:HeaderName"] = "X-API-Key",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void HttpEndpoint_ValidOAuth2_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "oauth_api",
            [$"{EP}:Url"] = "https://api.example.com/webhook",
            [$"{EP}:Auth:Type"] = "OAuth2ClientCredentials",
            [$"{EP}:Auth:TokenEndpoint"] = "https://auth.example.com/token",
            [$"{EP}:Auth:ClientId"] = "client-id",
            [$"{EP}:Auth:ClientSecret"] = "client-secret",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    // -------------------------------------------------------------------------
    // RabbitMQ
    // -------------------------------------------------------------------------

    [Fact]
    public void RabbitMq_ValidDirectQueue_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "rabbitmq_queue",
            [$"{EP}:MessageQueueType"] = "RabbitMQ",
            [$"{EP}:MessageQueue:HostName"] = "rabbitmq.example.com",
            [$"{EP}:MessageQueue:Port"] = "5672",
            [$"{EP}:MessageQueue:VirtualHost"] = "/",
            [$"{EP}:MessageQueue:Username"] = "guest",
            [$"{EP}:MessageQueue:Password"] = "guest",
            [$"{EP}:MessageQueue:QueueName"] = "trignis-changes",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void RabbitMq_ValidExchangeRouting_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "rabbitmq_exchange",
            [$"{EP}:MessageQueueType"] = "RabbitMQ",
            [$"{EP}:MessageQueue:HostName"] = "rabbitmq.example.com",
            [$"{EP}:MessageQueue:Exchange"] = "data-changes",
            [$"{EP}:MessageQueue:RoutingKey"] = "db.orders",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void RabbitMq_MissingHostName_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "rabbitmq_ep",
            [$"{EP}:MessageQueueType"] = "RabbitMQ",
            [$"{EP}:MessageQueue:QueueName"] = "test-queue",
            // HostName omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void RabbitMq_MissingQueueAndExchange_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "rabbitmq_ep",
            [$"{EP}:MessageQueueType"] = "RabbitMQ",
            [$"{EP}:MessageQueue:HostName"] = "rabbitmq.example.com",
            // Neither QueueName nor Exchange
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    // -------------------------------------------------------------------------
    // Azure Service Bus
    // -------------------------------------------------------------------------

    [Fact]
    public void AzureServiceBus_ValidQueue_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "asb_queue",
            [$"{EP}:MessageQueueType"] = "AzureServiceBus",
            [$"{EP}:MessageQueue:ConnectionString"] = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            [$"{EP}:MessageQueue:QueueName"] = "change-tracking",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void AzureServiceBus_ValidTopic_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "asb_topic",
            [$"{EP}:MessageQueueType"] = "AzureServiceBus",
            [$"{EP}:MessageQueue:ConnectionString"] = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            [$"{EP}:MessageQueue:TopicName"] = "data-changes",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void AzureServiceBus_MissingConnectionString_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "asb_ep",
            [$"{EP}:MessageQueueType"] = "AzureServiceBus",
            [$"{EP}:MessageQueue:QueueName"] = "change-tracking",
            // ConnectionString omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void AzureServiceBus_MissingQueueAndTopic_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "asb_ep",
            [$"{EP}:MessageQueueType"] = "AzureServiceBus",
            [$"{EP}:MessageQueue:ConnectionString"] = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            // Neither QueueName nor TopicName
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    // -------------------------------------------------------------------------
    // AWS SQS
    // -------------------------------------------------------------------------

    [Fact]
    public void AwsSqs_ValidWithExplicitCredentials_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "sqs_creds",
            [$"{EP}:MessageQueueType"] = "AWSSQS",
            [$"{EP}:MessageQueue:QueueUrl"] = "https://sqs.us-east-1.amazonaws.com/123456789/changes",
            [$"{EP}:MessageQueue:Region"] = "us-east-1",
            [$"{EP}:MessageQueue:AccessKeyId"] = "AKIAIOSFODNN7EXAMPLE",
            [$"{EP}:MessageQueue:SecretAccessKey"] = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void AwsSqs_ValidWithIamRole_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "sqs_iam",
            [$"{EP}:MessageQueueType"] = "AWSSQS",
            [$"{EP}:MessageQueue:QueueUrl"] = "https://sqs.us-east-1.amazonaws.com/123456789/changes",
            [$"{EP}:MessageQueue:Region"] = "us-east-1",
            // No credentials → IAM role
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void AwsSqs_MissingQueueUrl_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "sqs_ep",
            [$"{EP}:MessageQueueType"] = "AWSSQS",
            [$"{EP}:MessageQueue:Region"] = "us-east-1",
            // QueueUrl omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void AwsSqs_OnlyAccessKeyWithoutSecret_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "sqs_ep",
            [$"{EP}:MessageQueueType"] = "AWSSQS",
            [$"{EP}:MessageQueue:QueueUrl"] = "https://sqs.us-east-1.amazonaws.com/123456789/changes",
            [$"{EP}:MessageQueue:AccessKeyId"] = "AKIAIOSFODNN7EXAMPLE",
            // SecretAccessKey omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void AwsSqs_OnlySecretWithoutAccessKey_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "sqs_ep",
            [$"{EP}:MessageQueueType"] = "AWSSQS",
            [$"{EP}:MessageQueue:QueueUrl"] = "https://sqs.us-east-1.amazonaws.com/123456789/changes",
            [$"{EP}:MessageQueue:SecretAccessKey"] = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            // AccessKeyId omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    // -------------------------------------------------------------------------
    // Azure Event Hubs
    // -------------------------------------------------------------------------

    [Fact]
    public void AzureEventHubs_Valid_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "eventhubs_ep",
            [$"{EP}:MessageQueueType"] = "AzureEventHubs",
            [$"{EP}:MessageQueue:ConnectionString"] = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            [$"{EP}:MessageQueue:EventHubName"] = "change-tracking",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void AzureEventHubs_MissingConnectionString_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "eventhubs_ep",
            [$"{EP}:MessageQueueType"] = "AzureEventHubs",
            [$"{EP}:MessageQueue:EventHubName"] = "change-tracking",
            // ConnectionString omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void AzureEventHubs_MissingEventHubName_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "eventhubs_ep",
            [$"{EP}:MessageQueueType"] = "AzureEventHubs",
            [$"{EP}:MessageQueue:ConnectionString"] = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            // EventHubName omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    // -------------------------------------------------------------------------
    // Kafka
    // -------------------------------------------------------------------------

    [Fact]
    public void Kafka_ValidWithSasl_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "kafka_sasl",
            [$"{EP}:MessageQueueType"] = "Kafka",
            [$"{EP}:MessageQueue:BootstrapServers"] = "kafka.example.com:9092",
            [$"{EP}:MessageQueue:Topic"] = "db-changes",
            [$"{EP}:MessageQueue:Username"] = "api-key",
            [$"{EP}:MessageQueue:Password"] = "api-secret",
            [$"{EP}:MessageQueue:SecurityProtocol"] = "SASL_SSL",
            [$"{EP}:MessageQueue:SaslMechanism"] = "PLAIN",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void Kafka_ValidPlaintext_Passes()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "kafka_local",
            [$"{EP}:MessageQueueType"] = "Kafka",
            [$"{EP}:MessageQueue:BootstrapServers"] = "localhost:9092",
            [$"{EP}:MessageQueue:Topic"] = "trignis-changes",
        });
        ConfigurationValidator.ValidateConfiguration(config);
    }

    [Fact]
    public void Kafka_MissingBootstrapServers_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "kafka_ep",
            [$"{EP}:MessageQueueType"] = "Kafka",
            [$"{EP}:MessageQueue:Topic"] = "db-changes",
            // BootstrapServers omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void Kafka_MissingTopic_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "kafka_ep",
            [$"{EP}:MessageQueueType"] = "Kafka",
            [$"{EP}:MessageQueue:BootstrapServers"] = "kafka.example.com:9092",
            // Topic omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void Kafka_UsernameWithoutPassword_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "kafka_ep",
            [$"{EP}:MessageQueueType"] = "Kafka",
            [$"{EP}:MessageQueue:BootstrapServers"] = "kafka.example.com:9092",
            [$"{EP}:MessageQueue:Topic"] = "db-changes",
            [$"{EP}:MessageQueue:Username"] = "api-key",
            // Password omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }

    [Fact]
    public void Kafka_PasswordWithoutUsername_Throws()
    {
        var config = WithEndpoint(new Dictionary<string, string?>
        {
            [$"{EP}:Key"] = "kafka_ep",
            [$"{EP}:MessageQueueType"] = "Kafka",
            [$"{EP}:MessageQueue:BootstrapServers"] = "kafka.example.com:9092",
            [$"{EP}:MessageQueue:Topic"] = "db-changes",
            [$"{EP}:MessageQueue:Password"] = "api-secret",
            // Username omitted
        });
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationValidator.ValidateConfiguration(config));
    }
}
