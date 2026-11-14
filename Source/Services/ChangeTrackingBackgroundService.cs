using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using System.Net.Http.Headers;
using Trignis.Data;
using Trignis.Helpers;
using Trignis.Models;

namespace Trignis.Services;

public class ChangeTrackingBackgroundService : BackgroundService
{
    private readonly ILogger<ChangeTrackingBackgroundService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _stateConnectionString;
    private readonly DeadLetterService _deadLetterService;
    private readonly ExportService _exportService;
    private readonly RetryPolicies _retryPolicies;
    private readonly GlobalSettings _globalSettings;
    private readonly EnvironmentConfigService _configService;
    private readonly PauseService _pauseService;
    private volatile CancellationTokenSource? _globalStoppingSource;

    // Copy of the global token so reload paths never touch a disposed source
    private CancellationToken _globalStoppingToken;

    private readonly Dictionary<string, EnvTask> _envTasks = new();

    // Serializes every _envTasks mutation including the awaits inside start and stop
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private sealed record EnvTask(CancellationTokenSource Cts, Task Task);

    // Loops that ignored their stop timeout. A replacement waits for its predecessor here, so
    // one environment never has two loops exporting at once. Guarded by _lifecycleGate.
    private readonly Dictionary<string, Task> _draining = new();

    private static readonly TimeSpan EnvTaskStopTimeout = TimeSpan.FromSeconds(5);

    public ChangeTrackingBackgroundService(
        ILogger<ChangeTrackingBackgroundService> logger,
        IConfiguration config,
        IOptions<GlobalSettings> globalSettings,
        IHostApplicationLifetime lifetime,
        DeadLetterService deadLetterService,
        ExportService exportService,
        RetryPolicies retryPolicies,
        EnvironmentConfigService configService,
        PauseService pauseService)
    {
        _logger = logger;
        _logger.LogDebug("ChangeTrackingBackgroundService constructor called");
        _lifetime = lifetime;
        _deadLetterService = deadLetterService;
        _exportService = exportService;
        _retryPolicies = retryPolicies;
        _configService = configService;
        _pauseService = pauseService;
        _globalSettings = globalSettings.Value;

        var stateDbPath = config.GetValue<string>("ChangeTracking:StateDbPath", "state.db");
        _stateConnectionString = $"Data Source={stateDbPath}";
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Initializing databases...");

        try
        {
            // Initialize state database
            await InitializeStateDbAsync();

            // Initialize dead letter database
            await _deadLetterService.InitializeAsync();

            // Pause state lives in the same file as the watermarks
            await _pauseService.InitializeAsync();

            _logger.LogDebug("Databases initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to initialize databases during startup");
            throw;
        }

        await base.StartAsync(cancellationToken);
    }

    private async Task InitializeStateDbAsync()
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync();

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

        try
        {
            // Check if table exists
            var checkTableExists = conn.CreateCommand();
            checkTableExists.Transaction = tx;
            checkTableExists.CommandText = @"
                SELECT COUNT(*) FROM sqlite_master 
                WHERE type='table' AND name='LastVersions'
            ";
            var tableExists = (long)(await checkTableExists.ExecuteScalarAsync() ?? 0L) > 0;

            if (tableExists)
            {
                // Check if old schema (missing EnvironmentName column)
                var checkColumnExists = conn.CreateCommand();
                checkColumnExists.Transaction = tx;
                checkColumnExists.CommandText = @"
                    SELECT COUNT(*) FROM pragma_table_info('LastVersions') 
                    WHERE name='EnvironmentName'
                ";
                var hasEnvironmentColumn = (long)(await checkColumnExists.ExecuteScalarAsync() ?? 0L) > 0;

                if (!hasEnvironmentColumn)
                {
                    _logger.LogInformation("Migrating state database to new schema with environment support...");

                    // Backup old data
                    var backupCommand = conn.CreateCommand();
                    backupCommand.Transaction = tx;
                    backupCommand.CommandText = @"
                        CREATE TABLE IF NOT EXISTS LastVersions_Backup AS 
                        SELECT * FROM LastVersions
                    ";
                    await backupCommand.ExecuteNonQueryAsync();

                    // Drop old table
                    var dropCommand = conn.CreateCommand();
                    dropCommand.Transaction = tx;
                    dropCommand.CommandText = "DROP TABLE LastVersions";
                    await dropCommand.ExecuteNonQueryAsync();

                    _logger.LogWarning("Old state database schema dropped. All tracking objects will perform initial sync. " +
                        "The previous watermarks are kept in the LastVersions_Backup table.");
                }
            }

            // Create new schema
            var createCommand = conn.CreateCommand();
            createCommand.Transaction = tx;
            createCommand.CommandText = @"
                CREATE TABLE IF NOT EXISTS LastVersions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    EnvironmentName TEXT NOT NULL,
                    ObjectName TEXT NOT NULL,
                    LastVersion INTEGER NOT NULL,
                    LastUpdated DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(EnvironmentName, ObjectName)
                );

