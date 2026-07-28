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
using Trignis.MicrosoftSQL.Helpers;
using Trignis.MicrosoftSQL.Models;

namespace Trignis.MicrosoftSQL.Services;

/// <summary>Which target failed, and why. One entry per export destination.</summary>
public sealed record ExportFailure(string Target, Exception Error);

/// <summary>
/// Sends a change payload to every configured destination — file, message queue and HTTP.
/// Failures are returned rather than dead-lettered here so the caller decides what they mean:
/// the polling loop records a dead letter, a replay reports the failure back to the operator.
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

    public async Task<IReadOnlyList<ExportFailure>> ExportAsync(
        EnvironmentConfig environment, TrackingObject trackingObject, JsonElement data, CancellationToken stoppingToken)
    {
        var failures = new List<ExportFailure>();

        var exportToFile = environment.ChangeTracking.ExportToFile ?? _globalSettings.ExportToFile;
        var exportToApi = environment.ChangeTracking.ExportToApi ?? _globalSettings.ExportToApi;
        var retryPolicy = _retryPolicies.For(environment);

        var apiEndpoints = exportToApi ? (environment.ChangeTracking.ApiEndpoints ?? Array.Empty<ApiEndpoint>()) : Array.Empty<ApiEndpoint>();
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
                failures.Add(new ExportFailure("FILE", ex));
            }
        }

        foreach (var endpoint in apiEndpoints)
        {
            currentExportIndex++;
            var prefix = currentExportIndex == totalExports ? "└─" : "├─";
            var isQueue = !string.IsNullOrEmpty(endpoint.MessageQueueType);

            stoppingToken.ThrowIfCancellationRequested();

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

                        for (int i = 0; i < batches.Count; i++)
                        {
                            var batchElement = JsonDocument.Parse(JsonSerializer.Serialize(batches[i])).RootElement;
                            await SendHttpRequestAsync(endpoint, trackingObject, environment, batchElement, i + 1, batches.Count, stoppingToken);
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
                failures.Add(new ExportFailure($"{exportType}:{endpoint.Key ?? "unnamed"}", ex));
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
                    var cacheKey = $"{endpoint.Key ?? "default"}_{endpoint.Auth.ClientId}";
                    var token = await _oauth2TokenService.GetAccessTokenAsync(endpoint.Auth, cacheKey, stoppingToken);
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
        // retry policy skips it — a retry cannot make the body smaller — and the caller
        // dead-letters the batch instead.
        if (payloadBytes > _globalSettings.MaxPayloadSizeBytes)
        {
            throw new InvalidOperationException(
                $"Payload for endpoint '{endpoint.Key ?? apiUrl}' is {payloadBytes} bytes, over the MaxPayloadSizeBytes limit of {_globalSettings.MaxPayloadSizeBytes}. " +
                "Lower MaxRecordsPerBatch, enable EnablePayloadBatching, or raise the limit.");
        }

        var response = await client.PostAsync(apiUrl, content, stoppingToken);

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
