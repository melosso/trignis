using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Trignis.MicrosoftSQL.Models;

namespace Trignis.MicrosoftSQL.Services;

/// <summary>
/// Shared retry policies for the source read and the export write.
/// Policies are immutable and thread-safe, so one instance is reused per distinct
/// (environment, retry count, delay). Keying on the values as well as the name keeps a
/// hot-reloaded environment from reusing a policy built for its old settings.
/// Cancellation is honoured by passing the token to ExecuteAsync, which aborts the waits.
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
            .Or<SqlException>()
            .WaitAndRetryAsync(retryCount, _ => retryDelay, (exception, timeSpan, attempt, _) =>
                _logger.LogWarning($"[{environment.Name}] Retry {attempt} of {retryCount} after {timeSpan.TotalSeconds}s due to {exception.Message}")));
    }
}
