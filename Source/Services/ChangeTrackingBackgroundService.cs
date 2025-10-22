using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.Net.Http.Headers;
using Trignis.MicrosoftSQL.Models;
using System.Text.RegularExpressions;

namespace Trignis.MicrosoftSQL.Services;

public class ChangeTrackingBackgroundService : BackgroundService
{
    private readonly ILogger<ChangeTrackingBackgroundService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _stateConnectionString;
    private readonly long _maxExportDirectorySizeBytes;
    private readonly DeadLetterService _deadLetterService;
    private readonly MessageQueueService _messageQueueService;
    private readonly OAuth2TokenService _oauth2TokenService;
    private readonly GlobalSettings _globalSettings;
    private readonly List<EnvironmentConfig> _environments;
    private readonly Dictionary<string, bool> _isProcessing = new();
    private DateTime _lastPurgeTime = DateTime.MinValue;

    public ChangeTrackingBackgroundService(
        ILogger<ChangeTrackingBackgroundService> logger,
        IConfiguration config,
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        IHostApplicationLifetime lifetime,
        DeadLetterService deadLetterService,
        MessageQueueService messageQueueService,
        OAuth2TokenService oauth2TokenService)
    {
        _logger = logger;
        _logger.LogDebug("ChangeTrackingBackgroundService constructor called");
        _config = config;
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _lifetime = lifetime;
        _deadLetterService = deadLetterService;
        _messageQueueService = messageQueueService;
        _oauth2TokenService = oauth2TokenService;

        var stateDbPath = _config.GetValue<string>("ChangeTracking:StateDbPath", "state.db");
        _stateConnectionString = $"Data Source={stateDbPath}";

        var maxSizeMB = _config.GetValue<int>("ChangeTracking:FilePathSizeLimit", 500);
        _maxExportDirectorySizeBytes = maxSizeMB * 1024L * 1024L;

        // Load global settings
        _globalSettings = _config.GetSection("ChangeTracking:GlobalSettings").Get<GlobalSettings>() ?? new GlobalSettings();

        // Load environments
        _environments = _config.GetSection("ChangeTracking:Environments").Get<List<EnvironmentConfig>>() ?? new();
        
        foreach (var env in _environments)
        {
            _isProcessing[env.Name] = false;
        }
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

                    _logger.LogWarning("Old state database schema dropped. All tracking objects will perform initial sync.");
                    _logger.LogDebug("Old data backed up to LastVersions_Backup table");
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

            // If a backup exists, and migration is done successfully, drop it
            var checkBackupExists = conn.CreateCommand();
            checkBackupExists.Transaction = tx;
            checkBackupExists.CommandText = @"
                SELECT COUNT(*) FROM sqlite_master 
                WHERE type='table' AND name='LastVersions_Backup'
            ";
            var backupExists = (long)(await checkBackupExists.ExecuteScalarAsync() ?? 0L) > 0;

            if (backupExists)
            {
                var dropBackup = conn.CreateCommand();
                dropBackup.Transaction = tx;
                dropBackup.CommandText = "DROP TABLE LastVersions_Backup";
                await dropBackup.ExecuteNonQueryAsync();
                _logger.LogInformation("Backup table 'LastVersions_Backup' removed after successful migration.");
            }

            await tx.CommitAsync();
            _logger.LogDebug("State database initialized with environment support");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Database initialization failed; rolled back changes. Backup (if created) retained.");
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("Application is running in ExecuteAsync");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            // Purge old dead letters once per day
            if ((DateTime.UtcNow - _lastPurgeTime).TotalHours >= 24)
            {
                await _deadLetterService.PurgeOldDeadLettersAsync();
                _lastPurgeTime = DateTime.UtcNow;
            }

            // Create separate tasks for each environment
            var environmentTasks = _environments.Select(env => 
                ProcessEnvironmentAsync(env, stoppingToken)).ToArray();

            await Task.WhenAll(environmentTasks);
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

        _logger.LogDebug("Background service execution completed");
    }

