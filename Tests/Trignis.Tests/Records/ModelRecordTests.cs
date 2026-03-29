using System;
using System.Collections.Generic;
using Trignis.MicrosoftSQL.Models;
using Trignis.MicrosoftSQL.Services;
using Xunit;

namespace Trignis.Tests.Records;

/// <summary>
/// Validates the shape, defaults, equality, and with-expression semantics of every
/// record type that flows through the Trignis pipeline.
/// </summary>
public class ModelRecordTests
{
    // -------------------------------------------------------------------------
    // EnvironmentConfig
    // -------------------------------------------------------------------------

    [Fact]
    public void EnvironmentConfig_DefaultName_IsEmpty()
    {
        var config = new EnvironmentConfig();
        Assert.Equal(string.Empty, config.Name);
    }

    [Fact]
    public void EnvironmentConfig_DefaultConnectionStrings_IsEmptyDictionary()
    {
        var config = new EnvironmentConfig();
        Assert.NotNull(config.ConnectionStrings);
        Assert.Empty(config.ConnectionStrings);
    }

    [Fact]
    public void EnvironmentConfig_DefaultChangeTracking_IsNotNull()
    {
        var config = new EnvironmentConfig();
        Assert.NotNull(config.ChangeTracking);
    }

    [Fact]
    public void EnvironmentConfig_ValueEquality_WhenSameReferenceTypeProperties()
    {
        // Records use EqualityComparer<T>.Default per property. Dictionary<> compares by
        // reference, so equality holds only when both instances share the same dictionary object.
        var sharedDict = new Dictionary<string, string> { ["DB"] = "conn" };
        var sharedCt = new EnvironmentChangeTracking();

        var a = new EnvironmentConfig { Name = "Prod", ConnectionStrings = sharedDict, ChangeTracking = sharedCt };
        var b = new EnvironmentConfig { Name = "Prod", ConnectionStrings = sharedDict, ChangeTracking = sharedCt };
        Assert.Equal(a, b);
    }

