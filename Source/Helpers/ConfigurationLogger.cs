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
        Log.Information("████████╗██████╗ ██╗ ██████╗ ███╗   ██╗██╗███████╗");
        Log.Information("╚══██╔══╝██╔══██╗██║██╔════╝ ████╗  ██║██║██╔════╝");
        Log.Information("   ██║   ██████╔╝██║██║  ███╗██╔██╗ ██║██║███████╗");
        Log.Information("   ██║   ██╔══██╗██║██║   ██║██║╚██╗██║██║╚════██║");
        Log.Information("   ██║   ██║  ██║██║╚██████╔╝██║ ╚████║██║███████║");
        Log.Information("   ╚═╝   ╚═╝  ╚═╝╚═╝ ╚═════╝ ╚═╝  ╚═══╝╚═╝╚══════╝");
        Log.Information("");
        Log.Information("Application is booting up...");
        Log.Information($" ├─ Version: {version}");
        Log.Information($" └─ Environment: {Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}");
        Log.Information("");
        Log.Information("[Configuration]");

        // Show loaded environments
        var loadedEnvironments = configuration.GetSection("ChangeTracking:LoadedEnvironments").Get<string[]>() ?? Array.Empty<string>();
        if (loadedEnvironments.Any())
        {
            Log.Information($"├─ Loaded Environment Files: {string.Join(", ", loadedEnvironments)}");
        }

        // Tracking Objects - grouped by environment
        var trackingObjects = configuration.GetSection("ChangeTracking:TrackingObjects").Get<TrackingObject[]>() ?? Array.Empty<TrackingObject>();
        Log.Information($"├─ Tracking Objects: {trackingObjects.Length}");
        
        var groupedByEnv = trackingObjects.GroupBy(o => string.IsNullOrEmpty(o.EnvironmentFile) ? "Unknown" : o.EnvironmentFile).OrderBy(g => g.Key);
        
        var envIndex = 0;
        var totalEnvs = groupedByEnv.Count();
        
        foreach (var envGroup in groupedByEnv)
        {
            envIndex++;
            var isLastEnv = envIndex == totalEnvs;
            var envPrefix = isLastEnv ? "└─" : "├─";
            var envVertical = isLastEnv ? " " : "│";
            
            Log.Information($"│  {envPrefix} Environment: [{envGroup.Key}] ({envGroup.Count()} objects)");
            
            var objects = envGroup.ToArray();
            for (int i = 0; i < objects.Length; i++)
            {
                var obj = objects[i];
                var isLastObj = i == objects.Length - 1;
                var objPrefix = isLastObj ? "└─" : "├─";
                
                var connString = configuration.GetConnectionString(obj.Database);
                if (string.IsNullOrEmpty(connString))
                {
                    Log.Warning($"│  {envVertical}  {objPrefix} ✖ '{obj.Name}' ({obj.TableName}): Database '{obj.Database}' connection missing");
                }
                else
                {
                    try
                    {
                        var builder = new SqlConnectionStringBuilder(connString);
                        Log.Information($"│  {envVertical}  {objPrefix} ✓ '{obj.Name}' ({obj.TableName}) – DB: {builder.InitialCatalog ?? "N/A"}, SP: {obj.StoredProcedureName}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"│  {envVertical}  {objPrefix} ✖ '{obj.Name}' ({obj.TableName}): Invalid connection - {ex.Message}");
                    }
                }
            }
        }

        // Change Tracking Settings
        var pollingInterval = configuration.GetValue<int>("ChangeTracking:PollingIntervalSeconds", 30);
        Log.Information($"│");
        Log.Information($"├─ Change Tracking:");
        Log.Information($"│  ├─ Polling Interval: {pollingInterval} seconds");

        // Export Configuration
        var exportToFile = configuration.GetValue<bool>("ChangeTracking:ExportToFile", false);
        var exportToApi = configuration.GetValue<bool>("ChangeTracking:ExportToApi", false);

        Log.Information($"│  └─ Export Destinations:");
        
        if (exportToFile)
        {
            var filePrefix = exportToApi ? "├─" : "└─";
            var filePath = configuration.GetValue<string>("ChangeTracking:FilePath", "exports/{object}/{database}/changes-{timestamp}.json");
            Log.Information($"│     {filePrefix} ✉ File Export: ENABLED");
            
            if (exportToApi)
            {
                Log.Information($"│     │  └─ Path: {filePath}");
                var maxSizeMB = configuration.GetValue<int>("ChangeTracking:FilePathSizeLimit", 500);
                Log.Information($"│     │  └─ Max Size: {maxSizeMB} MB");
            }
            else
            {
                Log.Information($"│        └─ Path: {filePath}");
                var maxSizeMB = configuration.GetValue<int>("ChangeTracking:FilePathSizeLimit", 500);
                Log.Information($"│        └─ Max Size: {maxSizeMB} MB");
            }
        }
        else
        {
            var filePrefix = exportToApi ? "├─" : "└─";
            Log.Information($"│     {filePrefix} 🗲 File Export: DISABLED");
        }

        if (exportToApi)
        {
            var apiEndpoints = configuration.GetSection("ChangeTracking:ApiEndpoints").Get<ApiEndpoint[]>();
            if (apiEndpoints != null && apiEndpoints.Length > 0)
            {
                Log.Information($"│     └─ ✉ API Export: ENABLED");
                for (int i = 0; i < apiEndpoints.Length; i++)
                {
                    var endpoint = apiEndpoints[i];
                    var isLastEndpoint = i == apiEndpoints.Length - 1;
                    var prefix = isLastEndpoint ? "└─" : "├─";
                    var verticalBar = isLastEndpoint ? " " : "│";
                    Log.Information($"│        {prefix} Endpoint '{endpoint.Key ?? $"#{i+1}"}'");
                    Log.Information($"│        {verticalBar}  ├─ URL: {endpoint.Url}");
                    Log.Information($"│        {verticalBar}  └─ Auth: {endpoint.Auth?.Type ?? "None"}");
                }
            }
            else
            {
                Log.Information($"│     └─ ✉ API Export: DISABLED (no endpoints configured)");
            }
        }
        else
        {
            Log.Information($"│     └─ ✉ API Export: DISABLED");
        }

        // Health Endpoint
        var healthEnabled = configuration.GetValue<bool>("Health:Enabled", false);
        var healthPort = configuration.GetValue<int>("Health:Port", 2455);
        var healthHost = configuration.GetValue<string>("Health:Host", "*");

        if (healthEnabled)
        {
            Log.Information($"│");
            Log.Information($"├─ Health Endpoint:");
            Log.Information($"│  ├─ Status: ENABLED");
            Log.Information($"│  └─ URL: http://{healthHost}:{healthPort}/health");
        }

        // Failover Settings
        var retryCount = configuration.GetValue<int>("ChangeTracking:RetryCount", 3);
        var retryDelay = configuration.GetValue<int>("ChangeTracking:RetryDelaySeconds", 5);
        Log.Information($"│");
        Log.Information($"└─ Failover Settings:");
        Log.Information($"   ├─ Retry Count: {retryCount}");
        Log.Information($"   └─ Retry Delay: {retryDelay} seconds");
        Log.Information("");
    }
}