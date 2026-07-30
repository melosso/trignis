using Microsoft.Extensions.Configuration;
using Serilog;
using System.Data.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using Trignis.Data;
using Trignis.Models;

namespace Trignis.Helpers;

public static class ConfigurationLogger
{
    /// <summary>
    /// First of <paramref name="keys"/> present in the connection string, since the key naming
    /// differs per provider (SQL Server "Server", PostgreSQL "Host", both accept "Data Source").
    /// </summary>
    private static string Lookup(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
            if (builder.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        return "N/A";
    }

    public static void LogConfigurationStatus(IConfiguration configuration, IReadOnlyList<EnvironmentConfig> environments, GlobalSettings globalSettings)
    {
        var version = typeof(ConfigurationLogger).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        Log.Information("");
        Log.Information("Application is booting up...");
        Log.Information($" ├─ Version: {version}");
        Log.Information($" └─ Environment: {Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}");
        Log.Information("");
        Log.Information("[Configuration]");

        Log.Information("├─ Global Settings:");
        Log.Information($"│  ├─ Default Polling Interval: {globalSettings.PollingIntervalSeconds}s");
        Log.Information($"│  ├─ Max Payload Size: {globalSettings.MaxPayloadSizeBytes / 1024 / 1024}MB");
        Log.Information($"│  ├─ Max Records Per Batch: {globalSettings.MaxRecordsPerBatch}");
        Log.Information($"│  ├─ Payload Batching: {(globalSettings.EnablePayloadBatching ? "Enabled" : "Disabled")}");
        Log.Information($"│  ├─ Retry Count: {globalSettings.RetryCount}");
        Log.Information($"│  ├─ Retry Delay: {globalSettings.RetryDelaySeconds}s");
        Log.Information($"│  ├─ Dead Letter Retention: {globalSettings.DeadletterRetentionDays} days");
        Log.Information($"│  ├─ Dead Letter Monitor: {(globalSettings.DeadLetterMonitorEnabled ? $"{globalSettings.DeadLetterThreshold} messages / Every {globalSettings.DeadLetterCheckIntervalMinutes}min" : "Disabled")}");
        Log.Information($"│  └─ Connection Health Check: {(globalSettings.HealthCheckEnabled ? $"Every {globalSettings.HealthCheckIntervalMinutes}min" : "Disabled")}");
        Log.Information("│");

        Log.Information($"├─ Environments: {environments.Count}");

        for (int envIndex = 0; envIndex < environments.Count; envIndex++)
        {
            var env = environments[envIndex];
            var isLastEnv = envIndex == environments.Count - 1;
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
            Log.Information($"│  {envVertical}  │  ├─ Export to File: {(exportToFile ? "Enabled" : "Disabled")} {(env.ChangeTracking.ExportToFile.HasValue ? "*" : "")}");
            Log.Information($"│  {envVertical}  │  └─ Export to API: {(exportToApi ? "Enabled" : "Disabled")} {(env.ChangeTracking.ExportToApi.HasValue ? "*" : "")}");
            
            // Connection Strings
            Log.Information($"│  {envVertical}  ├─ Provider: {(SqlDialect.TryParse(env.Provider, out var dialect) ? dialect.Name : $"{env.Provider} (unknown)")}");
            Log.Information($"│  {envVertical}  ├─ Connection Strings: {env.ConnectionStrings.Count}");
            var connIndex = 0;
            foreach (var conn in env.ConnectionStrings)
            {
                connIndex++;
                var isLastConn = connIndex == env.ConnectionStrings.Count;
                var connPrefix = isLastConn ? "└─" : "├─";
                
                try
                {
                    var builder = new DbConnectionStringBuilder { ConnectionString = conn.Value };
                    Log.Information($"│  {envVertical}  │  {connPrefix} {conn.Key}: {Lookup(builder, "server", "data source", "host")}/{Lookup(builder, "database", "initial catalog")}");
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
                    Log.Information($"│  {envVertical}  │  {objPrefix} ✓ '{obj.Name}' ({obj.TableName}) · DB: {obj.Database}, SP: {obj.StoredProcedureName}, Mode: {syncMode}");
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
                        Log.Information($"│  {envVertical}     {epVertical}  └─ {endpoint.MessageQueueTarget()}");
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

        // Health Endpoint / Web UI
        var healthEnabled = configuration.GetValue<bool>("Health:Enabled", false);
        var webHostEnabled = configuration.GetValue<bool>("WebHost:Enabled", false);
        var healthPort = configuration.GetValue<int>("Health:Port", 2455);
        var healthHost = configuration.GetValue<string>("Health:Host", "*");

        Log.Information($"│");
        Log.Information($"├─ Health Endpoint: {(healthEnabled ? $"http://{healthHost}:{healthPort}" : "Disabled")}");
        Log.Information($"└─ Web UI: {(webHostEnabled ? $"http://{healthHost}:{healthPort}/ui" : "Disabled")}");

        
        Log.Information("");
    }
}