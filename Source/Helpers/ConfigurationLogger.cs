using Microsoft.Extensions.Configuration;
using Serilog;
using Microsoft.Data.SqlClient;
using System;
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

        // Tracking Objects
        var trackingObjects = configuration.GetSection("ChangeTracking:TrackingObjects").Get<TrackingObject[]>() ?? Array.Empty<TrackingObject>();
        Log.Information($"├─ Tracking Objects: {trackingObjects.Length}");
        for (int i = 0; i < trackingObjects.Length; i++)
        {
            var obj = trackingObjects[i];
            var isLast = i == trackingObjects.Length - 1;
            var prefix = isLast ? "└─" : "├─";
            
            var connString = configuration.GetConnectionString(obj.Database);
            if (string.IsNullOrEmpty(connString))
            {
                Log.Warning($"│  {prefix} ❌ '{obj.Name}' ({obj.TableName}): Database '{obj.Database}' connection missing");
            }
            else
            {
                try
                {
                    var builder = new SqlConnectionStringBuilder(connString);
                    Log.Information($"│  {prefix} ✅ '{obj.Name}' ({obj.TableName}) → DB: {builder.InitialCatalog ?? "N/A"}, SP: {obj.StoredProcedureName}");
                }
                catch (Exception ex)
                {
                    Log.Error($"│  {prefix} ❌ '{obj.Name}' ({obj.TableName}): Invalid connection - {ex.Message}");
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
            Log.Information($"│     {filePrefix} 📁 File Export: ENABLED");
            
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
            Log.Information($"│     {filePrefix} 📁 File Export: DISABLED");
        }

        if (exportToApi)
        {
            var apiUrl = configuration.GetValue<string>("ChangeTracking:ApiUrl");
            var authType = configuration.GetValue<string>("ChangeTracking:ApiAuth:Type");
            Log.Information($"│     └─ 🌐 API Export: ENABLED");
            Log.Information($"│        ├─ URL: {apiUrl}");
            Log.Information($"│        └─ Auth: {authType ?? "None"}");
        }
        else
        {
            Log.Information($"│     └─ 🌐 API Export: DISABLED");
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
