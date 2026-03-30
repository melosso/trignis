using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Trignis.MicrosoftSQL.Helpers;
using Trignis.MicrosoftSQL.Models;

namespace Trignis.MicrosoftSQL.Services;

public class EnvironmentChangeEvent
{
    public IReadOnlyList<EnvironmentConfig> Added { get; init; } = [];
    public IReadOnlyList<EnvironmentConfig> Removed { get; init; } = [];
    public IReadOnlyList<EnvironmentConfig> Updated { get; init; } = [];
}

public class EnvironmentConfigService : IDisposable
{
    private readonly ILogger<EnvironmentConfigService> _logger;
    private readonly EncryptionService _encryptionService;

    private ImmutableList<EnvironmentConfig> _environments = ImmutableList<EnvironmentConfig>.Empty;
    private readonly object _lock = new();

    private string _envDir = "environments";
    private string? _selectedEnvironment;

    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, Timer> _debounceTimers = [];

    public IReadOnlyList<EnvironmentConfig> Environments => _environments;
    public event Action<EnvironmentChangeEvent>? ConfigurationChanged;

    public EnvironmentConfigService(
        ILogger<EnvironmentConfigService> logger,
        EncryptionService encryptionService)
    {
        _logger = logger;
        _encryptionService = encryptionService;
    }

    public void Initialize(List<EnvironmentConfig> environments, string envDir, string? selectedEnvironment = null)
    {
        _envDir = envDir;
        _selectedEnvironment = selectedEnvironment;
        lock (_lock) { _environments = ImmutableList.CreateRange(environments); }
    }

    public void StartWatching()
    {
        if (!Directory.Exists(_envDir)) return;

        _watcher = new FileSystemWatcher(_envDir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;

        _logger.LogInformation("Watching '{Dir}' for configuration changes", Path.GetFullPath(_envDir));
        _logger.LogInformation("");
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_debounceTimers)
        {
            if (_debounceTimers.TryGetValue(e.FullPath, out var existing)) existing.Dispose();
            _debounceTimers[e.FullPath] = new Timer(_ => HandleFileChange(e.FullPath), null, 500, Timeout.Infinite);
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileNameWithoutExtension(e.Name ?? "");
        if (string.IsNullOrEmpty(name)) return;

        List<EnvironmentConfig> removed;
        lock (_lock)
        {
            removed = _environments.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (removed.Count == 0) return;
            _environments = _environments.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        _logger.LogInformation("Environment '{Name}' removed (file deleted)", name);
        ConfigurationChanged?.Invoke(new EnvironmentChangeEvent { Removed = removed });
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        OnFileDeleted(sender, new FileSystemEventArgs(WatcherChangeTypes.Deleted, _envDir, e.OldName));
        if (e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            OnFileEvent(sender, e);
    }

    private void HandleFileChange(string fullPath)
    {
        lock (_debounceTimers) { _debounceTimers.Remove(fullPath); }

        var newEnv = LoadFile(fullPath);
        if (newEnv == null) return;

        // If a specific environment is selected, only react to that file
        if (!string.IsNullOrEmpty(_selectedEnvironment) &&
            !newEnv.Name.Equals(_selectedEnvironment, StringComparison.OrdinalIgnoreCase))
            return;

        List<EnvironmentConfig> added = [];
        List<EnvironmentConfig> updated = [];

        lock (_lock)
        {
            var existing = _environments.FirstOrDefault(x => x.Name.Equals(newEnv.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _environments = _environments.SetItem(_environments.IndexOf(existing), newEnv);
                updated.Add(newEnv);
            }
            else
            {
                _environments = _environments.Add(newEnv);
                added.Add(newEnv);
            }
        }

        if (added.Count > 0)
        {
            _logger.LogInformation("Environment '{Name}' added ({Objects} objects, {Endpoints} endpoints)",
                newEnv.Name, newEnv.ChangeTracking.TrackingObjects.Length, newEnv.ChangeTracking.ApiEndpoints.Length);
            ConfigurationChanged?.Invoke(new EnvironmentChangeEvent { Added = added });
        }
        else
        {
            _logger.LogInformation("Environment '{Name}' reloaded ({Objects} objects, {Endpoints} endpoints)",
                newEnv.Name, newEnv.ChangeTracking.TrackingObjects.Length, newEnv.ChangeTracking.ApiEndpoints.Length);
            ConfigurationChanged?.Invoke(new EnvironmentChangeEvent { Updated = updated });
        }
    }

    internal EnvironmentConfig? LoadFile(string fullPath)
    {
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var relativePath = Path.GetRelativePath(".", fullPath);

        try
        {
            var cfg = new ConfigurationBuilder()
                .AddEncryptedJsonFile(relativePath, _encryptionService, optional: true)
                .Build();

            var connectionStrings = new Dictionary<string, string>();
            foreach (var cs in cfg.GetSection("ConnectionStrings").GetChildren())
                if (!string.IsNullOrEmpty(cs.Value))
                    connectionStrings[cs.Key] = cs.Value;

            var ct = cfg.GetSection("ChangeTracking");

            var trackingObjects = (ct.GetSection("TrackingObjects").Get<TrackingObject[]>() ?? [])
                .Select(obj => obj with { EnvironmentFile = name })
                .ToArray();

            var apiEndpoints = (ct.GetSection("ApiEndpoints").Get<ApiEndpoint[]>() ?? [])
                .Select(ep => ep with { EnvironmentFile = name })
                .ToArray();

            var envConfig = new EnvironmentConfig
            {
                Name = name,
                ConnectionStrings = connectionStrings,
                ChangeTracking = new EnvironmentChangeTracking
                {
                    TrackingObjects = trackingObjects,
                    ApiEndpoints = apiEndpoints,
                    PollingIntervalSeconds = ct.GetValue<int?>("PollingIntervalSeconds"),
                    ExportToFile = ct.GetValue<bool?>("ExportToFile"),
                    FilePath = ct.GetValue<string?>("FilePath"),
                    ExportToApi = ct.GetValue<bool?>("ExportToApi"),
                    RetryCount = ct.GetValue<int?>("RetryCount"),
                    RetryDelaySeconds = ct.GetValue<int?>("RetryDelaySeconds")
                }
            };

            return envConfig;
        }
        catch (Exception ex)
        {
            _logger.LogError("Environment '{Name}': failed to load '{Path}': {Error}", name, relativePath, ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_debounceTimers)
        {
            foreach (var t in _debounceTimers.Values) t.Dispose();
            _debounceTimers.Clear();
        }
    }
}
