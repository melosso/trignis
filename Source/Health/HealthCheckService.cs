using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Trignis.MicrosoftSQL.Models;

namespace Trignis.MicrosoftSQL.Services;

public class HealthCheckService
{
    private readonly ILogger<HealthCheckService> _logger;
    private readonly IConfiguration _config;
    private readonly EnvironmentConfigService _envConfigService;
    private readonly DateTime _startTime;
    private readonly string _version;
    private readonly int _cacheDurationSeconds;
    private volatile string? _cachedResponse;
    private long _lastCheckTimeTicks = DateTime.MinValue.Ticks;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public HealthCheckService(
        ILogger<HealthCheckService> logger,
        IConfiguration config,
        EnvironmentConfigService envConfigService)
    {
        _logger = logger;
        _config = config;
        _envConfigService = envConfigService;
        _startTime = DateTime.UtcNow;
        _version = (typeof(HealthCheckService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
        _cacheDurationSeconds = _config.GetValue<int>("Health:CacheDurationSeconds", 10);
    }

    public async Task<string> GetHealthStatusAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Check if we have a valid cached response (outer fast path — no lock)
        if (_cachedResponse != null && (now - new DateTime(Interlocked.Read(ref _lastCheckTimeTicks), DateTimeKind.Utc)).TotalSeconds < _cacheDurationSeconds)
        {
            return _cachedResponse;
        }

        // Use semaphore to prevent multiple simultaneous health checks
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock; re-read now so the staleness check is accurate
            now = DateTime.UtcNow;
            if (_cachedResponse != null && (now - new DateTime(Interlocked.Read(ref _lastCheckTimeTicks), DateTimeKind.Utc)).TotalSeconds < _cacheDurationSeconds)
            {
                return _cachedResponse;
            }

            // Perform actual health check
            var uptime = (long)(now - _startTime).TotalSeconds;
            var timestamp = now.ToString("yyyy-MM-ddTHH:mm:ssZ");

            // Check database connectivity
            var (dbStatus, dbResponseTime) = await CheckDatabaseHealthAsync(cancellationToken).ConfigureAwait(false);

            var healthResponse = new
            {
                status = dbStatus == "ok (all)" ? "healthy" : "degraded",
                service = "trignis-service",
                uptime = $"{uptime}s",
                timestamp = timestamp,
                version = _version,
                checks = new
                {
                    database = new
                    {
                        status = dbStatus,
                        response_time_ms = dbResponseTime
                    }
                }
            };

            _cachedResponse = JsonSerializer.Serialize(healthResponse, new JsonSerializerOptions { WriteIndented = true });
            Interlocked.Exchange(ref _lastCheckTimeTicks, now.Ticks);

            return _cachedResponse;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<(string status, long responseTimeMs)> CheckDatabaseHealthAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        var environments = _envConfigService.Environments;
        var trackingObjects = environments.SelectMany(e => e.ChangeTracking.TrackingObjects ?? Array.Empty<TrackingObject>()).ToArray();

        if (trackingObjects.Length == 0)
        {
            sw.Stop();
            return ("no databases configured", sw.ElapsedMilliseconds);
        }

        var uniqueDatabases = trackingObjects.Select(t => t.Database).Distinct().ToList();
        int successCount = 0;
        int failCount = 0;

        foreach (var dbName in uniqueDatabases)
        {
            var connString = environments
                .SelectMany(e => e.ConnectionStrings)
                .FirstOrDefault(cs => cs.Key == dbName).Value;

            if (string.IsNullOrEmpty(connString))
            {
                failCount++;
                continue;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var conn = new SqlConnection(connString);
                await conn.OpenAsync(cts.Token).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                cmd.CommandTimeout = 5;
                await cmd.ExecuteScalarAsync(cts.Token).ConfigureAwait(false);
                successCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Health check failed for database '{Database}': {Message}", dbName, ex.GetBaseException().Message);
                failCount++;
            }
        }

        sw.Stop();

        string status;
        if (failCount == 0)
        {
            status = "ok (all)";
        }
        else if (successCount > 0)
        {
            status = $"degraded ({successCount}/{uniqueDatabases.Count})";
        }
        else
        {
            status = "failed (all)";
        }

        return (status, sw.ElapsedMilliseconds);
    }
}