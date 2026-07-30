using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trignis.Models;
using Trignis.Services;

namespace Trignis.Tests.Stress;

// TEMPORARY harness for the concurrency stress tests
// Runs a real ChangeTrackingBackgroundService over a throwaway working directory
internal sealed class StressHost : IAsyncDisposable
{
    private readonly string _originalCwd;
    private readonly CancellationTokenSource _stop = new();
    private Task? _execute;

    public string Root { get; }
    public string EnvDir { get; }
    public EnvironmentConfigService ConfigService { get; }
    public ChangeTrackingBackgroundService Service { get; }

    // Set TRIGNIS_STRESS_LOG to a file path to capture service logs while debugging a stress run
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("TRIGNIS_STRESS_LOG");

    private static ILogger<T> Log<T>() =>
        LogPath is null ? NullLogger<T>.Instance : new FileLogger<T>(LogPath);

    public StressHost(int pollingIntervalSeconds = 1)
    {
        // The encrypted json provider roots its file provider at AppContext.BaseDirectory, so the
        // environment files have to live under it or they are silently skipped as optional
        _originalCwd = Environment.CurrentDirectory;
        Root = Path.Combine(AppContext.BaseDirectory, $"stress-{Guid.NewGuid():N}");
        EnvDir = Path.Combine(Root, "environments");
        Directory.CreateDirectory(EnvDir);
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        var settings = Options.Create(new GlobalSettings
        {
            PollingIntervalSeconds = pollingIntervalSeconds,
            RetryCount = 1,
            RetryDelaySeconds = 1
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChangeTracking:StateDbPath"] = Path.Combine(Root, "state.db")
            })
            .Build();

        var encryption = new EncryptionService(Root);
        ConfigService = new EnvironmentConfigService(Log<EnvironmentConfigService>(), encryption);

        var retries = new RetryPolicies(Log<RetryPolicies>(), settings);
        var deadLetters = new DeadLetterService(Log<DeadLetterService>(), settings);
        var exports = new ExportService(
            Log<ExportService>(),
            new StubHttpClientFactory(),
            new MessageQueueService(Log<MessageQueueService>()),
            new OAuth2TokenService(Log<OAuth2TokenService>(), new StubHttpClientFactory()),
            retries,
            settings);

        Service = new ChangeTrackingBackgroundService(
            Log<ChangeTrackingBackgroundService>(),
            config,
            settings,
            new StubLifetime(),
            deadLetters,
            exports,
            retries,
            ConfigService,
            new PauseService(config));
    }

    public async Task StartAsync()
    {
        var loaded = Directory.GetFiles(EnvDir, "*.json")
            .Select(ConfigService.LoadFile)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        ConfigService.Initialize(loaded, EnvDir);
        ConfigService.StartWatching();

        await Service.StartAsync(_stop.Token);
        _execute = Service.ExecuteTask;
    }

    // Reads the private lifecycle dictionary so a test can assert one live task per environment
    public IReadOnlyDictionary<string, Task> LiveTasks()
    {
        var field = typeof(ChangeTrackingBackgroundService)
            .GetField("_envTasks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var raw = (IDictionary)field.GetValue(Service)!;

        var result = new Dictionary<string, Task>();
        foreach (DictionaryEntry entry in raw)
        {
            var value = entry.Value!;
            var type = value.GetType();

            // Record exposes a Task property, the older ValueTuple exposes an Item2 field
            var task = (Task)(type.GetProperty("Task")?.GetValue(value)
                              ?? type.GetField("Item2")!.GetValue(value)!);

            result[(string)entry.Key] = task;
        }
        return result;
    }

    // Raises the hot-reload event the watcher would raise, without waiting on the 500ms debounce
    public void RaiseConfigurationChanged(EnvironmentChangeEvent e)
    {
        var field = typeof(EnvironmentConfigService)
            .GetField("ConfigurationChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;

        ((Action<EnvironmentChangeEvent>?)field.GetValue(ConfigService))?.Invoke(e);
    }

    public EnvironmentConfig Config(string name) =>
        ConfigService.Environments.First(x => x.Name == name);

    public string EnvFile(string name) => Path.Combine(EnvDir, $"{name}.json");

    public void WriteEnv(string name, string connectionString, string? storedProcedure, int pollingIntervalSeconds = 1)
    {
        var objects = storedProcedure is null
            ? "[]"
            : $$"""
                [{
                    "Name": "{{name}}_obj",
                    "Database": "probe",
                    "TableName": "dbo.Probe",
                    "StoredProcedureName": "{{storedProcedure}}",
                    "InitialSyncMode": "Full"
                  }]
                """;

        var json = $$"""
            {
              "Provider": "mssql",
              "ConnectionStrings": { "probe": "{{connectionString.Replace("\\", "\\\\")}}" },
              "ChangeTracking": {
                "PollingIntervalSeconds": {{pollingIntervalSeconds}},
                "ExportToFile": false,
                "ExportToApi": false,
                "TrackingObjects": {{objects}},
                "ApiEndpoints": []
              }
            }
            """;

        File.WriteAllText(EnvFile(name), json);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync();
            if (_execute is not null) await _execute.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch { /* shutdown is best effort in a stress harness */ }

        ConfigService.Dispose();
        Service.Dispose();
        _stop.Dispose();

        Environment.CurrentDirectory = _originalCwd;
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }
}
