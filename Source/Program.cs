using Trignis.MicrosoftSQL.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.IO;
using Serilog;
using Serilog.Sinks.EventLog;
using Trignis.MicrosoftSQL.Helpers;
using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using System.Linq;
using Trignis.MicrosoftSQL.Models;
using System.Collections.Generic;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Http;

var tempConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .Build();

bool useEventLog = tempConfig.GetValue<bool>("Windows:UseEventLog", false);

var loggerConfig = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("log/trignis-.log", rollingInterval: RollingInterval.Day);

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && useEventLog)
{
    loggerConfig.WriteTo.EventLog(source: "Trignis", logName: "Application");
}

Log.Logger = loggerConfig.CreateLogger();

// Initialize encryption service
var encryptionService = new EncryptionService(AppContext.BaseDirectory);

// Encrypt config files if plain
encryptionService.EncryptConfigFiles();

try
{
    // Set the working directory to the executable's directory to ensure relative paths work correctly when running as a Windows service
    Environment.CurrentDirectory = AppContext.BaseDirectory;

    var builder = WebApplication.CreateBuilder(args);

    // Determine which environment file(s) to load
    var envDir = Directory.GetDirectories(".").FirstOrDefault(d => d.Equals("environments", StringComparison.OrdinalIgnoreCase)) ?? "environments";
    var selectedEnvironment = Environment.GetEnvironmentVariable("TRIGNIS_ENVIRONMENT") 
        ?? builder.Configuration.GetValue<string>("SelectedEnvironment");
    
    List<TrackingObject> allTrackingObjects = new();
    List<ApiEndpoint> allApiEndpoints = new();
    Dictionary<string, string> allConnectionStrings = new();
    List<string> loadedEnvironments = new();
    IConfiguration? firstEnvironmentConfig = null;
    
    if (Directory.Exists(envDir))
    {
        var jsonFiles = Directory.GetFiles(envDir, "*.json").OrderBy(f => Path.GetFileName(f)).ToList();
        
        // Filter files based on selected environment
        if (!string.IsNullOrEmpty(selectedEnvironment))
        {
            var targetFile = jsonFiles.FirstOrDefault(f => 
                Path.GetFileNameWithoutExtension(f).Equals(selectedEnvironment, StringComparison.OrdinalIgnoreCase));
            
            if (targetFile != null)
            {
                jsonFiles = new List<string> { targetFile };
                Log.Information($"Loading specific environment: {selectedEnvironment}");
            }
            else
            {
                Log.Warning($"Environment '{selectedEnvironment}' not found. Available: {string.Join(", ", jsonFiles.Select(f => Path.GetFileNameWithoutExtension(f)))}");
                Log.Information("Loading all environment files...");
            }
        }
        else
        {
            Log.Debug("No specific environment selected (TRIGNIS_ENVIRONMENT or SelectedEnvironment). Loading all environment files...");
        }

        foreach (var file in jsonFiles)
        {
            var relativePath = Path.GetRelativePath(".", file);
            var environmentName = Path.GetFileNameWithoutExtension(file);
            loadedEnvironments.Add(environmentName);
            
            // Load configuration WITH encrypted JSON support
            var tempBuilder = new ConfigurationBuilder();
            tempBuilder.AddEncryptedJsonFile(relativePath, encryptionService, optional: true);
            var tempCfg = tempBuilder.Build();
            
            // Collect connection strings from this file
            var connStrings = tempCfg.GetSection("ConnectionStrings").GetChildren();
            foreach (var connString in connStrings)
            {
                var key = connString.Key;
                var value = connString.Value;
                if (!string.IsNullOrEmpty(value))
                {
                    // If connection string already exists from another file, warn about conflict
                    if (allConnectionStrings.ContainsKey(key))
                    {
                        Log.Warning($"Connection string '{key}' from '{environmentName}' overwrites previous definition");
                    }
                    allConnectionStrings[key] = value;
                }
            }
            
            // Collect TrackingObjects from this file and tag with environment
            var trackingObjects = tempCfg.GetSection("ChangeTracking:TrackingObjects").Get<TrackingObject[]>() ?? Array.Empty<TrackingObject>();
            
            foreach (var obj in trackingObjects)
            {
                obj.EnvironmentFile = environmentName;
            }
            allTrackingObjects.AddRange(trackingObjects);
            
            // Collect ApiEndpoints from this file
            var apiEndpoints = tempCfg.GetSection("ChangeTracking:ApiEndpoints").Get<ApiEndpoint[]>() ?? Array.Empty<ApiEndpoint>();
            foreach (var endpoint in apiEndpoints)
            {
                endpoint.EnvironmentFile = environmentName;
            }
            allApiEndpoints.AddRange(apiEndpoints);
            
            Log.Debug($"Loaded environment file: {environmentName} ({trackingObjects.Length} objects, {apiEndpoints.Length} endpoints, {connStrings.Count()} connection strings)");
            
            // Store the first environment config for later use
            if (firstEnvironmentConfig == null)
            {
                firstEnvironmentConfig = tempCfg;
            }
        }
    }

    // Add connection strings to main configuration
    if (allConnectionStrings.Any())
    {
        var connectionStringsJson = JsonSerializer.Serialize(new { ConnectionStrings = allConnectionStrings });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(connectionStringsJson));
        builder.Configuration.AddJsonStream(stream);
    }

    // Add combined TrackingObjects and ApiEndpoints to configuration
    if (allTrackingObjects.Any() || allApiEndpoints.Any() || firstEnvironmentConfig != null)
    {
        object changeTrackingObj;
        if (firstEnvironmentConfig != null)
        {
            var ct = firstEnvironmentConfig.GetSection("ChangeTracking");
            changeTrackingObj = new
            {
                TrackingObjects = allTrackingObjects,
                ApiEndpoints = allApiEndpoints,
                LoadedEnvironments = loadedEnvironments,
                PollingIntervalSeconds = ct.GetValue<int>("PollingIntervalSeconds", 30),
                ExportToFile = ct.GetValue<bool>("ExportToFile", false),
                FilePath = ct.GetValue<string>("FilePath", "exports/{object}/{database}/changes-{timestamp}.json"),
                FilePathSizeLimit = ct.GetValue<int>("FilePathSizeLimit", 500),
                ExportToApi = ct.GetValue<bool>("ExportToApi", false),
                RetryCount = ct.GetValue<int>("RetryCount", 3),
                RetryDelaySeconds = ct.GetValue<int>("RetryDelaySeconds", 5)
            };
        }
        else
        {
            changeTrackingObj = new
            {
                TrackingObjects = allTrackingObjects,
                ApiEndpoints = allApiEndpoints,
                LoadedEnvironments = loadedEnvironments
            };
        }
        var combinedObj = new
        {
            ChangeTracking = changeTrackingObj
        };
        var combinedJson = JsonSerializer.Serialize(combinedObj);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(combinedJson));
        builder.Configuration.AddJsonStream(stream);
    }

    // Configure Serilog from configuration
    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services));

    // Use Windows Service hosting
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Trignis (Agent)";
    });

    // Configure shutdown timeout (default is 5 seconds, increase if needed)
    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    });

    // Add services to the container
    builder.Services.AddHostedService<ChangeTrackingBackgroundService>();
    builder.Services.AddSingleton<DeadLetterService>();
    builder.Services.AddSingleton<HealthCheckService>();
    builder.Services.AddSingleton(encryptionService);
    builder.Services.AddHttpClient();
        
    var app = builder.Build();

    // Configure health endpoint
    var healthEnabled = builder.Configuration.GetValue<bool>("Health:Enabled", false);
    var healthPort = builder.Configuration.GetValue<int>("Health:Port", 2455);
    var healthHost = builder.Configuration.GetValue<string>("Health:Host", "*");

    if (healthEnabled)
    {
        app.Urls.Add($"http://{healthHost}:{healthPort}");
        
        app.MapGet("/health", async (HealthCheckService healthService) =>
        {
            var health = await healthService.GetHealthStatusAsync();
            return Results.Content(health, "application/json");
        });
    }
    // Log configuration status
    ConfigurationLogger.LogConfigurationStatus(builder.Configuration);

    // Register shutdown handler for graceful exit
    var lifetime = app.Lifetime;
    
    lifetime.ApplicationStarted.Register(() =>
    {
        Log.Information("Application started successfully");
        Log.Information("");
    });

    lifetime.ApplicationStopping.Register(() =>
    {
        Log.Information("");
        Log.Information("Exit: Application is stopping...");
    });

    lifetime.ApplicationStopped.Register(() =>
    {
        Log.Information("");
        Log.Information("Application stopped");
    });

    // Run the application
    app.Run();
    
    Log.Information("Exit: Application shutdown complete");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    
    // If running as console, wait for user input
    if (!OperatingSystem.IsWindows() || Environment.UserInteractive)
    {
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
    
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}