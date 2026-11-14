using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Trignis.Models;

namespace Trignis.Services;

/// <summary>
/// Shared retry policies for the source read and the export write. One immutable, thread-safe instance per (environment, retry count, delay): a hot-reloaded environment gets a policy for its new settings. The token passed to ExecuteAsync cancels the waits.
/// </summary>
public sealed class RetryPolicies
{
    private readonly ILogger<RetryPolicies> _logger;
    private readonly GlobalSettings _globalSettings;
    private readonly ConcurrentDictionary<string, AsyncRetryPolicy> _cache = new();

    public RetryPolicies(ILogger<RetryPolicies> logger, IOptions<GlobalSettings> globalSettings)
    {
        _logger = logger;
        _globalSettings = globalSettings.Value;
    }

    public AsyncRetryPolicy For(EnvironmentConfig environment)
    {
        var retryCount = environment.ChangeTracking.RetryCount ?? _globalSettings.RetryCount;
        var retryDelay = TimeSpan.FromSeconds(environment.ChangeTracking.RetryDelaySeconds ?? _globalSettings.RetryDelaySeconds);

        return _cache.GetOrAdd($"{environment.Name}:{retryCount}:{retryDelay.TotalSeconds}", _ => Policy
            .Handle<HttpRequestException>()
            .Or<IOException>()
            .Or<DbException>()
            .WaitAndRetryAsync(retryCount, _ => retryDelay, (exception, timeSpan, attempt, _) =>
                _logger.LogWarning($"[{environment.Name}] Retry {attempt} of {retryCount} after {timeSpan.TotalSeconds}s due to {exception.Message}")));
    }
}