    private async Task ProcessEnvironmentAsync(EnvironmentConfig environment, CancellationToken stoppingToken)
    {
        var pollingInterval = TimeSpan.FromSeconds(
            environment.ChangeTracking.PollingIntervalSeconds ?? _globalSettings.PollingIntervalSeconds);

        _logger.LogDebug($"Starting processing thread for environment '{environment.Name}' (Interval: {pollingInterval.TotalSeconds}s)");

        while (!stoppingToken.IsCancellationRequested)
        {
            _isProcessing[environment.Name] = true;
            var cycleStartTime = DateTime.UtcNow;
            
            try
            {
                _logger.LogDebug($"[{environment.Name}] Starting change tracking cycle at {cycleStartTime:HH:mm:ss}");

                foreach (var trackingObject in environment.ChangeTracking.TrackingObjects)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation($"[{environment.Name}] Cancellation requested, stopping current cycle");
                        break;
                    }

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
                        _logger.LogError(ex, $"[{environment.Name}] Error processing changes for object {trackingObject.Name}");
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
            finally
            {
                _isProcessing[environment.Name] = false;
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

    private AsyncRetryPolicy GetRetryPolicy(EnvironmentConfig environment, CancellationToken stoppingToken)
    {
        var retryCount = environment.ChangeTracking.RetryCount ?? _globalSettings.RetryCount;
        var retryDelay = TimeSpan.FromSeconds(environment.ChangeTracking.RetryDelaySeconds ?? _globalSettings.RetryDelaySeconds);
        
        return Policy
            .Handle<HttpRequestException>()
            .Or<IOException>()
            .Or<SqlException>()
            .WaitAndRetryAsync(retryCount, attempt => retryDelay, (exception, timeSpan, attempt, context) =>
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }
                _logger.LogWarning($"[{environment.Name}] Retry {attempt} after {timeSpan.TotalSeconds}s due to {exception.Message}");
            });
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

        var retryPolicy = GetRetryPolicy(environment, stoppingToken);
        await retryPolicy.ExecuteAsync(async () =>
        {
            // Modify connection string to handle large text
            var builder = new SqlConnectionStringBuilder(connectionString);

            builder.ApplicationName = "Trignis";

            if (!builder.ConnectionString.Contains("Packet Size"))
            {
                builder.PacketSize = 32768;  // Increase packet size for large data
                builder.ConnectTimeout = 30; // Set connection timeout
            }
            
            using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(stoppingToken);
            
            // Set TEXTSIZE to unlimited for this session
            using (var setCommand = new SqlCommand("SET TEXTSIZE 2147483647; SET ANSI_WARNINGS OFF;", conn))
            {
                await setCommand.ExecuteNonQueryAsync(stoppingToken);
            }
            
            var lastVersion = await GetLastProcessedVersionAsync(environment.Name, trackingObject.Name);

            int fromVersion;
            if (lastVersion == 0)
            {
                if (string.Equals(trackingObject.InitialSyncMode, "Full", StringComparison.OrdinalIgnoreCase))
                {
                    fromVersion = 0;
                    _logger.LogInformation($"[{environment.Name}] Performing initial full sync for {trackingObject.Name}");
                }
                else
                {
                    var currentVersion = await conn.ExecuteScalarAsync<long>("SELECT CHANGE_TRACKING_CURRENT_VERSION()");
                    lastVersion = (int)currentVersion;
                    await SetLastProcessedVersionAsync(environment.Name, trackingObject.Name, lastVersion);
                    fromVersion = lastVersion;
                    _logger.LogInformation($"[{environment.Name}] Initialized last processed version for {trackingObject.Name} to {lastVersion}");
                }
            }
            else
            {
                fromVersion = lastVersion;
            }

            var payload = new { fromVersion = fromVersion };
            var json = JsonSerializer.Serialize(payload);

            stoppingToken.ThrowIfCancellationRequested();

            // Build the SQL to wrap the SP call in a table variable for reliable retrieval. Source: https://stackoverflow.com/a/63090846
            var parameters = new DynamicParameters();
            parameters.Add("@JsonParam", json);  // Note: Renamed to avoid conflict with the table variable

            string result;

            var sql = $@"SET NOCOUNT ON; EXEC {trackingObject.StoredProcedureName} @Json = @JsonParam;";

            // Local helper to read potentially large NVARCHAR result from first column
            async Task<string> ReadClobAsync(System.Data.Common.DbDataReader reader, CancellationToken ct)
            {
                var sb = new StringBuilder();

                // Read each row in the resultset; SQL Server may return the JSON in 2k chunks, one chunk per row in the first column. Append each non-null chunk.
                while (await reader.ReadAsync(ct))
                {
                    if (await reader.IsDBNullAsync(0, ct))
                        continue;

                    // Use GetFieldValueAsync<string> to retrieve the text chunk efficiently
                    var chunk = await reader.GetFieldValueAsync<string>(0, ct);
                    if (!string.IsNullOrEmpty(chunk))
                        sb.Append(chunk);

                    ct.ThrowIfCancellationRequested();
                }

                return sb.ToString();
            }

            using (var reader = await conn.ExecuteReaderAsync(sql, parameters, commandTimeout: 300))
            {
                // Read the result from the first column
                result = await ReadClobAsync(reader, stoppingToken);
            }

            _logger.LogDebug($"[{environment.Name}] Retrieved {result.Length} characters from wrapped stored procedure {trackingObject.StoredProcedureName}");

            if (!string.IsNullOrEmpty(result))
            {
                _logger.LogDebug($"[{environment.Name}] Attempting to parse JSON ({result.Length} chars)");

                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(result);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, $"[{environment.Name}] Failed to parse JSON for {trackingObject.Name}. " +
                        $"Result length: {result.Length} chars. First 200 chars: {result.Substring(0, Math.Min(200, result.Length))}... " +
                        $"Last 200 chars: ...{(result.Length > 200 ? result.Substring(result.Length - 200) : "")}");

                    // Save the problematic JSON to debug folder for inspection
                    if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                    {
                        var debugDir = "debug";
                        if (!Directory.Exists(debugDir))
                        {
                            Directory.CreateDirectory(debugDir);
                        }

                        var debugPath = Path.Combine(debugDir, $"debug_{environment.Name}_{trackingObject.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}_partial.json");
                        await File.WriteAllTextAsync(debugPath, result);
                        _logger.LogDebug($"[{environment.Name}] Saved partial problematic JSON to: {debugPath}");
                    }
                    throw;
                }

                var metadata = doc.RootElement.GetProperty("Metadata");
                var sync = metadata.GetProperty("Sync");
                var version = sync.GetProperty("Version").GetInt32();

                if (doc.RootElement.TryGetProperty("Data", out var data))
                {
                    if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                    {
                        _logger.LogInformation($"[{environment.Name}]  ├─ Found {data.GetArrayLength()} changes at version {version}.");

                        stoppingToken.ThrowIfCancellationRequested();

                        var maxVersion = data.EnumerateArray().Max(e => e.GetProperty("$version").GetInt32());
                        await SetLastProcessedVersionAsync(environment.Name, trackingObject.Name, maxVersion);

                        await ExportChangesAsync(environment, trackingObject, data, stoppingToken);
                    }
                }

                await SetLastProcessedVersionAsync(environment.Name, trackingObject.Name, version);
            }
        });
    }

    private async Task<int> GetLastProcessedVersionAsync(string environmentName, string objectName)
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
        var result = await command.ExecuteScalarAsync();
        return result is long version ? (int)version : 0;
    }

    private async Task SetLastProcessedVersionAsync(string environmentName, string objectName, int version)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync();
        var command = conn.CreateCommand();
        command.CommandText = @"
            INSERT INTO LastVersions (EnvironmentName, ObjectName, LastVersion, LastUpdated)
            VALUES (@environmentName, @objectName, @version, CURRENT_TIMESTAMP)
            ON CONFLICT(EnvironmentName, ObjectName) 
            DO UPDATE SET 
                LastVersion = @version,
                LastUpdated = CURRENT_TIMESTAMP
        ";
        command.Parameters.AddWithValue("@environmentName", environmentName);
        command.Parameters.AddWithValue("@objectName", objectName);
        command.Parameters.AddWithValue("@version", version);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExportChangesAsync(EnvironmentConfig environment, TrackingObject trackingObject, JsonElement data, CancellationToken stoppingToken)
    {
        var exportToFile = environment.ChangeTracking.ExportToFile ?? _globalSettings.ExportToFile;
        var exportToApi = environment.ChangeTracking.ExportToApi ?? _globalSettings.ExportToApi;
        var retryPolicy = GetRetryPolicy(environment, stoppingToken);

        if (exportToFile)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                await retryPolicy.ExecuteAsync(async () => await ExportToFileAsync(environment, trackingObject, data));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{environment.Name}] File export failed for {trackingObject.Name}, saving to dead letter");
                await _deadLetterService.SaveDeadLetterAsync($"{environment.Name}_{trackingObject.Name}", trackingObject.Database, data, ex);
            }
        }

        if (exportToApi)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                await retryPolicy.ExecuteAsync(async () => await ExportToApiAsync(environment, trackingObject, data, stoppingToken));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{environment.Name}] API export failed for {trackingObject.Name}, saving to dead letter");
                await _deadLetterService.SaveDeadLetterAsync($"{environment.Name}_{trackingObject.Name}", trackingObject.Database, data, ex);
            }
        }
    }

    private async Task ExportToFileAsync(EnvironmentConfig environment, TrackingObject trackingObject, JsonElement data)
    {
        var filePathTemplate = environment.ChangeTracking.FilePath ?? _globalSettings.FilePath;
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var filePath = filePathTemplate
            .Replace("{timestamp}", timestamp)
            .Replace("{object}", trackingObject.Name)
            .Replace("{database}", trackingObject.Database)
            .Replace("{environment}", environment.Name);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
        _logger.LogInformation($"[{environment.Name}]  └─ Exported changes to file: {filePath}");

        CleanupOldFiles("exports", _maxExportDirectorySizeBytes);
    }

    private void CleanupOldFiles(string basePath, long maxSizeBytes)
    {
        if (!Directory.Exists(basePath))
            return;

        var allFiles = Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.CreationTime)
            .ToList();

        long currentSize = allFiles.Sum(f => f.Length);
        if (currentSize <= maxSizeBytes) return;

        _logger.LogInformation($"Export directory size {currentSize / 1024 / 1024} MB exceeds limit {maxSizeBytes / 1024 / 1024} MB. Cleaning up old files...");

        foreach (var file in allFiles)
        {
            if (currentSize <= maxSizeBytes) break;
            try
            {
                file.Delete();
                currentSize -= file.Length;
                _logger.LogInformation($"Deleted old export file: {file.FullName}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to delete file {file.FullName}");
            }
        }
    }

    private async Task ExportToApiAsync(EnvironmentConfig environment, TrackingObject trackingObject, JsonElement data, CancellationToken stoppingToken)
    {
        var apiEndpoints = environment.ChangeTracking.ApiEndpoints;
        if (apiEndpoints == null || apiEndpoints.Length == 0)
        {
            _logger.LogDebug($"[{environment.Name}] No API endpoints configured.");
            return;
        }

        var recordCount = data.GetArrayLength();
        var maxRecordsPerBatch = _globalSettings.MaxRecordsPerBatch;
        var enableBatching = _globalSettings.EnablePayloadBatching;

        // Check if we need to batch the payload
        if (enableBatching && recordCount > maxRecordsPerBatch)
        {
            _logger.LogInformation($"[{environment.Name}] Large payload detected ({recordCount} records). Batching into chunks of {maxRecordsPerBatch}...");
            
            var batches = data.EnumerateArray()
                .Select((record, index) => new { record, index })
                .GroupBy(x => x.index / maxRecordsPerBatch)
                .Select(g => g.Select(x => x.record).ToArray())
                .ToList();

            _logger.LogInformation($"[{environment.Name}] Split into {batches.Count} batches");

            for (int i = 0; i < batches.Count; i++)
            {
                stoppingToken.ThrowIfCancellationRequested();
                
                var batch = batches[i];
                var batchJson = JsonSerializer.Serialize(batch);
                var batchElement = JsonDocument.Parse(batchJson).RootElement;
                
                _logger.LogDebug($"[{environment.Name}] Sending batch {i + 1}/{batches.Count} ({batch.Length} records)");
                
                await SendToEndpointsAsync(environment, trackingObject, batchElement, apiEndpoints, i + 1, batches.Count);
            }
        }
        else
        {
            await SendToEndpointsAsync(environment, trackingObject, data, apiEndpoints, null, null);
        }
    }

    private async Task SendToEndpointsAsync(EnvironmentConfig environment, TrackingObject trackingObject, JsonElement data, ApiEndpoint[] endpoints, int? batchNumber, int? totalBatches)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        foreach (var endpoint in endpoints)
        {
            try
            {
                // Handle Message Queue endpoints
                if (!string.IsNullOrEmpty(endpoint.MessageQueueType))
                {
                    await _messageQueueService.SendToQueueAsync(endpoint, data);
                    continue;
                }

                // Handle HTTP endpoints
                if (string.IsNullOrEmpty(endpoint.Url))
                    continue;

                var apiUrl = endpoint.Url
                    .Replace("{timestamp}", Uri.EscapeDataString(timestamp))
                    .Replace("{object}", Uri.EscapeDataString(trackingObject.Name))
                    .Replace("{database}", Uri.EscapeDataString(trackingObject.Database))
                    .Replace("{environment}", Uri.EscapeDataString(environment.Name))
                    .Replace("{key}", Uri.EscapeDataString(endpoint.Key ?? ""));

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5); // Longer timeout for large payloads
                
                // Add authentication header
                if (endpoint.Auth != null && !string.IsNullOrEmpty(endpoint.Auth.Type))
                {
                    var authType = endpoint.Auth.Type.ToLower();
                    switch (authType)
                    {
                        case "bearer":
                            var token = endpoint.Auth.Token;
                            if (!string.IsNullOrEmpty(token))
                            {
                                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                            }
                            break;
                            
                        case "oauth2clientcredentials":
                            var cacheKey = $"{endpoint.Key}_{endpoint.Auth.ClientId}";
                            var accessToken = await _oauth2TokenService.GetAccessTokenAsync(endpoint.Auth, cacheKey);
                            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                            break;
                            
                        case "basic":
                            var username = endpoint.Auth.Username;
                            var password = endpoint.Auth.Password;
                            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                            {
                                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                            }
                            break;
                            
                        case "apikey":
                            var apiKey = endpoint.Auth.ApiKey;
                            var headerName = endpoint.Auth.HeaderName ?? "X-API-Key";
                            if (!string.IsNullOrEmpty(apiKey))
                            {
                                client.DefaultRequestHeaders.Add(headerName, apiKey);
                            }
                            break;
                    }
                }

                // Add custom headers
                if (endpoint.CustomHeaders != null)
                {
                    foreach (var header in endpoint.CustomHeaders)
                    {
                        var headerValue = header.Value
                            .Replace("{timestamp}", timestamp)
                            .Replace("{object}", trackingObject.Name)
                            .Replace("{database}", trackingObject.Database)
                            .Replace("{environment}", environment.Name)
                            .Replace("{guid}", Guid.NewGuid().ToString());
                            
                        if (batchNumber.HasValue && totalBatches.HasValue)
                        {
                            headerValue = headerValue
                                .Replace("{batch}", batchNumber.Value.ToString())
                                .Replace("{totalbatches}", totalBatches.Value.ToString());
                        }
                            
                        client.DefaultRequestHeaders.Add(header.Key, headerValue);
                    }
                }

                // Add batch info to headers if batching
                if (batchNumber.HasValue && totalBatches.HasValue)
                {
                    client.DefaultRequestHeaders.Add("X-Batch-Number", batchNumber.Value.ToString());
                    client.DefaultRequestHeaders.Add("X-Total-Batches", totalBatches.Value.ToString());
                }

                var jsonContent = JsonSerializer.Serialize(data);
                HttpContent content;

                // Apply compression if enabled
                if (endpoint.EnableCompression)
                {
                    var compressedBytes = CompressString(jsonContent);
                    content = new ByteArrayContent(compressedBytes);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    content.Headers.ContentEncoding.Add("gzip");
                    _logger.LogDebug($"[{environment.Name}] Compressed payload from {jsonContent.Length} to {compressedBytes.Length} bytes");
                }
                else
                {
                    content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                }

                var response = await client.PostAsync(apiUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"API export to '{endpoint.Key ?? apiUrl}' failed with status {response.StatusCode}");
                }

                var batchInfo = batchNumber.HasValue ? $" (Batch {batchNumber}/{totalBatches})" : "";
                _logger.LogInformation($"[{environment.Name}]  └─ Exported changes to API endpoint '{endpoint.Key ?? "unnamed"}'{batchInfo}: {apiUrl}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{environment.Name}] Export failed for endpoint '{endpoint.Key ?? "unnamed"}'");
                throw;
            }
        }
    }

    private byte[] CompressString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var outputStream = new System.IO.MemoryStream();
        using (var gzipStream = new System.IO.Compression.GZipStream(outputStream, System.IO.Compression.CompressionMode.Compress))
        {
            gzipStream.Write(bytes, 0, bytes.Length);
        }
        return outputStream.ToArray();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Exit: Background service is stopping...");
        
        try
        {
            // Wait for all environments to finish processing
            var timeout = TimeSpan.FromSeconds(30);
            var startWait = DateTime.UtcNow;
            
            while (_isProcessing.Any(kvp => kvp.Value) && (DateTime.UtcNow - startWait) < timeout)
            {
                await Task.Delay(500, cancellationToken);
            }

            if (_isProcessing.Any(kvp => kvp.Value))
            {
                _logger.LogWarning("Some environments did not complete within timeout, forcing shutdown");
            }

            _logger.LogDebug("Exit: Background service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during service shutdown");
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _logger.LogDebug("Disposing Background service resources");
        base.Dispose();
    }
}