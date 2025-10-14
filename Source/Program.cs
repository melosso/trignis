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

    // Load environment-specific configuration from Environments folder
    var envConfigPath = Path.Combine("Environments", $"{builder.Environment.EnvironmentName}.json");
    builder.Configuration.AddEncryptedJsonFile(envConfigPath, encryptionService, optional: true, reloadOnChange: true);

    // Load example.json if present
    builder.Configuration.AddEncryptedJsonFile("Environments/example.json", encryptionService, optional: true, reloadOnChange: true);

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
    builder.Services.AddSingleton(encryptionService);
    builder.Services.AddHttpClient();

    var app = builder.Build();

    // Log configuration status
    ConfigurationLogger.LogConfigurationStatus(app.Services.GetRequiredService<IConfiguration>());

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
    Log.Fatal(ex, "❌ Application terminated unexpectedly");
    
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