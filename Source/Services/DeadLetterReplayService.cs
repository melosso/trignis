using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trignis.Models;

namespace Trignis.Services;

/// <summary>
/// Retries dead letters on an exponential backoff until they succeed or run out of attempts.
/// What is left over is a genuine "someone has to look at this", which is what the dashboard
/// replay button is for. Separate from <see cref="DeadLetterQueueMonitor"/> so that turning
/// the alerting off does not silently turn retrying off with it.
/// </summary>
public sealed class DeadLetterReplayService : BackgroundService
{
    /// <summary>Long enough that a destination down overnight is still retried a few times.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    /// <summary>Bounded so one cycle cannot monopolise the destinations the polling loop also uses.</summary>
    private const int BatchSize = 25;

    private readonly ILogger<DeadLetterReplayService> _logger;
    private readonly DeadLetterService _deadLetters;
    private readonly DeadLetterReplayer _replayer;
    private readonly GlobalSettings _settings;

    public DeadLetterReplayService(
        ILogger<DeadLetterReplayService> logger,
        DeadLetterService deadLetters,
        DeadLetterReplayer replayer,
        IOptions<GlobalSettings> settings)
    {
        _logger = logger;
        _deadLetters = deadLetters;
        _replayer = replayer;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxAttempts = _settings.DeadLetterMaxReplayAttempts;

        if (!_settings.DeadLetterAutoReplayEnabled || maxAttempts <= 0)
        {
            _logger.LogDebug("Automatic dead letter replay is disabled");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _settings.DeadLetterReplayIntervalSeconds));
        _logger.LogDebug("Automatic dead letter replay started (Interval: {Interval}s, Max attempts: {Max})",
            interval.TotalSeconds, maxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await ReplayDueAsync(maxAttempts, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Automatic dead letter replay cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during automatic dead letter replay");
            }
        }
    }

    private async Task ReplayDueAsync(int maxAttempts, CancellationToken ct)
    {
        var due = await _deadLetters.GetDueForReplayAsync(maxAttempts, BatchSize, ct);
        if (due.Count == 0)
            return;

        var replayed = 0;

        foreach (var record in due)
        {
            ct.ThrowIfCancellationRequested();

            DeadLetterReplayer.Result result;
            try
            {
                result = await _replayer.ReplayAsync(record, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // An unexpected failure counts as an attempt; otherwise a poison payload
                // that throws before reaching a destination would be retried forever.
                result = new DeadLetterReplayer.Result(DeadLetterReplayer.Outcome.Failed, ex.Message, []);
            }

            switch (result.Outcome)
            {
                case DeadLetterReplayer.Outcome.Replayed:
                    replayed++;
                    break;

                case DeadLetterReplayer.Outcome.Unroutable:
                    // Park it rather than retry it: no amount of waiting brings a deleted
                    // environment back. A manual replay resets this once the config is fixed.
                    await _deadLetters.RecordReplayFailureAsync(
                        record.Id, result.Reason ?? "Unroutable", DateTime.UtcNow.AddYears(1), ct);
                    _logger.LogWarning("Dead letter {Id} cannot be replayed automatically: {Reason}", record.Id, result.Reason);
                    break;

                default:
                    var backoff = Backoff(record.Attempts, _settings.DeadLetterReplayBackoffSeconds);
                    await _deadLetters.RecordReplayFailureAsync(
                        record.Id, result.Reason ?? "Replay failed", DateTime.UtcNow + backoff, ct);
                    _logger.LogDebug("Dead letter {Id} failed attempt {Attempt}/{Max}, retrying in {Backoff}",
                        record.Id, record.Attempts + 1, maxAttempts, backoff);
                    break;
            }
        }

        if (replayed > 0)
            _logger.LogInformation("Automatically replayed {Replayed} of {Total} due dead letter(s)", replayed, due.Count);
        else
            _logger.LogDebug("Automatic replay attempted {Total} dead letter(s), none succeeded", due.Count);
    }

    /// <summary>
    /// Doubling delay, capped. <paramref name="attempts"/> is the count the row had going in,
    /// so the first failure waits one base delay and the fifth waits sixteen.
    /// </summary>
    internal static TimeSpan Backoff(int attempts, int baseSeconds)
    {
        var seconds = Math.Max(1, baseSeconds) * Math.Pow(2, Math.Max(0, attempts));
        return seconds >= MaxBackoff.TotalSeconds ? MaxBackoff : TimeSpan.FromSeconds(seconds);
    }
}
