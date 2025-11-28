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

Environment.CurrentDirectory = AppContext.BaseDirectory;

// Load .env file if it exists
var envPath = Path.Combine(AppContext.BaseDirectory, ".env");
if (File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed))
            continue;

        var parts = trimmed.Split('=', 2);
        if (parts.Length == 2)
        {
            var key = parts[0].Trim();
            var value = parts[1].Trim().Trim('"', '\'');
            Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);
        }
    }
}

var tempConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .Build();

bool useEventLog = tempConfig.GetValue<bool>("Windows:UseEventLog", false);

var logDirectory = Path.Combine(AppContext.BaseDirectory, "log");
if (!Directory.Exists(logDirectory))
{
    Directory.CreateDirectory(logDirectory);
}

// Configure initial logger (for `appsettings.json`)
var loggerConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(tempConfig);

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && useEventLog)
{
    loggerConfig.WriteTo.EventLog(source: "Trignis", logName: "Application");
}

Log.Logger = loggerConfig.CreateLogger();

Log.Information("");
Log.Information("████████╗██████╗ ██╗ ██████╗ ███╗   ██╗██╗███████╗");
Log.Information("╚══██╔══╝██╔══██╗██║██╔════╝ ████╗  ██║██║██╔════╝");
Log.Information("   ██║   ██████╔╝██║██║  ███╗██╔██╗ ██║██║███████╗");
Log.Information("   ██║   ██╔══██╗██║██║   ██║██║╚██╗██║██║╚════██║");
Log.Information("   ██║   ██║  ██║██║╚██████╔╝██║ ╚████║██║███████║");
Log.Information("   ╚═╝   ╚═╝  ╚═╝╚═╝ ╚═════╝ ╚═╝  ╚═══╝╚═╝╚══════╝");
Log.Information("");

// Initialize encryption service
var encryptionService = new EncryptionService(AppContext.BaseDirectory);