    [Fact]
    public void EnvironmentConfig_Inequality_WhenDifferentDictionaryInstances()
    {
        // Two structurally identical dictionaries are NOT equal by reference —
        // this documents the known limitation of record equality with mutable collections.
        var a = new EnvironmentConfig { Name = "Prod", ConnectionStrings = new Dictionary<string, string>() };
        var b = new EnvironmentConfig { Name = "Prod", ConnectionStrings = new Dictionary<string, string>() };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void EnvironmentConfig_WithExpression_ProducesNewInstance()
    {
        var original = new EnvironmentConfig { Name = "Dev" };
        var updated = original with { Name = "Staging" };

        Assert.Equal("Dev", original.Name);
        Assert.Equal("Staging", updated.Name);
        Assert.NotSame(original, updated);
    }

    [Fact]
    public void EnvironmentConfig_ConnectionStrings_StoredAsReadOnly()
    {
        var dict = new Dictionary<string, string> { ["DB1"] = "connstr1" };
        var config = new EnvironmentConfig { ConnectionStrings = dict };
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(config.ConnectionStrings);
        Assert.Equal("connstr1", config.ConnectionStrings["DB1"]);
    }

    // -------------------------------------------------------------------------
    // EnvironmentChangeTracking
    // -------------------------------------------------------------------------

    [Fact]
    public void EnvironmentChangeTracking_DefaultTrackingObjects_IsEmptyArray()
    {
        var ct = new EnvironmentChangeTracking();
        Assert.NotNull(ct.TrackingObjects);
        Assert.Empty(ct.TrackingObjects);
    }

    [Fact]
    public void EnvironmentChangeTracking_DefaultApiEndpoints_IsEmptyArray()
    {
        var ct = new EnvironmentChangeTracking();
        Assert.NotNull(ct.ApiEndpoints);
        Assert.Empty(ct.ApiEndpoints);
    }

    [Fact]
    public void EnvironmentChangeTracking_NullableOverrides_DefaultToNull()
    {
        var ct = new EnvironmentChangeTracking();
        Assert.Null(ct.PollingIntervalSeconds);
        Assert.Null(ct.ExportToFile);
        Assert.Null(ct.FilePath);
        Assert.Null(ct.ExportToApi);
        Assert.Null(ct.RetryCount);
        Assert.Null(ct.RetryDelaySeconds);
    }

    [Fact]
    public void EnvironmentChangeTracking_WithExpression_OverridesPollingInterval()
    {
        var original = new EnvironmentChangeTracking();
        var overridden = original with { PollingIntervalSeconds = 10 };
        Assert.Null(original.PollingIntervalSeconds);
        Assert.Equal(10, overridden.PollingIntervalSeconds);
    }

    // -------------------------------------------------------------------------
    // GlobalSettings
    // -------------------------------------------------------------------------

    [Fact]
    public void GlobalSettings_PollingIntervalSeconds_DefaultsTo30()
    {
        Assert.Equal(30, new GlobalSettings().PollingIntervalSeconds);
    }

    [Fact]
    public void GlobalSettings_ExportToFile_DefaultsFalse()
    {
        Assert.False(new GlobalSettings().ExportToFile);
    }

    [Fact]
    public void GlobalSettings_ExportToApi_DefaultsFalse()
    {
        Assert.False(new GlobalSettings().ExportToApi);
    }

    [Fact]
    public void GlobalSettings_RetryCount_DefaultsTo3()
    {
        Assert.Equal(3, new GlobalSettings().RetryCount);
    }

    [Fact]
    public void GlobalSettings_RetryDelaySeconds_DefaultsTo5()
    {
        Assert.Equal(5, new GlobalSettings().RetryDelaySeconds);
    }

    [Fact]
    public void GlobalSettings_DeadletterRetentionDays_DefaultsTo60()
    {
        Assert.Equal(60, new GlobalSettings().DeadletterRetentionDays);
    }

    [Fact]
    public void GlobalSettings_DeadLetterThreshold_DefaultsTo100()
    {
        Assert.Equal(100, new GlobalSettings().DeadLetterThreshold);
    }

    [Fact]
    public void GlobalSettings_DeadLetterCheckIntervalMinutes_DefaultsTo30()
    {
        Assert.Equal(30, new GlobalSettings().DeadLetterCheckIntervalMinutes);
    }

    [Fact]
    public void GlobalSettings_DeadLetterMonitorEnabled_DefaultsTrue()
    {
        Assert.True(new GlobalSettings().DeadLetterMonitorEnabled);
    }

    [Fact]
    public void GlobalSettings_HealthCheckEnabled_DefaultsTrue()
    {
        Assert.True(new GlobalSettings().HealthCheckEnabled);
    }

    [Fact]
    public void GlobalSettings_HealthCheckIntervalMinutes_DefaultsTo15()
    {
        Assert.Equal(15, new GlobalSettings().HealthCheckIntervalMinutes);
    }

    [Fact]
    public void GlobalSettings_MaxPayloadSizeBytes_DefaultsTo5MB()
    {
        Assert.Equal(5 * 1024 * 1024, new GlobalSettings().MaxPayloadSizeBytes);
    }

    [Fact]
    public void GlobalSettings_MaxRecordsPerBatch_DefaultsTo1000()
    {
        Assert.Equal(1000, new GlobalSettings().MaxRecordsPerBatch);
    }

    [Fact]
    public void GlobalSettings_EnablePayloadBatching_DefaultsTrue()
    {
        Assert.True(new GlobalSettings().EnablePayloadBatching);
    }

    [Fact]
    public void GlobalSettings_FilePath_DefaultsToTemplate()
    {
        Assert.Equal("exports/{object}/{database}/changes-{timestamp}.json", new GlobalSettings().FilePath);
    }

    [Fact]
    public void GlobalSettings_WithExpression_OverridesRetryCount()
    {
        var defaults = new GlobalSettings();
        var custom = defaults with { RetryCount = 5, RetryDelaySeconds = 10 };
        Assert.Equal(3, defaults.RetryCount);
        Assert.Equal(5, custom.RetryCount);
        Assert.Equal(10, custom.RetryDelaySeconds);
    }

    // -------------------------------------------------------------------------
    // TrackingObject
    // -------------------------------------------------------------------------

    [Fact]
    public void TrackingObject_RequiredProperties_MustBeSupplied()
    {
        var obj = new TrackingObject
        {
            Name = "Orders",
            Database = "SalesDB",
            TableName = "dbo.Orders",
            StoredProcedureName = "sp_GetOrderChanges"
        };

        Assert.Equal("Orders", obj.Name);
        Assert.Equal("SalesDB", obj.Database);
        Assert.Equal("dbo.Orders", obj.TableName);
        Assert.Equal("sp_GetOrderChanges", obj.StoredProcedureName);
    }

    [Fact]
    public void TrackingObject_InitialSyncMode_DefaultsToIncremental()
    {
        var obj = new TrackingObject
        {
            Name = "X", Database = "X", TableName = "X", StoredProcedureName = "X"
        };
        Assert.Equal("Incremental", obj.InitialSyncMode);
    }

    [Fact]
    public void TrackingObject_EnvironmentFile_DefaultsToNull()
    {
        var obj = new TrackingObject
        {
            Name = "X", Database = "X", TableName = "X", StoredProcedureName = "X"
        };
        Assert.Null(obj.EnvironmentFile);
    }

    [Fact]
    public void TrackingObject_WithExpression_StampsEnvironmentFile()
    {
        var original = new TrackingObject
        {
            Name = "Orders", Database = "SalesDB", TableName = "dbo.Orders", StoredProcedureName = "sp_Get"
        };
        var stamped = original with { EnvironmentFile = "prod.json" };

        Assert.Null(original.EnvironmentFile);
        Assert.Equal("prod.json", stamped.EnvironmentFile);
    }

    [Fact]
    public void TrackingObject_ValueEquality_WhenAllPropertiesMatch()
    {
        var a = new TrackingObject { Name = "A", Database = "DB", TableName = "T", StoredProcedureName = "SP" };
        var b = new TrackingObject { Name = "A", Database = "DB", TableName = "T", StoredProcedureName = "SP" };
        Assert.Equal(a, b);
    }

    // -------------------------------------------------------------------------
    // ApiEndpoint
    // -------------------------------------------------------------------------

    [Fact]
    public void ApiEndpoint_AllProperties_DefaultToNull()
    {
        var ep = new ApiEndpoint();
        Assert.Null(ep.Key);
        Assert.Null(ep.Url);
        Assert.Null(ep.Auth);
        Assert.Null(ep.EnvironmentFile);
        Assert.Null(ep.CustomHeaders);
        Assert.Null(ep.MessageQueueType);
        Assert.Null(ep.MessageQueue);
    }

    [Fact]
    public void ApiEndpoint_EnableCompression_DefaultsFalse()
    {
        Assert.False(new ApiEndpoint().EnableCompression);
    }

    [Fact]
    public void ApiEndpoint_HttpEndpoint_RoundTrips()
    {
        var ep = new ApiEndpoint
        {
            Key = "api1",
            Url = "https://example.com/events",
            Auth = new ApiAuth { Type = "Bearer", Token = "tok123" },
            EnableCompression = true
        };

        Assert.Equal("api1", ep.Key);
        Assert.Equal("https://example.com/events", ep.Url);
        Assert.Equal("Bearer", ep.Auth!.Type);
        Assert.True(ep.EnableCompression);
    }

    [Fact]
    public void ApiEndpoint_MessageQueueEndpoint_RoundTrips()
    {
        var mq = new MessageQueueConfig { HostName = "rabbit", QueueName = "changes" };
        var ep = new ApiEndpoint { MessageQueueType = "RabbitMQ", MessageQueue = mq };

        Assert.Equal("RabbitMQ", ep.MessageQueueType);
        Assert.Equal("rabbit", ep.MessageQueue!.HostName);
        Assert.Equal("changes", ep.MessageQueue.QueueName);
    }

    [Fact]
    public void ApiEndpoint_CustomHeaders_StoredAsReadOnly()
    {
        var headers = new Dictionary<string, string> { ["X-Tenant"] = "acme" };
        var ep = new ApiEndpoint { CustomHeaders = headers };
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(ep.CustomHeaders);
        Assert.Equal("acme", ep.CustomHeaders!["X-Tenant"]);
    }

    [Fact]
    public void ApiEndpoint_WithExpression_UpdatesUrl()
    {
        var original = new ApiEndpoint { Url = "https://old.example.com" };
        var updated = original with { Url = "https://new.example.com" };

        Assert.Equal("https://old.example.com", original.Url);
        Assert.Equal("https://new.example.com", updated.Url);
    }

    // -------------------------------------------------------------------------
    // ApiAuth
    // -------------------------------------------------------------------------

    [Fact]
    public void ApiAuth_AllProperties_DefaultToNull()
    {
        var auth = new ApiAuth();
        Assert.Null(auth.Type);
        Assert.Null(auth.Token);
        Assert.Null(auth.Username);
        Assert.Null(auth.Password);
        Assert.Null(auth.ApiKey);
        Assert.Null(auth.HeaderName);
        Assert.Null(auth.TokenEndpoint);
        Assert.Null(auth.ClientId);
        Assert.Null(auth.ClientSecret);
        Assert.Null(auth.Scope);
        Assert.Null(auth.TokenExpirationSeconds);
    }

    [Fact]
    public void ApiAuth_BearerAuth_RoundTrips()
    {
        var auth = new ApiAuth { Type = "Bearer", Token = "eyJhbGciOiJSUzI1NiJ9" };
        Assert.Equal("Bearer", auth.Type);
        Assert.Equal("eyJhbGciOiJSUzI1NiJ9", auth.Token);
    }

    [Fact]
    public void ApiAuth_BasicAuth_RoundTrips()
    {
        var auth = new ApiAuth { Type = "Basic", Username = "user", Password = "pass" };
        Assert.Equal("Basic", auth.Type);
        Assert.Equal("user", auth.Username);
        Assert.Equal("pass", auth.Password);
    }

    [Fact]
    public void ApiAuth_ApiKeyAuth_RoundTrips()
    {
        var auth = new ApiAuth { Type = "ApiKey", ApiKey = "secret-key", HeaderName = "X-API-Key" };
        Assert.Equal("ApiKey", auth.Type);
        Assert.Equal("secret-key", auth.ApiKey);
        Assert.Equal("X-API-Key", auth.HeaderName);
    }

    [Fact]
    public void ApiAuth_OAuth2_RoundTrips()
    {
        var auth = new ApiAuth
        {
            Type = "OAuth2ClientCredentials",
            TokenEndpoint = "https://auth.example.com/token",
            ClientId = "client123",
            ClientSecret = "secret",
            Scope = "api.read",
            TokenExpirationSeconds = 1800
        };
        Assert.Equal("OAuth2ClientCredentials", auth.Type);
        Assert.Equal("https://auth.example.com/token", auth.TokenEndpoint);
        Assert.Equal("client123", auth.ClientId);
        Assert.Equal("secret", auth.ClientSecret);
        Assert.Equal("api.read", auth.Scope);
        Assert.Equal(1800, auth.TokenExpirationSeconds);
    }

    [Fact]
    public void ApiAuth_ValueEquality_WhenPropertiesMatch()
    {
        var a = new ApiAuth { Type = "Bearer", Token = "tok" };
        var b = new ApiAuth { Type = "Bearer", Token = "tok" };
        Assert.Equal(a, b);
    }

    // -------------------------------------------------------------------------
    // MessageQueueConfig
    // -------------------------------------------------------------------------

    [Fact]
    public void MessageQueueConfig_RabbitMqDefaults()
    {
        var cfg = new MessageQueueConfig();
        Assert.Equal(5672, cfg.Port);
        Assert.Equal("/", cfg.VirtualHost);
        Assert.Null(cfg.HostName);
        Assert.Null(cfg.Username);
        Assert.Null(cfg.Password);
        Assert.Null(cfg.QueueName);
        Assert.Null(cfg.Exchange);
        Assert.Null(cfg.RoutingKey);
    }

    [Fact]
    public void MessageQueueConfig_RabbitMq_RoundTrips()
    {
        var cfg = new MessageQueueConfig
        {
            HostName = "rabbitmq.internal",
            Port = 5671,
            VirtualHost = "/staging",
            Username = "admin",
            Password = "pass",
            QueueName = "db-changes"
        };
        Assert.Equal("rabbitmq.internal", cfg.HostName);
        Assert.Equal(5671, cfg.Port);
        Assert.Equal("/staging", cfg.VirtualHost);
        Assert.Equal("db-changes", cfg.QueueName);
    }

    [Fact]
    public void MessageQueueConfig_AzureServiceBus_RoundTrips()
    {
        var cfg = new MessageQueueConfig
        {
            ConnectionString = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            QueueName = "changes"
        };
        Assert.NotNull(cfg.ConnectionString);
        Assert.Equal("changes", cfg.QueueName);
    }

    [Fact]
    public void MessageQueueConfig_AwsSqs_RoundTrips()
    {
        var cfg = new MessageQueueConfig
        {
            QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456789/changes",
            Region = "us-east-1",
            AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
            SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
        };
        Assert.Equal("us-east-1", cfg.Region);
        Assert.NotNull(cfg.AccessKeyId);
        Assert.NotNull(cfg.SecretAccessKey);
    }

    [Fact]
    public void MessageQueueConfig_Kafka_RoundTrips()
    {
        var cfg = new MessageQueueConfig
        {
            BootstrapServers = "broker1:9092,broker2:9092",
            Topic = "db-changes",
            SecurityProtocol = "SASL_SSL",
            SaslMechanism = "PLAIN"
        };
        Assert.Equal("broker1:9092,broker2:9092", cfg.BootstrapServers);
        Assert.Equal("db-changes", cfg.Topic);
        Assert.Equal("SASL_SSL", cfg.SecurityProtocol);
        Assert.Equal("PLAIN", cfg.SaslMechanism);
    }

    [Fact]
    public void MessageQueueConfig_AzureEventHubs_RoundTrips()
    {
        var cfg = new MessageQueueConfig
        {
            ConnectionString = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=ZmFrZQ==",
            EventHubName = "db-changes"
        };
        Assert.Equal("db-changes", cfg.EventHubName);
    }

    // -------------------------------------------------------------------------
    // DeadLetterStats (readonly record struct)
    // -------------------------------------------------------------------------

    [Fact]
    public void DeadLetterStats_DefaultValues_AreZero()
    {
        var stats = new DeadLetterStats();
        Assert.Equal(0L, stats.TotalCount);
        Assert.Equal(0L, stats.LastHourCount);
        Assert.Equal(0L, stats.Last24HoursCount);
        Assert.Equal(0L, stats.Last7DaysCount);
        Assert.Equal(0L, stats.MostCommonErrorCount);
        Assert.Null(stats.MostCommonError);
    }

    [Fact]
    public void DeadLetterStats_ValueEquality_WhenPropertiesMatch()
    {
        var a = new DeadLetterStats { TotalCount = 5, LastHourCount = 1 };
        var b = new DeadLetterStats { TotalCount = 5, LastHourCount = 1 };
        Assert.Equal(a, b);
    }

    [Fact]
    public void DeadLetterStats_IsValueType()
    {
        Assert.True(typeof(DeadLetterStats).IsValueType);
    }

    [Fact]
    public void DeadLetterStats_WithExpression_ProducesNewValue()
    {
        var original = new DeadLetterStats { TotalCount = 10 };
        var updated = original with { TotalCount = 20, MostCommonError = "Timeout" };

        Assert.Equal(10L, original.TotalCount);
        Assert.Equal(20L, updated.TotalCount);
        Assert.Equal("Timeout", updated.MostCommonError);
        Assert.Null(original.MostCommonError);
    }

    [Fact]
    public void DeadLetterStats_AllCounters_RoundTrip()
    {
        var stats = new DeadLetterStats
        {
            TotalCount = 1000,
            LastHourCount = 5,
            Last24HoursCount = 42,
            Last7DaysCount = 300,
            MostCommonError = "Connection timeout",
            MostCommonErrorCount = 17
        };
        Assert.Equal(1000L, stats.TotalCount);
        Assert.Equal(5L, stats.LastHourCount);
        Assert.Equal(42L, stats.Last24HoursCount);
        Assert.Equal(300L, stats.Last7DaysCount);
        Assert.Equal("Connection timeout", stats.MostCommonError);
        Assert.Equal(17L, stats.MostCommonErrorCount);
    }

    // -------------------------------------------------------------------------
    // EnvironmentChangeEvent
    // -------------------------------------------------------------------------

    [Fact]
    public void EnvironmentChangeEvent_DefaultCollections_AreEmpty()
    {
        var evt = new EnvironmentChangeEvent();
        Assert.NotNull(evt.Added);
        Assert.NotNull(evt.Removed);
        Assert.NotNull(evt.Updated);
        Assert.Empty(evt.Added);
        Assert.Empty(evt.Removed);
        Assert.Empty(evt.Updated);
    }

    [Fact]
    public void EnvironmentChangeEvent_Added_StoredAsReadOnly()
    {
        var added = new List<EnvironmentConfig> { new() { Name = "NewEnv" } };
        var evt = new EnvironmentChangeEvent { Added = added };
        Assert.IsAssignableFrom<IReadOnlyList<EnvironmentConfig>>(evt.Added);
        Assert.Single(evt.Added);
        Assert.Equal("NewEnv", evt.Added[0].Name);
    }

    [Fact]
    public void EnvironmentChangeEvent_Updated_StoredAsReadOnly()
    {
        var updated = new List<EnvironmentConfig>
        {
            new() { Name = "Env1" },
            new() { Name = "Env2" }
        };
        var evt = new EnvironmentChangeEvent { Updated = updated };
        Assert.Equal(2, evt.Updated.Count);
    }
}
