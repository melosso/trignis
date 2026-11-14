using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Trignis.Services;

/// <summary>
/// Resends a dead letter to the destinations of the environment it came from.
/// Shared by the dashboard's replay button and the automatic retry loop, so both
/// answer "can this be replayed, and did it work" the same way.
/// </summary>
public sealed class DeadLetterReplayer
{
    private readonly DeadLetterService _deadLetters;
    private readonly ExportService _exportService;
    private readonly EnvironmentConfigService _configService;
    private readonly PauseService _pauseService;

    // The manual button and the automatic loop can pick the same row at the same moment.
    // ponytail: in-memory claim, enough for one process; move it to a column if Trignis ever runs as two.
    private readonly ConcurrentDictionary<long, byte> _inFlight = new();

    public DeadLetterReplayer(
        DeadLetterService deadLetters,
        ExportService exportService,
        EnvironmentConfigService configService,
        PauseService pauseService)
    {
        _deadLetters = deadLetters;
        _exportService = exportService;
        _configService = configService;
        _pauseService = pauseService;
    }

    public enum Outcome
    {
        /// <summary>Every destination accepted it and the row has been removed.</summary>
        Replayed,

        /// <summary>At least one destination refused it. The row is kept.</summary>
        Failed,

        /// <summary>The environment or object it belongs to no longer exists. Retrying cannot help.</summary>
        Unroutable,

        /// <summary>The environment or object is paused. Nothing was sent and no attempt was spent.</summary>
        Paused,

        /// <summary>Another replay of the same row is running. Nothing was sent.</summary>
        InProgress,
    }

    public sealed record Result(Outcome Outcome, string? Reason, IReadOnlyList<ExportFailure> Failures)
    {
        public static Result Unroutable(string reason) => new(Outcome.Unroutable, reason, []);
    }

    /// <summary>Replays one record. Deletes it only when every destination succeeded.</summary>
    public async Task<Result> ReplayAsync(DeadLetterService.DeadLetterRecord record, CancellationToken ct)
    {
        if (!_inFlight.TryAdd(record.Id, 0))
            return new Result(Outcome.InProgress, "This dead letter is already being replayed", []);

        try
        {
            return await ReplayClaimedAsync(record, ct);
        }
        finally
        {
            _inFlight.TryRemove(record.Id, out _);
        }
    }

    private async Task<Result> ReplayClaimedAsync(DeadLetterService.DeadLetterRecord record, CancellationToken ct)
    {
        if (record.EnvironmentName == null)
            return Result.Unroutable("This dead letter predates environment tracking and cannot be replayed");

        var environment = _configService.Environments
            .FirstOrDefault(e => e.Name.Equals(record.EnvironmentName, StringComparison.OrdinalIgnoreCase));
        if (environment == null)
            return Result.Unroutable($"Environment '{record.EnvironmentName}' is no longer configured");

        var trackingObject = environment.ChangeTracking.TrackingObjects
            .FirstOrDefault(t => t.Name.Equals(record.ObjectName, StringComparison.OrdinalIgnoreCase));
        if (trackingObject == null)
            return Result.Unroutable($"Tracking object '{record.ObjectName}' is no longer configured");

        // A pause holds everything bound for the environment, including what failed earlier.
        var paused = await _pauseService.GetPausedScopesAsync(ct);
        if (paused.Contains(PauseService.EnvironmentScope(environment.Name)) ||
            paused.Contains(PauseService.ObjectScope(environment.Name, trackingObject.Name)))
            return new Result(Outcome.Paused, $"'{trackingObject.Name}' in '{environment.Name}' is paused; resume it before replaying", []);

        // The row may have been replayed and removed between being listed and being claimed.
        if (await _deadLetters.GetAsync(record.Id, ct) is not { } current)
            return new Result(Outcome.Replayed, null, []);

        // Only the destinations that refused it. Without this check a renamed endpoint would match
        // nothing, report no failures, and the row would be deleted without ever being sent.
        var configured = _exportService.ConfiguredTargets(environment);
        var targets = current.FailedTargets?.Where(configured.Contains).ToList();
        if (configured.Count == 0)
            return Result.Unroutable($"Environment '{environment.Name}' has no export destinations enabled");
        if (targets is { Count: 0 })
            return Result.Unroutable($"Destination(s) {string.Join(", ", current.FailedTargets!)} are no longer configured");

        using var document = JsonDocument.Parse(current.Data);
        var failures = await _exportService.ExportAsync(environment, trackingObject, document.RootElement, ct, targets);

        if (failures.Count > 0)
        {
            await _deadLetters.SetFailedTargetsAsync(current.Id, failures.Select(f => f.Target).Distinct(), ct);
            return new Result(Outcome.Failed, string.Join("; ", failures.Select(f => $"{f.Target}: {f.Error.Message}")), failures);
        }

        await _deadLetters.DeleteAsync(record.Id, ct);
        return new Result(Outcome.Replayed, null, []);
    }
}
