using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Trignis.Tests.Stress;

/// <summary>The PostgreSQL scripts in Source/SQL on a database container.</summary>
[Collection("SqliteTests")]
public sealed class ShippedProcedureTests
{
    private static readonly string SqlDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Source/SQL"));

    [ParityTheory]
    [InlineData("02-stored-procedure.postgres.table.sql")]
    [InlineData("02-stored-procedure.postgres.column.sql")]
    public async Task OlderTransactionCommittingFirst_DoesNotHideANewerOpenOne(string procedure)
    {
        var connection = ParitySource.ConnectionString("postgres")!;
        await using (var setup = new NpgsqlConnection(connection))
        {
            await setup.OpenAsync();
            await Exec(setup, File.ReadAllText(Path.Combine(SqlDir, "01-change-tracking-setup.postgres.sql")));
            await Exec(setup, File.ReadAllText(Path.Combine(SqlDir, procedure)));
        }

        await using var older = new NpgsqlConnection(connection);
        await using var newer = new NpgsqlConnection(connection);
        await older.OpenAsync();
        await newer.OpenAsync();

        // The older transaction takes its id first; the newer one inserts first and stays open.
        await using var olderTx = await older.BeginTransactionAsync();
        await Exec(older, "select pg_current_xact_id()", olderTx);
        await using var newerTx = await newer.BeginTransactionAsync();
        await Exec(newer, Insert(1), newerTx);
        await Exec(older, Insert(2), olderTx);
        await olderTx.CommitAsync();

        var first = await Sync(connection, 0);
        await newerTx.CommitAsync();
        var second = await Sync(connection, first.Version);
        var third = await Sync(connection, second.Version);

        Assert.Contains(1, first.Steps.Concat(second.Steps).Concat(third.Steps));
        Assert.Contains(2, first.Steps.Concat(second.Steps).Concat(third.Steps));
    }

    private static string Insert(int steps) =>
        $"insert into public.training_sessions (recorded_on, type, steps, distance, duration, calories) values (now(), 'run', {steps}, 1, 1, 1)";

    private static async Task<(long Version, List<int> Steps)> Sync(string connection, long fromVersion)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select web.get_training_sessions_sync(@p::json)", conn);
        cmd.Parameters.AddWithValue("p", JsonSerializer.Serialize(new { fromVersion, mode = "sync" }));
        using var doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync())!);
        var rows = doc.RootElement.GetProperty("Data").EnumerateArray().ToList();
        var version = rows.Count > 0
            ? rows.Max(r => r.GetProperty("$version").GetInt64())
            : doc.RootElement.GetProperty("Metadata").GetProperty("Sync").GetProperty("Version").GetInt64();
        return (version, rows.Where(r => r.TryGetProperty("Steps", out var s) && s.ValueKind == JsonValueKind.Number).Select(r => r.GetProperty("Steps").GetInt32()).ToList());
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }
}
