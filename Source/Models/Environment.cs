using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Trignis.MicrosoftSQL.Models;

public class EnvironmentConfig
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("ConnectionStrings")]
    public Dictionary<string, string> ConnectionStrings { get; set; } = new();
    
    [JsonPropertyName("ChangeTracking")]
    public EnvironmentChangeTracking ChangeTracking { get; set; } = new();
}

/// <summary>
/// Environment-specific change tracking settings
/// </summary>
public class EnvironmentChangeTracking
{
    [JsonPropertyName("TrackingObjects")]
    public TrackingObject[] TrackingObjects { get; set; } = Array.Empty<TrackingObject>();
    
    [JsonPropertyName("ApiEndpoints")]
    public ApiEndpoint[] ApiEndpoints { get; set; } = Array.Empty<ApiEndpoint>();
    
    // Environment-specific settings (can override global)
    [JsonPropertyName("PollingIntervalSeconds")]
    public int? PollingIntervalSeconds { get; set; }
    
    [JsonPropertyName("ExportToFile")]
    public bool? ExportToFile { get; set; }
    
    [JsonPropertyName("FilePath")]
    public string? FilePath { get; set; }
    
    [JsonPropertyName("ExportToApi")]
    public bool? ExportToApi { get; set; }
    
    [JsonPropertyName("RetryCount")]
    public int? RetryCount { get; set; }
    
    [JsonPropertyName("RetryDelaySeconds")]
    public int? RetryDelaySeconds { get; set; }
}

/// <summary>
/// Global application settings (applies to all environments)
/// </summary>
public class GlobalSettings
{
    // Default values that environments can override
    [JsonPropertyName("PollingIntervalSeconds")]
    public int PollingIntervalSeconds { get; set; } = 30;
    
    [JsonPropertyName("ExportToFile")]
    public bool ExportToFile { get; set; } = false;
    
    [JsonPropertyName("FilePath")]
    public string FilePath { get; set; } = "exports/{object}/{database}/changes-{timestamp}.json";
    
    [JsonPropertyName("FilePathSizeLimit")]
    public int FilePathSizeLimit { get; set; } = 500;
    
    [JsonPropertyName("ExportToApi")]
    public bool ExportToApi { get; set; } = false;
    
    [JsonPropertyName("RetryCount")]
    public int RetryCount { get; set; } = 3;
    
    [JsonPropertyName("RetryDelaySeconds")]
    public int RetryDelaySeconds { get; set; } = 5;
    
    // Dead letter settings (global)
    [JsonPropertyName("DeadletterRetentionDays")]
    public int DeadletterRetentionDays { get; set; } = 60;
    
    [JsonPropertyName("DeadLetterThreshold")]
    public int DeadLetterThreshold { get; set; } = 100;
    
    [JsonPropertyName("DeadLetterCheckIntervalMinutes")]
    public int DeadLetterCheckIntervalMinutes { get; set; } = 30;
    
    [JsonPropertyName("DeadLetterMonitorEnabled")]
    public bool DeadLetterMonitorEnabled { get; set; } = true;
    
    // Health check settings (global)
    [JsonPropertyName("HealthCheckEnabled")]
    public bool HealthCheckEnabled { get; set; } = true;
    
    [JsonPropertyName("HealthCheckIntervalMinutes")]
    public int HealthCheckIntervalMinutes { get; set; } = 15;
    
    // API payload settings (global)
    [JsonPropertyName("MaxPayloadSizeBytes")]
    public int MaxPayloadSizeBytes { get; set; } = 5 * 1024 * 1024; // 5MB default
    
    [JsonPropertyName("MaxRecordsPerBatch")]
    public int MaxRecordsPerBatch { get; set; } = 1000; // Max records per API call
    
    [JsonPropertyName("EnablePayloadBatching")]
    public bool EnablePayloadBatching { get; set; } = true; // Auto-batch large payloads
}