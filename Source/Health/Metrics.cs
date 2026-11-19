using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Trignis.Services;

namespace Trignis.Health;

/// <summary>
/// <c>/metrics</c> in the Prometheus text format. Written by hand: the format is a few lines of plain text, no client library needed.
/// </summary>
public static class Metrics
{
    public readonly record struct Sample(string Environment, string Table, string? Destination, double Value);

    public static async Task<string> RenderAsync(
        string stateDbPath, string sinkholePath, EnvironmentConfigService environments, PauseService pauses, ExportService exports, CancellationToken ct)
    {
        var tables = environments.Environments
            .SelectMany(e => e.ChangeTracking.TrackingObjects.Select(t => (Environment: e.Name, Table: t.Name)))
            .ToList();
        var configured = tables.ToHashSet();

        // Tables removed from the configuration keep their state row; leaving them out keeps alerts from firing forever
        var ages = (await QueryAsync(stateDbPath,
                "SELECT EnvironmentName, ObjectName, (julianday('now') - julianday(LastUpdated)) * 86400 FROM LastVersions", ct))
            .Where(r => configured.Contains((r.Environment, r.Table)));

        var paused = await pauses.GetPausedScopesAsync(ct);

        var deadLetters = File.Exists(sinkholePath)
            ? (await QueryAsync(sinkholePath,
                "SELECT EnvironmentName, TrackingObjectName, COUNT(*) FROM DeadLetters WHERE EnvironmentName IS NOT NULL GROUP BY 1, 2", ct))
                // TrackingObjectName is stored as "<environment>_<table>"
                .Select(r => r with { Table = r.Table.StartsWith(r.Environment + "_", StringComparison.Ordinal) ? r.Table[(r.Environment.Length + 1)..] : r.Table })
            : [];

        var text = new StringBuilder();
        Write(text, "trignis_last_cycle_age_seconds", "gauge", "Seconds since the last successful read of the table.", ages);
        Write(text, "trignis_paused", "gauge", "1 when the table or its environment is paused.", tables.Select(t => new Sample(t.Environment, t.Table, null,
            paused.Contains(PauseService.EnvironmentScope(t.Environment)) || paused.Contains(PauseService.ObjectScope(t.Environment, t.Table)) ? 1 : 0)));
        Write(text, "trignis_rows_exported_total", "counter", "Rows exported per destination since start.",
            exports.RowsExported.Select(kv => new Sample(kv.Key.Environment, kv.Key.Table, kv.Key.Destination, kv.Value)));
        Write(text, "trignis_export_failures_total", "counter", "Failed exports per destination since start.",
            exports.ExportFailures.Select(kv => new Sample(kv.Key.Environment, kv.Key.Table, kv.Key.Destination, kv.Value)));
        Write(text, "trignis_dead_letters", "gauge", "Dead letters waiting for replay or review.", deadLetters);
        return text.ToString();
    }

    internal static void Write(StringBuilder text, string name, string type, string help, IEnumerable<Sample> samples)
    {
        text.Append($"# HELP {name} {help}\n# TYPE {name} {type}\n");
        foreach (var s in samples)
        {
            text.Append($"{name}{{environment=\"{Escape(s.Environment)}\",table=\"{Escape(s.Table)}\"");
            if (s.Destination is not null) text.Append($",destination=\"{Escape(s.Destination)}\"");
            text.Append("} ").Append(s.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static async Task<List<Sample>> QueryAsync(string dbPath, string sql, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(ct);
        var command = conn.CreateCommand();
        command.CommandText = sql;
        var samples = new List<Sample>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            samples.Add(new Sample(reader.GetString(0), reader.GetString(1), null, reader.GetDouble(2)));
        return samples;
    }
}
