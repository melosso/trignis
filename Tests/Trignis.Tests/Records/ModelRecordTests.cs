using Trignis.MicrosoftSQL.Models;
using Xunit;

namespace Trignis.Tests.Records;

/// <summary>
/// Pins the model defaults production actually depends on: each one is the value a service
/// falls back to when the corresponding configuration key is absent. Record mechanics
/// (with-expressions, value equality, init-only accessors) are compiler behaviour and are
/// deliberately not tested here.
/// </summary>
public class ModelRecordTests
{
    [Fact]
    public void GlobalSettings_Defaults_MatchDocumentedFallbacks()
    {
        var s = new GlobalSettings();

        Assert.Equal(30, s.PollingIntervalSeconds);
        Assert.False(s.ExportToFile);
        Assert.Equal("exports/{object}/{database}/changes-{timestamp}.json", s.FilePath);
        Assert.Equal(500, s.FilePathSizeLimit);
        Assert.False(s.ExportToApi);
        Assert.Equal(3, s.RetryCount);
        Assert.Equal(5, s.RetryDelaySeconds);
        Assert.Equal(60, s.DeadletterRetentionDays);
        Assert.Equal(100, s.DeadLetterThreshold);
        Assert.Equal(30, s.DeadLetterCheckIntervalMinutes);
        Assert.True(s.DeadLetterMonitorEnabled);
        Assert.True(s.HealthCheckEnabled);
        Assert.Equal(15, s.HealthCheckIntervalMinutes);
        Assert.Equal(5 * 1024 * 1024, s.MaxPayloadSizeBytes);
        Assert.Equal(1000, s.MaxRecordsPerBatch);
        Assert.True(s.EnablePayloadBatching);
    }

    /// <summary>Null means "inherit the global value". See ChangeTrackingBackgroundService.</summary>
    [Fact]
    public void EnvironmentChangeTracking_OverridesDefaultToNull()
    {
        var ct = new EnvironmentChangeTracking();

        Assert.Null(ct.PollingIntervalSeconds);
        Assert.Null(ct.ExportToFile);
        Assert.Null(ct.FilePath);
        Assert.Null(ct.ExportToApi);
        Assert.Null(ct.RetryCount);
        Assert.Null(ct.RetryDelaySeconds);
        Assert.Empty(ct.TrackingObjects);
        Assert.Empty(ct.ApiEndpoints);
    }

    [Fact]
    public void EnvironmentConfig_Defaults_AreNonNull()
    {
        var config = new EnvironmentConfig();

        Assert.NotNull(config.ChangeTracking);
        Assert.Empty(config.ConnectionStrings);
    }

    /// <summary>Anything other than "Full" takes the incremental branch on first sync.</summary>
    [Fact]
    public void TrackingObject_InitialSyncMode_DefaultsToIncremental()
    {
        var obj = new TrackingObject
        {
            Name = "Orders",
            Database = "TestDB",
            TableName = "dbo.Orders",
            StoredProcedureName = "sp_GetOrders"
        };

        Assert.Equal("Incremental", obj.InitialSyncMode);
    }

    [Fact]
    public void ApiEndpoint_EnableCompression_DefaultsFalse()
    {
        Assert.False(new ApiEndpoint().EnableCompression);
    }

    [Fact]
    public void MessageQueueConfig_RabbitMqDefaults()
    {
        var config = new MessageQueueConfig();

        Assert.Equal(5672, config.Port);
        Assert.Equal("/", config.VirtualHost);
    }
}
