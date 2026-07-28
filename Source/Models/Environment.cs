using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Trignis.MicrosoftSQL.Models;

public record class EnvironmentConfig
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ConnectionStrings { get; init; } = new Dictionary<string, string>();

    public EnvironmentChangeTracking ChangeTracking { get; init; } = new();
}

/// <summary>
/// Environment-specific change tracking settings
/// </summary>
public record class EnvironmentChangeTracking
{
    public TrackingObject[] TrackingObjects { get; init; } = Array.Empty<TrackingObject>();

    public ApiEndpoint[] ApiEndpoints { get; init; } = Array.Empty<ApiEndpoint>();

    // Environment-specific settings (can override global)
    public int? PollingIntervalSeconds { get; init; }

    public bool? ExportToFile { get; init; }

    public string? FilePath { get; init; }

    public bool? ExportToApi { get; init; }

    public int? RetryCount { get; init; }

    public int? RetryDelaySeconds { get; init; }
}

/// <summary>
/// Global application settings (applies to all environments).
/// The JsonPropertyName attributes are load-bearing: /ui/api/settings serializes this record
/// and the settings page reads the PascalCase names.
/// </summary>
public record class GlobalSettings
{
    // Default values that environments can override
    [JsonPropertyName("PollingIntervalSeconds")]
    public int PollingIntervalSeconds { get; init; } = 30;

    [JsonPropertyName("ExportToFile")]
    public bool ExportToFile { get; init; } = false;

    [JsonPropertyName("FilePath")]
    public string FilePath { get; init; } = "exports/{object}/{database}/changes-{timestamp}.json";

    [JsonPropertyName("FilePathSizeLimit")]
    public int FilePathSizeLimit { get; init; } = 500;

    [JsonPropertyName("ExportToApi")]
    public bool ExportToApi { get; init; } = false;

    [JsonPropertyName("RetryCount")]
    public int RetryCount { get; init; } = 3;

    [JsonPropertyName("RetryDelaySeconds")]
    public int RetryDelaySeconds { get; init; } = 5;

    // Dead letter settings (global)
    [JsonPropertyName("DeadletterRetentionDays")]
    public int DeadletterRetentionDays { get; init; } = 60;

    [JsonPropertyName("DeadLetterThreshold")]
    public int DeadLetterThreshold { get; init; } = 100;

    [JsonPropertyName("DeadLetterCheckIntervalMinutes")]
    public int DeadLetterCheckIntervalMinutes { get; init; } = 30;

    [JsonPropertyName("DeadLetterMonitorEnabled")]
    public bool DeadLetterMonitorEnabled { get; init; } = true;

    // Health check settings (global)
    [JsonPropertyName("HealthCheckEnabled")]
    public bool HealthCheckEnabled { get; init; } = true;

    [JsonPropertyName("HealthCheckIntervalMinutes")]
    public int HealthCheckIntervalMinutes { get; init; } = 15;

    // API payload settings (global)
    [JsonPropertyName("MaxPayloadSizeBytes")]
    public int MaxPayloadSizeBytes { get; init; } = 5 * 1024 * 1024; // 5MB default

    [JsonPropertyName("MaxRecordsPerBatch")]
    public int MaxRecordsPerBatch { get; init; } = 1000; // Max records per API call

    [JsonPropertyName("EnablePayloadBatching")]
    public bool EnablePayloadBatching { get; init; } = true; // Auto-batch large payloads
}
