using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trignis.Helpers;
using Trignis.Models;

namespace Trignis.Services;

/// <summary>Which target failed, and why. One entry per export destination.</summary>
public sealed record ExportFailure(string Target, Exception Error);

/// <summary>
/// Broadcasts the change payload to all configured destinations.  
/// Failures are returned to the caller for context-specific handling.
/// </summary>
public sealed class ExportService
{
    private readonly ILogger<ExportService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MessageQueueService _messageQueueService;
    private readonly OAuth2TokenService _oauth2TokenService;
    private readonly RetryPolicies _retryPolicies;
    private readonly GlobalSettings _globalSettings;
    private readonly long _maxExportDirectorySizeBytes;

    public ExportService(
        ILogger<ExportService> logger,
        IHttpClientFactory httpClientFactory,
        MessageQueueService messageQueueService,
        OAuth2TokenService oauth2TokenService,
        RetryPolicies retryPolicies,
        IOptions<GlobalSettings> globalSettings)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _messageQueueService = messageQueueService;
        _oauth2TokenService = oauth2TokenService;
        _retryPolicies = retryPolicies;
        _globalSettings = globalSettings.Value;
        _maxExportDirectorySizeBytes = _globalSettings.FilePathSizeLimit * 1024L * 1024L;
    }

    private const string FileTarget = "FILE";

    // ponytail: endpoints without a Key share the name "unnamed", so a replay narrowed to one of
    // them resends to all of them. Give endpoints a Key to tell them apart.
    private static string TargetName(ApiEndpoint endpoint) =>
        $"{(string.IsNullOrEmpty(endpoint.MessageQueueType) ? "HTTP" : "MQ")}:{endpoint.Key ?? "unnamed"}";

    private bool FileEnabled(EnvironmentConfig environment) =>
        environment.ChangeTracking.ExportToFile ?? _globalSettings.ExportToFile;

    private ApiEndpoint[] EnabledEndpoints(EnvironmentConfig environment) =>
        (environment.ChangeTracking.ExportToApi ?? _globalSettings.ExportToApi)
            ? environment.ChangeTracking.ApiEndpoints ?? Array.Empty<ApiEndpoint>()
            : Array.Empty<ApiEndpoint>();

    /// <summary>Names of the destinations an export for this environment would write to.</summary>
    public IReadOnlySet<string> ConfiguredTargets(EnvironmentConfig environment)
    {
        var targets = new HashSet<string>(EnabledEndpoints(environment).Select(TargetName), StringComparer.Ordinal);
        if (FileEnabled(environment)) targets.Add(FileTarget);
        return targets;
    }

    /// <param name="onlyTargets">Restricts the export to these destinations. Null means all of them.</param>
    public async Task<IReadOnlyList<ExportFailure>> ExportAsync(
        EnvironmentConfig environment, TrackingObject trackingObject, JsonElement data, CancellationToken stoppingToken,
        IReadOnlyCollection<string>? onlyTargets = null)
    {
        var failures = new List<ExportFailure>();

        bool Wanted(string target) => onlyTargets == null || onlyTargets.Contains(target);

        var exportToFile = FileEnabled(environment) && Wanted(FileTarget);
        var retryPolicy = _retryPolicies.For(environment);

        var apiEndpoints = EnabledEndpoints(environment).Where(e => Wanted(TargetName(e))).ToArray();
        var totalExports = (exportToFile ? 1 : 0) + apiEndpoints.Length;
        var currentExportIndex = 0;

        if (exportToFile)
        {
            currentExportIndex++;
            var prefix = currentExportIndex == totalExports ? "└─" : "├─";

            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                var filePath = await retryPolicy.ExecuteAsync(
                    async _ => await ExportToFileAsync(environment, trackingObject, data), stoppingToken);
                _logger.LogInformation($"[{environment.Name}]  {prefix} [FILE] Exported to: {filePath}");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug($"[{environment.Name}]  {prefix} File export cancelled due to shutdown");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{environment.Name}]  {prefix} [FILE] Export FAILED: {ex.Message}");
                failures.Add(new ExportFailure(FileTarget, ex));
            }
        }

        foreach (var endpoint in apiEndpoints)
        {
            currentExportIndex++;
            var prefix = currentExportIndex == totalExports ? "└─" : "├─";
            var isQueue = !string.IsNullOrEmpty(endpoint.MessageQueueType);

            stoppingToken.ThrowIfCancellationRequested();

            // Survives a retry, so a failure in batch 3 resumes at batch 3 rather than resending 1 and 2.
            // ponytail: a dead-lettered payload still replays whole; store the offset if that matters.
            var batchesSent = 0;

            try
            {
                await retryPolicy.ExecuteAsync(async _ =>
                {
                    if (isQueue)
                    {
                        await _messageQueueService.SendToQueueAsync(endpoint, data, stoppingToken);
                        return;
                    }

                    var recordCount = data.GetArrayLength();
                    var maxRecordsPerBatch = _globalSettings.MaxRecordsPerBatch;

                    if (_globalSettings.EnablePayloadBatching && recordCount > maxRecordsPerBatch)
                    {
                        var batches = data.EnumerateArray()
                            .Select((record, index) => new { record, index })
                            .GroupBy(x => x.index / maxRecordsPerBatch)
                            .Select(g => g.Select(x => x.record).ToArray())
                            .ToList();

                        _logger.LogDebug($"[{environment.Name}] Batching {recordCount} records into {batches.Count} batches");

                        for (int i = batchesSent; i < batches.Count; i++)
                        {
                            using var batchDocument = JsonDocument.Parse(JsonSerializer.Serialize(batches[i]));
                            await SendHttpRequestAsync(endpoint, trackingObject, environment, batchDocument.RootElement, i + 1, batches.Count, stoppingToken);
                            batchesSent = i + 1;
                        }
                    }
                    else
                    {
                        await SendHttpRequestAsync(endpoint, trackingObject, environment, data, null, null, stoppingToken);
                    }
                }, stoppingToken);

                if (isQueue)
                {
                    _logger.LogInformation($"[{environment.Name}]  {prefix} [MQ] Exported to {endpoint.MessageQueueType} {endpoint.MessageQueueTarget()}");
                }
                else
                {
                    _logger.LogInformation($"[{environment.Name}]  {prefix} [HTTP] Exported to endpoint '{endpoint.Key ?? "unnamed"}': {endpoint.Url}");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug($"[{environment.Name}]  {prefix} Export cancelled due to shutdown");
                throw;
            }
            catch (Exception ex)
            {
                var exportType = isQueue ? "MQ" : "HTTP";
                _logger.LogError(ex, $"[{environment.Name}]  {prefix} [{exportType}] Export FAILED: {ex.Message}");
                failures.Add(new ExportFailure(TargetName(endpoint), ex));
            }
        }

        return failures;
    }

    /// <summary>Writes the export and returns the path it resolved the template to.</summary>
    private async Task<string> ExportToFileAsync(EnvironmentConfig environment, TrackingObject trackingObject, JsonElement data)
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

        // Don't log here - caller logs with proper context

        var exportRoot = ExportRoot(filePathTemplate);
        if (exportRoot != null)
            CleanupOldFiles(exportRoot, _maxExportDirectorySizeBytes);

        return filePath;
    }

    /// <summary>
    /// Directory the size limit applies to: the fixed part of the template, before the first
    /// placeholder. Returns null when that would be the working directory, since sweeping the
    /// whole working directory for space is never what the setting means.
    /// </summary>
    internal static string? ExportRoot(string filePathTemplate)
    {
        var placeholder = filePathTemplate.IndexOf('{');
        var fixedPrefix = placeholder < 0 ? filePathTemplate : filePathTemplate[..placeholder];
        var directory = Path.GetDirectoryName(fixedPrefix);

        return string.IsNullOrEmpty(directory) ? null : directory;
    }

    private async Task SendHttpRequestAsync(
        ApiEndpoint endpoint,
        TrackingObject trackingObject,
        EnvironmentConfig environment,
        JsonElement data,
        int? batchNumber,
        int? totalBatches,
        CancellationToken stoppingToken)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var apiUrl = endpoint.Url?
            .Replace("{timestamp}", Uri.EscapeDataString(timestamp))
            .Replace("{object}", Uri.EscapeDataString(trackingObject.Name))
            .Replace("{database}", Uri.EscapeDataString(trackingObject.Database))
            .Replace("{environment}", Uri.EscapeDataString(environment.Name))
            .Replace("{key}", Uri.EscapeDataString(endpoint.Key ?? ""));

        if (string.IsNullOrEmpty(apiUrl))
            throw new InvalidOperationException("API URL is required for HTTP endpoints");

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        string? oauthCacheKey = null;

        // Add authentication
        if (endpoint.Auth != null)
        {
            switch (endpoint.Auth.Type?.ToLower())
            {
                case "bearer":
                    if (!string.IsNullOrEmpty(endpoint.Auth.Token))
                    {
                        client.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.Auth.Token);
                    }
                    break;
                case "basic":
                    if (!string.IsNullOrEmpty(endpoint.Auth.Username) && !string.IsNullOrEmpty(endpoint.Auth.Password))
                    {
                        var credentials = Convert.ToBase64String(
                            Encoding.UTF8.GetBytes($"{endpoint.Auth.Username}:{endpoint.Auth.Password}"));
                        client.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
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
                case "oauth2clientcredentials":
                    // Everything that decides which token comes back, so a reloaded scope or
                    // token endpoint never reuses a token issued for the old one.
                    oauthCacheKey = $"{environment.Name}/{endpoint.Key ?? "default"}_{endpoint.Auth.ClientId}_{endpoint.Auth.TokenEndpoint}_{endpoint.Auth.Scope}";
                    var token = await _oauth2TokenService.GetAccessTokenAsync(endpoint.Auth, oauthCacheKey, stoppingToken);
                    if (!string.IsNullOrEmpty(token))
                    {
                        client.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
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
        int payloadBytes;

        // Apply compression if enabled
        if (endpoint.EnableCompression)
        {
            var compressedBytes = Gzip.Compress(jsonContent);
            content = new ByteArrayContent(compressedBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            payloadBytes = compressedBytes.Length;
            _logger.LogDebug($"[{environment.Name}] Compressed payload from {jsonContent.Length} to {compressedBytes.Length} bytes");
        }
        else
        {
            content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            payloadBytes = Encoding.UTF8.GetByteCount(jsonContent);
        }

        // Measured on what actually goes over the wire. Not an HttpRequestException, so the
        // retry policy skips it (a retry cannot make the body smaller) and the caller
        // dead-letters the batch instead.
        if (payloadBytes > _globalSettings.MaxPayloadSizeBytes)
        {
            throw new InvalidOperationException(
                $"Payload for endpoint '{endpoint.Key ?? apiUrl}' is {payloadBytes} bytes, over the MaxPayloadSizeBytes limit of {_globalSettings.MaxPayloadSizeBytes}. " +
                "Lower MaxRecordsPerBatch, enable EnablePayloadBatching, or raise the limit.");
        }

        using var response = await client.PostAsync(apiUrl, content, stoppingToken);

        // A rejected token is not going to start working; drop it so the retry asks for a new one.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && oauthCacheKey != null)
            _oauth2TokenService.Invalidate(oauthCacheKey);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"API export to '{endpoint.Key ?? apiUrl}' failed with status {response.StatusCode}");
        }

        // Don't log here - caller logs with proper context
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
}
