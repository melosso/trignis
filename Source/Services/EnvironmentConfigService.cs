using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trignis.Helpers;
using Trignis.Models;

namespace Trignis.Services;

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
    private readonly GlobalSettings _globalSettings;

    // What a file held the last time it was applied. An editor saving twice, or our own
    // encrypt-on-reload rewrite, raises events for content that is already running.
    private readonly ConcurrentDictionary<string, string> _appliedHashes = new(StringComparer.OrdinalIgnoreCase);

    // Environment name -> why its last edit was refused. The previous version keeps running.
    private readonly ConcurrentDictionary<string, string> _loadErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Environments whose file on disk was rejected and are running an older version of it.</summary>
    public IReadOnlyDictionary<string, string> LoadErrors => _loadErrors;

    private volatile ImmutableList<EnvironmentConfig> _environments = ImmutableList<EnvironmentConfig>.Empty;
    private readonly object _lock = new();

    // Written by Initialize and read on watcher threads
    private volatile string _envDir = "environments";
    private volatile string? _selectedEnvironment;

    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, Timer> _debounceTimers = [];
    private bool _disposed;

    public IReadOnlyList<EnvironmentConfig> Environments => _environments;
    public event Action<EnvironmentChangeEvent>? ConfigurationChanged;

    public EnvironmentConfigService(
        ILogger<EnvironmentConfigService> logger,
        EncryptionService encryptionService,
        IOptions<GlobalSettings>? globalSettings = null)
    {
        _logger = logger;
        _encryptionService = encryptionService;
        _globalSettings = globalSettings?.Value ?? new GlobalSettings();
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
            if (_disposed) return;
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

        _loadErrors.TryRemove(name, out _);
        _appliedHashes.TryRemove(Path.GetFullPath(Path.Combine(_envDir, name + ".json")), out _);

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
        // Dispose on removal or every file change leaks a timer for the process lifetime
        lock (_debounceTimers)
        {
            if (_debounceTimers.Remove(fullPath, out var fired)) fired.Dispose();
            // A callback already in flight must not fire events after disposal
            if (_disposed) return;
        }

        // If a specific environment is selected, only react to that file
        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (!string.IsNullOrEmpty(_selectedEnvironment) &&
            !name.Equals(_selectedEnvironment, StringComparison.OrdinalIgnoreCase))
            return;

        // Deleted before the debounce fired. Loading it anyway yields an empty configuration,
        // which used to come back as a ghost environment with nothing in it.
        if (!File.Exists(fullPath)) return;

        // Same treatment a file gets at start-up: secrets encrypted, then validated
        _encryptionService.EncryptConfigFile(fullPath);

        string hash;
        try
        {
            hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Environment '{Name}': could not read '{Path}': {Error}", name, fullPath, ex.Message);
            return;
        }

        var hashKey = Path.GetFullPath(fullPath);
        if (_appliedHashes.TryGetValue(hashKey, out var applied) && applied == hash) return;

        var newEnv = LoadFile(fullPath, out var loadError);
        if (newEnv != null)
        {
            try
            {
                ConfigurationValidator.ValidateConfiguration([newEnv], _globalSettings, null);
            }
            catch (InvalidOperationException ex)
            {
                newEnv = null;
                loadError = ex.Message;
            }
        }

        if (newEnv == null)
        {
            _loadErrors[name] = loadError ?? "Failed to load";
            _logger.LogError("Environment '{Name}': the change was rejected and the previous configuration keeps running", name);
            return;
        }

        _loadErrors.TryRemove(name, out _);
        _appliedHashes[hashKey] = hash;

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

    internal EnvironmentConfig? LoadFile(string fullPath) => LoadFile(fullPath, out _);

    internal EnvironmentConfig? LoadFile(string fullPath, out string? error)
    {
        error = null;
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
                Provider = cfg.GetValue<string?>("Provider") ?? "mssql",
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
            error = ex.Message;
            return null;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_debounceTimers)
        {
            _disposed = true;
            foreach (var t in _debounceTimers.Values) t.Dispose();
            _debounceTimers.Clear();
        }
    }
}
