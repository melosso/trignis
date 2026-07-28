using System;
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

    public DeadLetterReplayer(
        DeadLetterService deadLetters,
        ExportService exportService,
        EnvironmentConfigService configService)
    {
        _deadLetters = deadLetters;
        _exportService = exportService;
        _configService = configService;
    }

    public enum Outcome
    {
        /// <summary>Every destination accepted it and the row has been removed.</summary>
        Replayed,

        /// <summary>At least one destination refused it. The row is kept.</summary>
        Failed,

        /// <summary>The environment or object it belongs to no longer exists. Retrying cannot help.</summary>
        Unroutable,
    }

    public sealed record Result(Outcome Outcome, string? Reason, IReadOnlyList<ExportFailure> Failures)
    {
        public static Result Unroutable(string reason) => new(Outcome.Unroutable, reason, []);
    }

    /// <summary>Replays one record. Deletes it only when every destination succeeded.</summary>
    public async Task<Result> ReplayAsync(DeadLetterService.DeadLetterRecord record, CancellationToken ct)
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

        using var document = JsonDocument.Parse(record.Data);
        var failures = await _exportService.ExportAsync(environment, trackingObject, document.RootElement, ct);

        if (failures.Count > 0)
            return new Result(Outcome.Failed, string.Join("; ", failures.Select(f => $"{f.Target}: {f.Error.Message}")), failures);

        await _deadLetters.DeleteAsync(record.Id, ct);
        return new Result(Outcome.Replayed, null, []);
    }
}