                CREATE INDEX IF NOT EXISTS idx_environment_object 
                ON LastVersions(EnvironmentName, ObjectName);

                CREATE INDEX IF NOT EXISTS idx_last_updated 
                ON LastVersions(LastUpdated);
            ";
            await createCommand.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            _logger.LogDebug("State database initialized with environment support");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Database initialization failed; rolled back changes, the state database is untouched.");
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("Application is running in ExecuteAsync");
        _globalStoppingSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _globalStoppingToken = _globalStoppingSource.Token;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            await _deadLetterService.PurgeOldDeadLettersAsync(stoppingToken);

            // Subscribe inside the gate so no reload lands mid startup
            await _lifecycleGate.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                foreach (var env in _configService.Environments)
                    await StartEnvironmentTaskAsync(env, stoppingToken).ConfigureAwait(false);

                _configService.ConfigurationChanged += OnConfigurationChanged;
            }
            finally
            {
                _lifecycleGate.Release();
            }

            // Wait until shutdown is requested
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Background service execution cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in background service");
            _lifetime.StopApplication();
        }
        finally
        {
            _configService.ConfigurationChanged -= OnConfigurationChanged;

            // Cancel first so a queued reload sees a cancelled token and starts nothing
            _globalStoppingSource?.Cancel();

            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var entry in _envTasks.Values) entry.Cts.Cancel();
                foreach (var name in _envTasks.Keys.ToList())
                    await StopEnvironmentTaskAsync(name).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        _logger.LogDebug("Background service execution completed");
    }

    private void OnConfigurationChanged(EnvironmentChangeEvent e) => _ = ApplyConfigurationChangeAsync(e);

    // Rapid file saves queue on the gate instead of racing
    private async Task ApplyConfigurationChangeAsync(EnvironmentChangeEvent e)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var env in e.Removed)
                await StopEnvironmentTaskAsync(env.Name).ConfigureAwait(false);
            foreach (var env in e.Updated.Concat(e.Added))
                await StartEnvironmentTaskAsync(env, _globalStoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Configuration reload abandoned because the service is shutting down");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply a configuration change");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // Caller holds _lifecycleGate
    private async Task StartEnvironmentTaskAsync(EnvironmentConfig env, CancellationToken stoppingToken)
    {
        await StopEnvironmentTaskAsync(env.Name).ConfigureAwait(false);

        if (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Not starting environment '{Env}' because the service is stopping", env.Name);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _draining.Remove(env.Name, out var predecessor);
        _envTasks[env.Name] = new EnvTask(cts, predecessor is null or { IsCompleted: true }
            ? ProcessEnvironmentAsync(env, cts.Token)
            : RunAfterAsync(predecessor, env, cts.Token));
    }

    private async Task RunAfterAsync(Task predecessor, EnvironmentConfig env, CancellationToken stoppingToken)
    {
        _logger.LogWarning("[{Env}] Waiting for the previous processing loop to finish before starting the new one", env.Name);
        try
        {
            await predecessor.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Its failure was its own; ours is only to not overlap with it
            _logger.LogDebug(ex, "[{Env}] Previous processing loop ended with an error", env.Name);
        }

        await ProcessEnvironmentAsync(env, stoppingToken).ConfigureAwait(false);
    }

    // Caller holds _lifecycleGate
    private async Task StopEnvironmentTaskAsync(string envName)
    {
        if (!_envTasks.Remove(envName, out var entry)) return;

        entry.Cts.Cancel();
        try
        {
            await entry.Task.WaitAsync(EnvTaskStopTimeout).ConfigureAwait(false);
            entry.Cts.Dispose();
        }
        catch (Exception ex)
        {
            // Abandoned task still holds the token so dispose only once it finishes
            _logger.LogDebug(ex, "Task for {Env} did not stop cleanly within {Timeout}", envName, EnvTaskStopTimeout);
            _ = entry.Task.ContinueWith(_ => entry.Cts.Dispose(), TaskScheduler.Default);
            _draining[envName] = _draining.TryGetValue(envName, out var earlier) && !earlier.IsCompleted
                ? Task.WhenAll(earlier, entry.Task)
                : entry.Task;
        }
    }

    private async Task ProcessEnvironmentAsync(EnvironmentConfig environment, CancellationToken stoppingToken)
    {
        var pollingInterval = TimeSpan.FromSeconds(
            environment.ChangeTracking.PollingIntervalSeconds ?? _globalSettings.PollingIntervalSeconds);

        _logger.LogDebug($"Starting processing thread for environment '{environment.Name}' (Interval: {pollingInterval.TotalSeconds}s)");

        // Paused scopes are logged on transition only; at one cycle every 30s, logging the
        // steady state would bury everything else.
        var environmentWasPaused = false;
        var pausedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStartTime = DateTime.UtcNow;

            try
            {
                _logger.LogDebug($"[{environment.Name}] Starting change tracking cycle at {cycleStartTime:HH:mm:ss}");

                // One query per cycle, then in-memory checks per object
                var paused = await _pauseService.GetPausedScopesAsync(stoppingToken);

                var environmentPaused = paused.Contains(PauseService.EnvironmentScope(environment.Name));
                if (environmentPaused != environmentWasPaused)
                {
                    if (environmentPaused)
                        _logger.LogWarning($"[{environment.Name}] Environment is paused; no changes will be read or exported until it is resumed");
                    else
                        _logger.LogInformation($"[{environment.Name}] Environment resumed");
                    environmentWasPaused = environmentPaused;
                }

                if (environmentPaused)
                {
                    await Task.Delay(pollingInterval, stoppingToken);
                    continue;
                }

                foreach (var trackingObject in environment.ChangeTracking.TrackingObjects)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation($"[{environment.Name}] Cancellation requested, stopping current cycle");
                        break;
                    }

                    if (paused.Contains(PauseService.ObjectScope(environment.Name, trackingObject.Name)))
                    {
                        if (pausedObjects.Add(trackingObject.Name))
                            _logger.LogWarning($"[{environment.Name}] {trackingObject.Name} is paused; skipping it until it is resumed");
                        continue;
                    }

                    if (pausedObjects.Remove(trackingObject.Name))
                        _logger.LogInformation($"[{environment.Name}] {trackingObject.Name} resumed");

                    try
                    {
                        await ProcessChangesForObjectAsync(environment, trackingObject, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation($"[{environment.Name}] Processing cancelled for {trackingObject.Name}");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"[{environment.Name}] Error processing changes for object {trackingObject.Name}: {ex.Message}");
                    }
                }

                var cycleDuration = DateTime.UtcNow - cycleStartTime;
                _logger.LogDebug($"[{environment.Name}] Change tracking cycle completed in {cycleDuration.TotalSeconds:F2}s");
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug($"[{environment.Name}] Change tracking cycle cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{environment.Name}] Error during change tracking cycle");
            }

            try
            {
                _logger.LogDebug($"[{environment.Name}] Waiting {pollingInterval.TotalSeconds}s until next cycle...");
                await Task.Delay(pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug($"[{environment.Name}] Wait cancelled, exiting loop");
                break;
            }
        }

        _logger.LogDebug($"Environment '{environment.Name}' processing thread stopped");
    }

    private async Task ProcessChangesForObjectAsync(EnvironmentConfig environment, TrackingObject trackingObject, CancellationToken stoppingToken)
    {
        _logger.LogInformation($"[{environment.Name}] Processing changes for {trackingObject.Name} ({trackingObject.TableName})...");

        if (!environment.ConnectionStrings.TryGetValue(trackingObject.Database, out var connectionString))
        {
            _logger.LogWarning($"[{environment.Name}] Connection string for database '{trackingObject.Database}' not found.");
            return;
        }

        stoppingToken.ThrowIfCancellationRequested();

        var dialect = SqlDialect.Parse(environment.Provider);

        // Only the source read is retried. Exporting has its own per-destination retry, and
        // retrying the bookkeeping from here would send the payload to every destination again.
        var (result, expectedVersion, seeding) = await _retryPolicies.For(environment).ExecuteAsync(
            _ => ReadChangesAsync(environment, trackingObject, dialect, connectionString, stoppingToken), stoppingToken);

        _logger.LogDebug($"[{environment.Name}] Retrieved {result.Length} characters from wrapped stored procedure {trackingObject.StoredProcedureName}");

        if (string.IsNullOrEmpty(result))
            return;

        using var doc = await ParseResultAsync(environment, trackingObject, result);

        var version = doc.RootElement.GetProperty("Metadata").GetProperty("Sync").GetProperty("Version").GetInt64();
        var maxVersion = version;

        if (seeding)
        {
            // A procedure that honours mode: "seed" returns no rows. One that ignores it
            // returns history, which incremental mode exists to avoid — so drop it here
            // rather than flooding every destination on the first cycle.
            if (doc.RootElement.TryGetProperty("Data", out var seeded)
                && seeded.ValueKind == JsonValueKind.Array && seeded.GetArrayLength() > 0)
            {
                _logger.LogWarning(
                    $"[{environment.Name}] {trackingObject.Name} returned {seeded.GetArrayLength()} rows during an incremental seed; discarding them. " +
                    $"Have the procedure return no rows when mode is \"seed\", or set InitialSyncMode to \"Full\".");
            }
        }
        else if (doc.RootElement.TryGetProperty("Data", out var data)
            && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            _logger.LogInformation($"[{environment.Name}]  ├─ Found {data.GetArrayLength()} changes at version {version}.");

            stoppingToken.ThrowIfCancellationRequested();

            maxVersion = data.EnumerateArray()
                .Select(e => e.TryGetProperty("$version", out var v) ? v.GetInt64() : (long?)null)
                .Where(v => v.HasValue).Select(v => v!.Value)
                .DefaultIfEmpty(version).Max();

            // One dead letter per payload, remembering which destinations refused it, so a
            // replay goes only to those once the downstream problem is fixed.
            var failures = await _exportService.ExportAsync(environment, trackingObject, data, stoppingToken);
            if (failures.Count > 0)
            {
                var error = failures.Count == 1
                    ? failures[0].Error
                    : new AggregateException(string.Join("; ", failures.Select(f => $"{f.Target}: {f.Error.Message}")));

                await _deadLetterService.SaveDeadLetterAsync(
                    environment.Name, trackingObject.Name, trackingObject.Database, data, error, stoppingToken,
                    failures.Select(f => f.Target).Distinct().ToList());
            }
        }

        if (!await TryAdvanceVersionAsync(environment.Name, trackingObject.Name, expectedVersion, maxVersion))
        {
            _logger.LogWarning(
                $"[{environment.Name}] Sync state for {trackingObject.Name} changed while this cycle ran (reset from the dashboard?); " +
                "leaving it as it is. The next cycle picks up from the new state.");
        }
    }

    /// <summary>
    /// Runs the tracking procedure and returns its raw JSON, the watermark the cycle started
    /// from (null when the object has never synced), and whether this is an incremental seed.
    /// </summary>
    private async Task<(string Result, long? ExpectedVersion, bool Seeding)> ReadChangesAsync(
        EnvironmentConfig environment, TrackingObject trackingObject, SqlDialect dialect, string connectionString, CancellationToken stoppingToken)
    {
        using var conn = await dialect.OpenAsync(connectionString, stoppingToken);

        // Null means "never synced". Zero is a real watermark: a database that has seen no
        // changes yet reports it, and treating it as "never synced" re-seeded past the first ones.
        var lastVersion = await GetLastProcessedVersionAsync(environment.Name, trackingObject.Name);

        long fromVersion;

        // Seeding means "start from now": report the watermark, send no history.
        // The server tells us on platforms that track it; elsewhere the procedure does,
        // which is why the payload carries the mode.
        var seeding = false;

        if (lastVersion is { } known)
        {
            fromVersion = known;
        }
        else if (string.Equals(trackingObject.InitialSyncMode, "Full", StringComparison.OrdinalIgnoreCase))
        {
            fromVersion = 0;
            _logger.LogInformation($"[{environment.Name}] Performing initial full sync for {trackingObject.Name}");
        }
        else if (dialect.CurrentVersionSql is not null)
        {
            using var versionCommand = conn.CreateCommand();
            versionCommand.CommandText = dialect.CurrentVersionSql;
            // NULL when change tracking is off for the database; the procedure reports that itself
            var current = await versionCommand.ExecuteScalarAsync(stoppingToken);
            fromVersion = current is null or DBNull ? 0 : Convert.ToInt64(current);

            if (fromVersion == 0)
            {
                // Nothing has ever been tracked in this database. Procedures read fromVersion 0 as
                // "send the whole table", so there is no incremental watermark to hand out yet.
                // ponytail: changes committed in the same polling interval as the database's very
                // first tracked change are skipped; use InitialSyncMode "Full" if that matters.
                _logger.LogDebug($"[{environment.Name}] No changes tracked yet for {trackingObject.Name}; waiting for the first one");
                return (string.Empty, null, false);
            }

            if (await TryAdvanceVersionAsync(environment.Name, trackingObject.Name, null, fromVersion))
                lastVersion = fromVersion;
            else
                lastVersion = fromVersion = await GetLastProcessedVersionAsync(environment.Name, trackingObject.Name) ?? fromVersion;

            _logger.LogInformation($"[{environment.Name}] Initialized last processed version for {trackingObject.Name} to {fromVersion}");
        }
        else
        {
            fromVersion = 0;
            seeding = true;
            _logger.LogInformation($"[{environment.Name}] Seeding last processed version for {trackingObject.Name} from the procedure");
        }

        var json = JsonSerializer.Serialize(new { fromVersion, mode = seeding ? "seed" : "sync" });

        stoppingToken.ThrowIfCancellationRequested();

        using var spCommand = conn.CreateCommand();
        spCommand.CommandText = string.Format(dialect.CallProcedure, trackingObject.StoredProcedureName);
        spCommand.CommandTimeout = 300;

        var jsonParam = spCommand.CreateParameter();
        jsonParam.ParameterName = SqlDialect.JsonParameter;
        jsonParam.Value = json;
        spCommand.Parameters.Add(jsonParam);

        // SQL Server may return the JSON in 2k chunks, one per row in the first column
        var sb = new StringBuilder();
        using (var reader = await spCommand.ExecuteReaderAsync(stoppingToken))
        {
            while (await reader.ReadAsync(stoppingToken))
            {
                if (await reader.IsDBNullAsync(0, stoppingToken))
                    continue;

                sb.Append(await reader.GetFieldValueAsync<string>(0, stoppingToken));
            }
        }

        return (sb.ToString(), lastVersion, seeding);
    }

    private async Task<JsonDocument> ParseResultAsync(EnvironmentConfig environment, TrackingObject trackingObject, string result)
    {
        try
        {
            return JsonDocument.Parse(result);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, $"[{environment.Name}] Failed to parse JSON for {trackingObject.Name}. " +
                $"Result length: {result.Length} chars. First 200 chars: {result.Substring(0, Math.Min(200, result.Length))}... " +
                $"Last 200 chars: ...{(result.Length > 200 ? result.Substring(result.Length - 200) : "")}");

            // Save the problematic JSON to debug folder for inspection
            if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
            {
                Directory.CreateDirectory("debug");
                var debugPath = Path.Combine("debug", $"debug_{Path.GetFileName(environment.Name)}_{Path.GetFileName(trackingObject.Name)}_{DateTime.UtcNow:yyyyMMddHHmmss}_partial.json");
                await File.WriteAllTextAsync(debugPath, result);
                _logger.LogDebug($"[{environment.Name}] Saved partial problematic JSON to: {debugPath}");
            }
            throw;
        }
    }

    private async Task<long?> GetLastProcessedVersionAsync(string environmentName, string objectName)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync();
        var command = conn.CreateCommand();
        command.CommandText = @"
            SELECT LastVersion 
            FROM LastVersions 
            WHERE EnvironmentName = @environmentName 
            AND ObjectName = @objectName
        ";
        command.Parameters.AddWithValue("@environmentName", environmentName);
        command.Parameters.AddWithValue("@objectName", objectName);
        return await command.ExecuteScalarAsync() is long version ? version : null;
    }

    /// <summary>
    /// Compare-and-swap on the watermark: writes only if the row is still what the cycle read
    /// (<paramref name="expected"/> null meaning "no row"). False means someone else changed it
    /// in the meantime, typically a reset from the dashboard, and that change wins.
    /// </summary>
    private async Task<bool> TryAdvanceVersionAsync(string environmentName, string objectName, long? expected, long version)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync();
        var command = conn.CreateCommand();
        command.CommandText = expected is null
            ? @"INSERT INTO LastVersions (EnvironmentName, ObjectName, LastVersion, LastUpdated)
                VALUES (@environmentName, @objectName, @version, CURRENT_TIMESTAMP)
                ON CONFLICT(EnvironmentName, ObjectName) DO NOTHING"
            : @"UPDATE LastVersions
                SET LastVersion = @version, LastUpdated = CURRENT_TIMESTAMP
                WHERE EnvironmentName = @environmentName AND ObjectName = @objectName AND LastVersion = @expected";
        command.Parameters.AddWithValue("@environmentName", environmentName);
        command.Parameters.AddWithValue("@objectName", objectName);
        command.Parameters.AddWithValue("@version", version);
        if (expected is { } e) command.Parameters.AddWithValue("@expected", e);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Exit: Background service is stopping...");

        // base.StopAsync awaits ExecuteAsync, whose finally awaits every environment task
        await base.StopAsync(cancellationToken);

        _logger.LogDebug("Exit: Background service stopped");
    }

    public override void Dispose()
    {
        _logger.LogDebug("Disposing Background service resources");
        _globalStoppingSource?.Dispose();
        _lifecycleGate.Dispose();
        base.Dispose();
    }
}