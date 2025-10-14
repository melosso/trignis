using System;
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

namespace Trignis.MicrosoftSQL.Services;

public class ChangeTrackingBackgroundService : BackgroundService
{
    private readonly ILogger<ChangeTrackingBackgroundService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly string _stateConnectionString;
    private readonly long _maxExportDirectorySizeBytes;
    private bool _isProcessing = false;

    public ChangeTrackingBackgroundService(
        ILogger<ChangeTrackingBackgroundService> logger,
        IConfiguration config,
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _config = config;
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _lifetime = lifetime;

        var stateDbPath = _config.GetValue<string>("ChangeTracking:StateDbPath", "state.db");
        _stateConnectionString = $"Data Source={stateDbPath}";

        var maxSizeMB = _config.GetValue<int>("ChangeTracking:FilePathSizeLimit", 500);
        _maxExportDirectorySizeBytes = maxSizeMB * 1024L * 1024L;

        var retryCount = _config.GetValue<int>("ChangeTracking:RetryCount", 3);
        var retryDelay = TimeSpan.FromSeconds(_config.GetValue<int>("ChangeTracking:RetryDelaySeconds", 5));
        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<IOException>()
            .Or<SqlException>()
            .WaitAndRetryAsync(retryCount, attempt => retryDelay, (exception, timeSpan, attempt, context) =>
            {
                _logger.LogWarning($"Retry {attempt} after {timeSpan.TotalSeconds}s due to {exception.Message}");
            });

        _maxExportDirectorySizeBytes = _config.GetValue<long>("ChangeTracking:MaxExportDirectorySizeBytes", 104857600); // Default: 100 MB
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Background service is starting...");
        
        try
        {
            // Initialize state database
            await InitializeStateDbAsync();
            _logger.LogDebug("State database initialized");
            
            // Validate configuration
            var trackingObjects = _config.GetSection("ChangeTracking:TrackingObjects").Get<TrackingObject[]>() ?? Array.Empty<TrackingObject>();
            if (trackingObjects.Length == 0)
            {
                throw new InvalidOperationException("No tracking objects configured. Please check the 'ChangeTracking:TrackingObjects' section in your configuration.");
            }
            
            _logger.LogDebug($"Configuration validated - {trackingObjects.Length} tracking object(s) configured");
            
            await base.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to start service!");
            throw;
        }
    }

    private async Task InitializeStateDbAsync()
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync();
        var command = conn.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS LastVersions (
                ObjectName TEXT PRIMARY KEY,
                LastVersion INTEGER NOT NULL
            );
        ";
        await command.ExecuteNonQueryAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("Application is running in ExecuteAsync");

