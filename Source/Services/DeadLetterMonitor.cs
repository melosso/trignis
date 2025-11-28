using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Trignis.MicrosoftSQL.Services;

/// <summary>
/// Monitors the dead letter queue and alerts when thresholds are exceeded
/// </summary>
public class DeadLetterQueueMonitor : BackgroundService
{
    private readonly ILogger<DeadLetterQueueMonitor> _logger;
    private readonly IConfiguration _config;
    private readonly string _sinkholeConnectionString;
    private readonly int _thresholdCount;
    private readonly int _checkIntervalMinutes;
    private readonly bool _enabled;
    private DateTime _lastAlertTime = DateTime.MinValue;
    private readonly TimeSpan _alertCooldown = TimeSpan.FromHours(1);

    public DeadLetterQueueMonitor(
        ILogger<DeadLetterQueueMonitor> logger,
        IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _sinkholeConnectionString = "Data Source=sinkhole.db";
        _thresholdCount = _config.GetValue<int>("ChangeTracking:DeadLetterThreshold", 100);
        _checkIntervalMinutes = _config.GetValue<int>("ChangeTracking:DeadLetterCheckIntervalMinutes", 30);
        _enabled = _config.GetValue<bool>("ChangeTracking:DeadLetterMonitorEnabled", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Dead letter queue monitoring is disabled");
            return;
        }

        _logger.LogDebug("Dead letter queue monitor started (Threshold: {Threshold}, Interval: {Interval}min)", 
            _thresholdCount, _checkIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_checkIntervalMinutes), stoppingToken);
                await CheckDeadLetterQueueAsync();
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Dead letter queue monitoring cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking dead letter queue");
            }
        }
    }

    private async Task CheckDeadLetterQueueAsync()
    {
        try
        {
            using var conn = new SqliteConnection(_sinkholeConnectionString);
            await conn.OpenAsync();

            // Get total count
            var totalCommand = conn.CreateCommand();
            totalCommand.CommandText = "SELECT COUNT(*) FROM DeadLetters";
            var totalResult = await totalCommand.ExecuteScalarAsync();
            var totalCount = (totalResult != null && totalResult != DBNull.Value) ? Convert.ToInt64(totalResult) : 0;

            // Get count in last 24 hours
            var recentCommand = conn.CreateCommand();
            recentCommand.CommandText = "SELECT COUNT(*) FROM DeadLetters WHERE Timestamp >= datetime('now', '-24 hours')";
            var recentResult = await recentCommand.ExecuteScalarAsync();
            var recentCount = (recentResult != null && recentResult != DBNull.Value) ? Convert.ToInt64(recentResult) : 0;

            // Get breakdown by tracking object
            var breakdownCommand = conn.CreateCommand();
            breakdownCommand.CommandText = @"
                SELECT TrackingObjectName, COUNT(*) as Count 
                FROM DeadLetters 
                WHERE Timestamp >= datetime('now', '-24 hours')
                GROUP BY TrackingObjectName 
                ORDER BY Count DESC 
                LIMIT 5";
            
            var breakdown = new System.Collections.Generic.List<(string ObjectName, long Count)>();
            using (var reader = await breakdownCommand.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    breakdown.Add((reader.GetString(0), reader.GetInt64(1)));
                }
            }

            _logger.LogDebug("Dead letter queue status - Total: {Total}, Recent (24h): {Recent}", totalCount, recentCount);

            // Alert if threshold exceeded and cooldown period has passed
            if (totalCount >= _thresholdCount && (DateTime.UtcNow - _lastAlertTime) > _alertCooldown)
            {
                _logger.LogWarning("⚠️ Dead letter queue threshold exceeded! Total: {Total} (Threshold: {Threshold})", 
                    totalCount, _thresholdCount);
                
                if (recentCount > 0)
                {
                    _logger.LogWarning("Recent failures (24h): {Recent}", recentCount);
                    
                    if (breakdown.Count > 0)
                    {
                        _logger.LogWarning("Top failing objects:");
                        foreach (var (objectName, count) in breakdown)
                        {
                            _logger.LogWarning("  └─ {ObjectName}: {Count} failures", objectName, count);
                        }
                    }
                }

                _logger.LogWarning("Action required: Review dead letters in sinkhole.db and address recurring failures");
                _lastAlertTime = DateTime.UtcNow;
            }
            else if (totalCount >= _thresholdCount * 0.75) // Warning at 75% threshold
            {
                _logger.LogInformation("Dead letter queue approaching threshold: {Total}/{Threshold} ({Percentage:P0})", 
                    totalCount, _thresholdCount, (double)totalCount / _thresholdCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check dead letter queue status");
        }
    }

    public async Task<DeadLetterStats> GetStatsAsync()
    {
        try
        {
            using var conn = new SqliteConnection(_sinkholeConnectionString);
            await conn.OpenAsync();

            var stats = new DeadLetterStats();

            // Total count
            var totalCommand = conn.CreateCommand();
            totalCommand.CommandText = "SELECT COUNT(*) FROM DeadLetters";
            var totalResult = await totalCommand.ExecuteScalarAsync();
            stats.TotalCount = (totalResult != null && totalResult != DBNull.Value) ? Convert.ToInt64(totalResult) : 0;

            // Recent counts
            var recentCommand = conn.CreateCommand();
            recentCommand.CommandText = @"
                SELECT 
                    COUNT(CASE WHEN Timestamp >= datetime('now', '-1 hour') THEN 1 END) as LastHour,
                    COUNT(CASE WHEN Timestamp >= datetime('now', '-24 hours') THEN 1 END) as Last24Hours,
                    COUNT(CASE WHEN Timestamp >= datetime('now', '-7 days') THEN 1 END) as Last7Days
                FROM DeadLetters";
            
            using (var reader = await recentCommand.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    stats.LastHourCount = reader.GetInt64(0);
                    stats.Last24HoursCount = reader.GetInt64(1);
                    stats.Last7DaysCount = reader.GetInt64(2);
                }
            }

            // Get most common error
            var errorCommand = conn.CreateCommand();
            errorCommand.CommandText = @"
                SELECT ErrorMessage, COUNT(*) as Count 
                FROM DeadLetters 
                WHERE Timestamp >= datetime('now', '-24 hours')
                GROUP BY ErrorMessage 
                ORDER BY Count DESC 
                LIMIT 1";
            
            using (var reader = await errorCommand.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    stats.MostCommonError = reader.GetString(0);
                    stats.MostCommonErrorCount = reader.GetInt64(1);
                }
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get dead letter statistics");
            return new DeadLetterStats();
        }
    }
}