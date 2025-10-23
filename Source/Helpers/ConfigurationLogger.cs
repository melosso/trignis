using Microsoft.Extensions.Configuration;
using Serilog;
using Microsoft.Data.SqlClient;
using System;
using System.Linq;
using Trignis.MicrosoftSQL.Models;

namespace Trignis.MicrosoftSQL.Helpers;

public static class ConfigurationLogger
{
    public static void LogConfigurationStatus(IConfiguration configuration)
    {
        var version = typeof(ConfigurationLogger).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        Log.Information("");
        Log.Information("Application is booting up...");
        Log.Information($" ├─ Version: {version}");
        Log.Information($" └─ Environment: {Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}");
        Log.Information("");
        Log.Information("[Configuration]");

        // Global Settings
        var globalSettings = configuration.GetSection("ChangeTracking:GlobalSettings").Get<GlobalSettings>() ?? new GlobalSettings();
        Log.Information("├─ Global Settings:");
        Log.Information($"│  ├─ Default Polling Interval: {globalSettings.PollingIntervalSeconds}s");
        Log.Information($"│  ├─ Max Payload Size: {globalSettings.MaxPayloadSizeBytes / 1024 / 1024}MB");
        Log.Information($"│  ├─ Max Records Per Batch: {globalSettings.MaxRecordsPerBatch}");
        Log.Information($"│  ├─ Payload Batching: {(globalSettings.EnablePayloadBatching ? "Enabled" : "DISABLED")}");
        Log.Information($"│  ├─ Retry Count: {globalSettings.RetryCount}");
        Log.Information($"│  ├─ Retry Delay: {globalSettings.RetryDelaySeconds}s");
        Log.Information($"│  ├─ Dead Letter Retention: {globalSettings.DeadletterRetentionDays} days");
        Log.Information($"│  ├─ Dead Letter Monitor: {(globalSettings.DeadLetterMonitorEnabled ? $"{globalSettings.DeadLetterThreshold} messages / Every {globalSettings.DeadLetterCheckIntervalMinutes}min" : "DISABLED")}");
        Log.Information($"│  └─ Connection Health Check: {(globalSettings.HealthCheckEnabled ? $"Every {globalSettings.HealthCheckIntervalMinutes}min" : "DISABLED")}");
        Log.Information("│");

        // Environments
        var environments = configuration.GetSection("ChangeTracking:Environments").Get<EnvironmentConfig[]>() ?? Array.Empty<EnvironmentConfig>();
        Log.Information($"├─ Environments: {environments.Length}");
        
        for (int envIndex = 0; envIndex < environments.Length; envIndex++)
        {
            var env = environments[envIndex];
            var isLastEnv = envIndex == environments.Length - 1;
            var envPrefix = isLastEnv ? "└─" : "├─";
            var envVertical = isLastEnv ? " " : "│";
            
            var totalObjects = env.ChangeTracking.TrackingObjects.Length;
            var totalEndpoints = env.ChangeTracking.ApiEndpoints.Length;
            
            Log.Information($"│  {envPrefix} Environment: [{env.Name}] ({totalObjects} objects, {totalEndpoints} endpoints)");
            
            // Environment-specific settings
            var pollingInterval = env.ChangeTracking.PollingIntervalSeconds ?? globalSettings.PollingIntervalSeconds;
            var exportToFile = env.ChangeTracking.ExportToFile ?? globalSettings.ExportToFile;
            var exportToApi = env.ChangeTracking.ExportToApi ?? globalSettings.ExportToApi;
            
            Log.Information($"│  {envVertical}  ├─ Settings:");
            Log.Information($"│  {envVertical}  │  ├─ Polling Interval: {pollingInterval}s {(env.ChangeTracking.PollingIntervalSeconds.HasValue ? "*" : "")}");
            Log.Information($"│  {envVertical}  │  ├─ Export to File: {(exportToFile ? "Enabled" : "DISABLED")} {(env.ChangeTracking.ExportToFile.HasValue ? "*" : "")}");
            Log.Information($"│  {envVertical}  │  └─ Export to API: {(exportToApi ? "Enabled" : "DISABLED")} {(env.ChangeTracking.ExportToApi.HasValue ? "*" : "")}");
            
            // Connection Strings
            Log.Information($"│  {envVertical}  ├─ Connection Strings: {env.ConnectionStrings.Count}");
            var connIndex = 0;
            foreach (var conn in env.ConnectionStrings)
            {
                connIndex++;
                var isLastConn = connIndex == env.ConnectionStrings.Count;
                var connPrefix = isLastConn ? "└─" : "├─";
                
                try
                {
                    var builder = new SqlConnectionStringBuilder(conn.Value);
                    Log.Information($"│  {envVertical}  │  {connPrefix} {conn.Key}: {builder.DataSource}/{builder.InitialCatalog ?? "N/A"}");
                }
                catch (Exception ex)
                {
                    Log.Error($"│  {envVertical}  │  {connPrefix} {conn.Key}: Invalid connection - {ex.Message}");
                }
            }
            
            // Tracking Objects
            Log.Information($"│  {envVertical}  ├─ Tracking Objects: {totalObjects}");
            for (int i = 0; i < env.ChangeTracking.TrackingObjects.Length; i++)
            {
                var obj = env.ChangeTracking.TrackingObjects[i];
                var isLastObj = i == env.ChangeTracking.TrackingObjects.Length - 1;
                var objPrefix = isLastObj ? "└─" : "├─";
                
                if (env.ConnectionStrings.ContainsKey(obj.Database))
                {
                    var syncMode = string.Equals(obj.InitialSyncMode, "Full", StringComparison.OrdinalIgnoreCase) ? "Full" : "Incremental";
                    Log.Information($"│  {envVertical}  │  {objPrefix} ✓ '{obj.Name}' ({obj.TableName}) — DB: {obj.Database}, SP: {obj.StoredProcedureName}, Mode: {syncMode}");
                }
                else
                {
                    Log.Warning($"│  {envVertical}  │  {objPrefix} ✖ '{obj.Name}' ({obj.TableName}): Database '{obj.Database}' connection missing");
                }
            }
            
            // API Endpoints
            Log.Information($"│  {envVertical}  └─ API Endpoints: {totalEndpoints}");
            for (int i = 0; i < env.ChangeTracking.ApiEndpoints.Length; i++)
            {
                var endpoint = env.ChangeTracking.ApiEndpoints[i];
                var isLastEndpoint = i == env.ChangeTracking.ApiEndpoints.Length - 1;
                var epPrefix = isLastEndpoint ? "└─" : "├─";
                var epVertical = isLastEndpoint ? " " : "│";
                
                Log.Information($"│  {envVertical}     {epPrefix} Endpoint '{endpoint.Key ?? $"#{i+1}"}'");
                
                // Message Queue endpoint
                if (!string.IsNullOrEmpty(endpoint.MessageQueueType))
                {
                    Log.Information($"│  {envVertical}     {epVertical}  ├─ Type: {endpoint.MessageQueueType}");
                    
                    if (endpoint.MessageQueue != null)
                    {
                        switch (endpoint.MessageQueueType.ToLower())
                        {
                            case "rabbitmq":
                                var mqTarget = !string.IsNullOrEmpty(endpoint.MessageQueue.QueueName) 
                                    ? $"Queue: {endpoint.MessageQueue.QueueName}"
                                    : $"Exchange: {endpoint.MessageQueue.Exchange}" + 
                                      (!string.IsNullOrEmpty(endpoint.MessageQueue.RoutingKey) ? $" (Key: {endpoint.MessageQueue.RoutingKey})" : "");
                                Log.Information($"│  {envVertical}     {epVertical}  └─ {mqTarget}");
                                break;
                            case "azureservicebus":
                                var asbTarget = !string.IsNullOrEmpty(endpoint.MessageQueue.QueueName)
                                    ? $"Queue: {endpoint.MessageQueue.QueueName}"
                                    : $"Topic: {endpoint.MessageQueue.TopicName}";
                                Log.Information($"│  {envVertical}     {epVertical}  └─ {asbTarget}");
                                break;
                            case "awssqs":
                                Log.Information($"│  {envVertical}     {epVertical}  └─ Queue: {endpoint.MessageQueue.QueueUrl}");
                                break;
                        }
                    }
                }
                // HTTP endpoint
                else
                {
                    Log.Information($"│  {envVertical}     {epVertical}  ├─ Type: HTTP");
                    Log.Information($"│  {envVertical}     {epVertical}  ├─ URL: {endpoint.Url}");
                    var authType = endpoint.Auth?.Type ?? "None";
                    if (endpoint.EnableCompression)
                    {
                        authType += " (Compressed)";
                    }
                    Log.Information($"│  {envVertical}     {epVertical}  └─ Auth: {authType}");
                }
            }
        }

        // Health Endpoint
        var healthEnabled = configuration.GetValue<bool>("Health:Enabled", false);
        var healthPort = configuration.GetValue<int>("Health:Port", 2455);
        var healthHost = configuration.GetValue<string>("Health:Host", "*");
        
        Log.Information("│");
        if (healthEnabled)
        {
            Log.Information($"└─ Health Endpoint:");
            Log.Information($"   ├─ Status: Enabled");
            Log.Information($"   └─ URL: http://{healthHost}:{healthPort}");
        }
        else
        {
            Log.Information($"└─ Health Endpoint: DISABLED");
        }
        
        Log.Information("");
    }
}