        try
        {
            // Small delay to ensure everything is ready
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            var interval = TimeSpan.FromSeconds(_config.GetValue<int>("ChangeTracking:PollingIntervalSeconds", 30));
            var trackingObjects = _config.GetSection("ChangeTracking:TrackingObjects").Get<TrackingObject[]>() ?? Array.Empty<TrackingObject>();

            while (!stoppingToken.IsCancellationRequested)
            {
                _isProcessing = true;
                var cycleStartTime = DateTime.UtcNow;
                
                try
                {
                    _logger.LogDebug($"Starting change tracking cycle at {cycleStartTime:HH:mm:ss}");

                    foreach (var trackingObject in trackingObjects)
                    {
                        // Check for cancellation before processing each object
                        if (stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogInformation("Cancellation requested, stopping current cycle");
                            break;
                        }

                        try
                        {
                            await ProcessChangesForObjectAsync(trackingObject, stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogInformation($"Processing cancelled for {trackingObject.Name}");
                            throw; // Re-throw to break outer loop
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error processing changes for object {trackingObject.Name}");
                            // Continue to next object instead of stopping entire service
                        }
                    }

                    var cycleDuration = DateTime.UtcNow - cycleStartTime;
                    _logger.LogDebug($"Change tracking cycle completed in {cycleDuration.TotalSeconds:F2}s");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Change tracking cycle cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during change tracking cycle");
                }
                finally
                {
                    _isProcessing = false;
                }

                // Wait for next interval or cancellation
                try
                {
                    _logger.LogDebug($"Waiting {interval.TotalSeconds}s until next cycle...");
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Wait cancelled, exiting loop");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Background service execution cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in background service");
            
            // Signal application to stop on fatal error
            _lifetime.StopApplication();
        }

        _logger.LogDebug("Background service execution completed");
    }

    private async Task ProcessChangesForObjectAsync(TrackingObject trackingObject, CancellationToken stoppingToken)
    {
        _logger.LogInformation($"Processing changes for {trackingObject.Name} ({trackingObject.TableName})...");

        var connectionString = _config.GetConnectionString(trackingObject.Database);
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogWarning($"Connection string for database '{trackingObject.Database}' not found.");
            return;
        }

        // Check cancellation before starting
        stoppingToken.ThrowIfCancellationRequested();

        await _retryPolicy.ExecuteAsync(async () =>
        {
            using var conn = new SqlConnection(connectionString);
            var lastVersion = await GetLastProcessedVersionAsync(trackingObject.Name);

            // On first run, initialize last version to current change tracking version
            if (lastVersion == 0)
            {
                var currentVersion = await conn.ExecuteScalarAsync<long>("SELECT CHANGE_TRACKING_CURRENT_VERSION()");
                lastVersion = (int)currentVersion;
                await SetLastProcessedVersionAsync(trackingObject.Name, lastVersion);
                _logger.LogInformation($"Initialized last processed version for {trackingObject.Name} to {lastVersion} (current database version)");
            }

            var payload = new { fromVersion = lastVersion };
            var json = JsonSerializer.Serialize(payload);

            DynamicParameters parameters = new DynamicParameters();
            parameters.Add("Json", json);

            // Check cancellation before database call
            stoppingToken.ThrowIfCancellationRequested();

            string result = string.Empty;
            using (var command = new SqlCommand(trackingObject.StoredProcedureName, conn))
            {
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.AddWithValue("@Json", json);
                await conn.OpenAsync();
                using (var reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        // Read large string data in chunks to avoid loading entire value into memory at once
                        var sb = new StringBuilder();
                        char[] buffer = new char[4096];
                        long fieldOffset = 0;
                        int charsRead;
                        do
                        {
                            charsRead = (int)reader.GetChars(0, fieldOffset, buffer, 0, buffer.Length);
                            if (charsRead > 0)
                            {
                                sb.Append(buffer, 0, charsRead);
                                fieldOffset += charsRead;
                            }
                        } while (charsRead == buffer.Length);
                        result = sb.ToString();
                    }
                }
            }

            if (!string.IsNullOrEmpty(result))
            {
                var doc = JsonDocument.Parse(result);
                var metadata = doc.RootElement.GetProperty("Metadata");
                var sync = metadata.GetProperty("Sync");
                var version = sync.GetProperty("Version").GetInt32();

                if (doc.RootElement.TryGetProperty("Data", out var data))
                {
                    if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                    {
                        _logger.LogInformation($" ├─ Found {data.GetArrayLength()} changes in {trackingObject.Name} ({trackingObject.TableName}) at version {version}.");

                        // Check cancellation before export
                        stoppingToken.ThrowIfCancellationRequested();

                        // Update last version to the max version of the changes before exporting
                        var maxVersion = data.EnumerateArray().Max(e => e.GetProperty("$version").GetInt32());
                        await SetLastProcessedVersionAsync(trackingObject.Name, maxVersion);

                        // Export logic here
                        await ExportChangesAsync(trackingObject, data, stoppingToken);
                    }
                }

                // Update last version to current if no changes or to ensure it's up to date
                await SetLastProcessedVersionAsync(trackingObject.Name, version);
            }
        });
    }

    private async Task<int> GetLastProcessedVersionAsync(string objectName)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync();
        var command = conn.CreateCommand();
        command.CommandText = "SELECT LastVersion FROM LastVersions WHERE ObjectName = @objectName";
        command.Parameters.AddWithValue("@objectName", objectName);
        var result = await command.ExecuteScalarAsync();
        return result is long version ? (int)version : 0;
    }

    private async Task SetLastProcessedVersionAsync(string objectName, int version)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync();
        var command = conn.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO LastVersions (ObjectName, LastVersion)
            VALUES (@objectName, @version)
        ";
        command.Parameters.AddWithValue("@objectName", objectName);
        command.Parameters.AddWithValue("@version", version);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExportChangesAsync(TrackingObject trackingObject, JsonElement data, CancellationToken stoppingToken)
    {
        var exportToFile = _config.GetValue<bool>("ChangeTracking:ExportToFile", false);
        var exportToApi = _config.GetValue<bool>("ChangeTracking:ExportToApi", false);

        if (exportToFile)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await _retryPolicy.ExecuteAsync(async () => await ExportToFileAsync(trackingObject, data));
        }

        if (exportToApi)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await _retryPolicy.ExecuteAsync(async () => await ExportToApiAsync(trackingObject, data));
        }
    }

    private long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        long size = 0;
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            size += info.Length;
        }
        return size;
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

        _logger.LogInformation($"Cleanup complete. Directory size now {currentSize / 1024 / 1024} MB.");
    }

    private async Task ExportToFileAsync(TrackingObject trackingObject, JsonElement data)
    {
        var filePathTemplate = _config.GetValue<string>("ChangeTracking:FilePath", "exports/{object}/{database}/changes-{timestamp}.json");
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var filePath = filePathTemplate
            .Replace("{timestamp}", timestamp)
            .Replace("{object}", trackingObject.Name)
            .Replace("{database}", trackingObject.Database);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
        _logger.LogInformation($" └─ Exported changes for {trackingObject.Name} to file: {filePath}");

        CleanupOldFiles("exports", _maxExportDirectorySizeBytes);
    }

    private async Task ExportToApiAsync(TrackingObject trackingObject, JsonElement data)
    {
        var apiUrlTemplate = _config.GetValue<string>("ChangeTracking:ApiUrl");
        if (string.IsNullOrEmpty(apiUrlTemplate))
        {
            _logger.LogWarning("API URL not configured for export.");
            return;
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var apiUrl = apiUrlTemplate
            .Replace("{timestamp}", Uri.EscapeDataString(timestamp))
            .Replace("{object}", Uri.EscapeDataString(trackingObject.Name))
            .Replace("{database}", Uri.EscapeDataString(trackingObject.Database));

        var client = _httpClientFactory.CreateClient();
        
        // Add authentication header
        var authType = _config.GetValue<string>("ChangeTracking:ApiAuth:Type");
        if (!string.IsNullOrEmpty(authType))
        {
            switch (authType.ToLower())
            {
                case "bearer":
                    var token = _config.GetValue<string>("ChangeTracking:ApiAuth:Token");
                    if (!string.IsNullOrEmpty(token))
                    {
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }
                    break;
                case "basic":
                    var username = _config.GetValue<string>("ChangeTracking:ApiAuth:Username");
                    var password = _config.GetValue<string>("ChangeTracking:ApiAuth:Password");
                    if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                    {
                        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                    }
                    break;
                case "apikey":
                    var apiKey = _config.GetValue<string>("ChangeTracking:ApiAuth:ApiKey");
                    var headerName = _config.GetValue<string>("ChangeTracking:ApiAuth:HeaderName", "X-API-Key");
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Add(headerName, apiKey);
                    }
                    break;
                default:
                    _logger.LogWarning($"Unsupported authentication type: {authType}");
                    break;
            }
        }

        var content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(apiUrl, content);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"API export failed with status {response.StatusCode}");
        }

        _logger.LogInformation(" └─ Exported changes to API.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Exit: Background service is stopping...");
        
        try
        {
            // Wait for current processing to complete (with timeout)
            if (_isProcessing)
            {
                _logger.LogInformation("Waiting for current processing to complete...");
                var timeout = TimeSpan.FromSeconds(20);
                var startWait = DateTime.UtcNow;
                
                while (_isProcessing && (DateTime.UtcNow - startWait) < timeout)
                {
                    await Task.Delay(500, cancellationToken);
                }

                if (_isProcessing)
                {
                    _logger.LogWarning("Processing did not complete within timeout, forcing shutdown");
                }
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