// Encrypt config files if plain
encryptionService.EncryptConfigFiles();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Initialize Serilog globally 
    builder.Host.UseSerilog();

    // Load global settings from appsettings.json first
    var globalSettings = builder.Configuration.GetSection("ChangeTracking:GlobalSettings").Get<GlobalSettings>() ?? new GlobalSettings();

    // Determine which environment file(s) to load
    var envDir = Directory.GetDirectories(".").FirstOrDefault(d => d.Equals("environments", StringComparison.OrdinalIgnoreCase)) ?? "environments";
    var selectedEnvironment = Environment.GetEnvironmentVariable("TRIGNIS_ENVIRONMENT") 
        ?? builder.Configuration.GetValue<string>("SelectedEnvironment");
    
    List<EnvironmentConfig> environments = new();
    
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
            Log.Debug("No specific environment selected. Loading all environment files...");
        }

        foreach (var file in jsonFiles)
        {
            var relativePath = Path.GetRelativePath(".", file);
            var environmentName = Path.GetFileNameWithoutExtension(file);
            
            // Load configuration
            var tempBuilder = new ConfigurationBuilder();
            tempBuilder.AddEncryptedJsonFile(relativePath, encryptionService, optional: true);
            var tempCfg = tempBuilder.Build();
            
            // Build environment config
            var envConfig = new EnvironmentConfig
            {
                Name = environmentName,
                ConnectionStrings = new Dictionary<string, string>(),
                ChangeTracking = new EnvironmentChangeTracking()
            };
            
            // Collect connection strings
            var connStrings = tempCfg.GetSection("ConnectionStrings").GetChildren();
            foreach (var connString in connStrings)
            {
                var key = connString.Key;
                var value = connString.Value;
                if (!string.IsNullOrEmpty(value))
                {
                    envConfig.ConnectionStrings[key] = value;
                }
            }
            
            // Collect ChangeTracking settings
            var ct = tempCfg.GetSection("ChangeTracking");
            
            // Load tracking objects
            var trackingObjects = ct.GetSection("TrackingObjects").Get<TrackingObject[]>() ?? Array.Empty<TrackingObject>();
            foreach (var obj in trackingObjects)
            {
                obj.EnvironmentFile = environmentName;
            }
            envConfig.ChangeTracking.TrackingObjects = trackingObjects;
            
            // Load API endpoints
            var apiEndpoints = ct.GetSection("ApiEndpoints").Get<ApiEndpoint[]>() ?? Array.Empty<ApiEndpoint>();
            foreach (var endpoint in apiEndpoints)
            {
                endpoint.EnvironmentFile = environmentName;
            }
            envConfig.ChangeTracking.ApiEndpoints = apiEndpoints;
            
            // Load environment-specific overrides (if present)
            envConfig.ChangeTracking.PollingIntervalSeconds = ct.GetValue<int?>("PollingIntervalSeconds");
            envConfig.ChangeTracking.ExportToFile = ct.GetValue<bool?>("ExportToFile");
            envConfig.ChangeTracking.FilePath = ct.GetValue<string?>("FilePath");
            envConfig.ChangeTracking.ExportToApi = ct.GetValue<bool?>("ExportToApi");
            envConfig.ChangeTracking.RetryCount = ct.GetValue<int?>("RetryCount");
            envConfig.ChangeTracking.RetryDelaySeconds = ct.GetValue<int?>("RetryDelaySeconds");
            
            environments.Add(envConfig);
            
            Log.Debug($"Loaded environment: {environmentName} ({trackingObjects.Length} objects, {apiEndpoints.Length} endpoints)");
        }
    }
    else
    {
        Log.Warning($"Environments directory '{envDir}' does not exist. Please create it and add environment configuration files.");
    }

    // Add environments to configuration
    if (environments.Any())
    {
        var combinedObj = new 
        { 
            ChangeTracking = new 
            { 
                GlobalSettings = globalSettings,
                Environments = environments 
            } 
        };
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNamingPolicy = null // Preserve property names as-is
        };
        var combinedJson = JsonSerializer.Serialize(combinedObj, options);
        
        Log.Debug("Environments configuration loaded successfully");
        
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(combinedJson));
        builder.Configuration.AddJsonStream(stream);
    }
    else
    {
        Log.Warning("No environments were loaded from the environments folder");
    }

    // Validate configuration
    ConfigurationValidator.ValidateConfiguration(builder.Configuration);

    // Log configuration status
    ConfigurationLogger.LogConfigurationStatus(builder.Configuration);

    // Use Windows Service hosting
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Trignis (Agent)";
    });

    // Configure shutdown timeout
    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    });

    // Register services
    builder.Services.AddHostedService<ChangeTrackingBackgroundService>();
    builder.Services.AddSingleton<DeadLetterQueueMonitor>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DeadLetterQueueMonitor>());
    builder.Services.AddSingleton<ConnectionHealthCheckService>();
    builder.Services.AddHostedService<ConnectionHealthCheckService>();
    builder.Services.AddSingleton<DeadLetterService>();
    builder.Services.AddSingleton<HealthCheckService>();
    builder.Services.AddSingleton<MessageQueueService>();
    builder.Services.AddSingleton<OAuth2TokenService>();
    builder.Services.AddSingleton(encryptionService);
    builder.Services.AddHttpClient();

    var app = builder.Build();

    // Configure health endpoint with enhanced details
    var healthEnabled = builder.Configuration.GetValue<bool>("Health:Enabled", false);
    var healthPort = builder.Configuration.GetValue<int>("Health:Port", 2455);
    var healthHost = builder.Configuration.GetValue<string>("Health:Host", "*");

    if (healthEnabled)
    {
        app.Urls.Add($"http://{healthHost}:{healthPort}");

        // API Discovery endpoint - lists all available endpoints
        app.MapGet("/", (HttpContext context) =>
        {
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var response = new
            {
                service = "trignis-service",
                version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                endpoints = new
                {
                    health_url = $"{baseUrl}/health",
                    deadletters_url = $"{baseUrl}/health/deadletters",
                    connections_url = $"{baseUrl}/health/connections",
                    state_url = $"{baseUrl}/health/state",
                    state_environment_url = $"{baseUrl}/health/state/{{environmentName}}"
                }
            };
            return Results.Json(response);
        });

        app.MapGet("/health", async (HealthCheckService healthService) =>
        {
            var health = await healthService.GetHealthStatusAsync();
            return Results.Content(health, "application/json");
        });

        app.MapGet("/health/deadletters", async (DeadLetterQueueMonitor dlqMonitor) =>
        {
            var stats = await dlqMonitor.GetStatsAsync();
            return Results.Json(stats);
        });

        app.MapGet("/health/connections", (ConnectionHealthCheckService connHealth) =>
        {
            var status = connHealth.GetHealthStatus();
            return Results.Json(status);
        });

        // State database endpoint showing tracking versions per environment
        app.MapGet("/health/state", async () =>
        {
            try
            {
                var stateDbPath = builder.Configuration.GetValue<string>("ChangeTracking:StateDbPath", "state.db");
                var connectionString = $"Data Source={stateDbPath}";

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                await conn.OpenAsync();

                // Get all state grouped by environment
                var command = conn.CreateCommand();
                command.CommandText = @"
                    SELECT 
                        EnvironmentName,
                        COUNT(*) as ObjectCount
                    FROM LastVersions
                    GROUP BY EnvironmentName
                    ORDER BY EnvironmentName
                ";

                var environments = new List<object>();

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var envName = reader.GetString(0);
                    var objectCount = reader.GetInt32(1);

                    // Get objects for this environment
                    var objectCommand = conn.CreateCommand();
                    objectCommand.CommandText = @"
                        SELECT 
                            ObjectName,
                            LastVersion,
                            LastUpdated
                        FROM LastVersions
                        WHERE EnvironmentName = @environmentName
                        ORDER BY ObjectName
                    ";
                    objectCommand.Parameters.AddWithValue("@environmentName", envName);

                    var objects = new List<object>();
                    using var objectReader = await objectCommand.ExecuteReaderAsync();
                    while (await objectReader.ReadAsync())
                    {
                        objects.Add(new
                        {
                            object_name = objectReader.GetString(0),
                            last_version = objectReader.GetInt32(1),
                            last_updated = objectReader.GetDateTime(2).ToString("yyyy-MM-ddTHH:mm:ssZ")
                        });
                    }

                    environments.Add(new
                    {
                        name = envName,
                        object_count = objectCount,
                        objects = objects
                    });
                }

                var response = new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    total_environments = environments.Count,
                    environments = environments
                };

                return Results.Json(response);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "Failed to read state database",
                    message = ex.Message
                });
            }
        });

        // Endpoint to query specific environment state
        app.MapGet("/health/state/{environmentName}", async (string environmentName) =>
        {
            try
            {
                var stateDbPath = builder.Configuration.GetValue<string>("ChangeTracking:StateDbPath", "state.db");
                var connectionString = $"Data Source={stateDbPath}";

                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                await conn.OpenAsync();

                var command = conn.CreateCommand();
                command.CommandText = @"
                    SELECT 
                        ObjectName,
                        LastVersion,
                        LastUpdated
                    FROM LastVersions
                    WHERE EnvironmentName = @environmentName
                    ORDER BY ObjectName
                ";
                command.Parameters.AddWithValue("@environmentName", environmentName);

                var objects = new List<object>();

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    objects.Add(new
                    {
                        object_name = reader.GetString(0),
                        last_version = reader.GetInt32(1),
                        last_updated = reader.GetDateTime(2).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    });
                }

                if (objects.Count == 0)
                {
                    return Results.NotFound(new
                    {
                        error = "Environment not found",
                        environment = environmentName
                    });
                }

                var response = new
                {
                    environment = environmentName,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    object_count = objects.Count,
                    objects = objects
                };

                return Results.Json(response);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "Failed to read state database",
                    message = ex.Message
                });
            }
        });

        // 404 handler for all other routes
        app.MapFallback((HttpContext context) =>
        {
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            return Results.NotFound(new
            {
                error = "Not Found",
                message = $"The requested endpoint '{context.Request.Path}' does not exist"
            });
        });
    }
    
    // Register shutdown handlers
    var lifetime = app.Lifetime;
    
    lifetime.ApplicationStarted.Register(() =>
    {
        Log.Information("✓ Application started successfully");
        Log.Information("");
    });

    lifetime.ApplicationStopping.Register(() =>
    {
        Log.Information("");
        Log.Information("Exit: Application is stopping...");
    });

    lifetime.ApplicationStopped.Register(() =>
    {
        Log.Information("Application stopped");
    });

    // Run the application
    app.Run();
    
    Log.Information("Exit: Application shutdown complete");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    
    // Show error if Serilog fails
    Console.WriteLine("");
    Console.WriteLine("Fatal error during application startup:");
    Console.WriteLine(ex.ToString());
    
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