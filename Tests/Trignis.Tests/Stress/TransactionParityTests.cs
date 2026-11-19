using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Trignis.Data;
using Xunit;
using Xunit.Abstractions;

namespace Trignis.Tests.Stress;

/// <summary>
/// Concurrent writers at every isolation level commit, roll back or fail mid-transaction while Trignis polls.
/// Committed changes arrive with their final value; rolled-back changes never arrive.
/// </summary>
[Collection("SqliteTests")]
public sealed class TransactionParityTests(ITestOutputHelper output)
{
    public static TheoryData<string> Providers() => ["mssql", "postgres"];

    private static IsolationLevel[] Levels(string provider) => provider == "mssql"
        ? [IsolationLevel.ReadUncommitted, IsolationLevel.ReadCommitted, IsolationLevel.RepeatableRead, IsolationLevel.Serializable, IsolationLevel.Snapshot]
        : [IsolationLevel.ReadCommitted, IsolationLevel.RepeatableRead, IsolationLevel.Serializable];

    [ParityTheory, MemberData(nameof(Providers))]
    public async Task RandomlyFailingTransactions_AtEveryIsolationLevel_DeliverCommittedChangesOnly(string provider)
    {
        await using var source = await ParitySource.CreateAsync(provider, transactional: true);
        await using var sink = new HttpSink();
        await using var host = new StressHost();
        host.WriteEnvJson("tx", $$"""
            {
              "Provider": "{{provider}}",
              "ConnectionStrings": { "src": "{{source.Connection}}" },
              "ChangeTracking": {
                "PollingIntervalSeconds": 1, "ExportToFile": false, "ExportToApi": true, "RetryCount": 0,
                "TrackingObjects": [{ "Name": "items", "Database": "src", "TableName": "{{source.Table}}", "StoredProcedureName": "{{source.Procedure}}", "InitialSyncMode": "Full" }],
                "ApiEndpoints": [{ "Key": "sink", "Url": "{{sink.Url}}" }]
              }
            }
            """);
        await host.StartAsync();

        var dialect = SqlDialect.Parse(provider);
        var committedIds = new ConcurrentDictionary<long, byte>();
        var rolledBackIds = new ConcurrentBag<long>();
        var rolledBackValues = new ConcurrentBag<string>();
        var outcomes = new ConcurrentDictionary<string, int>();
        var levels = Levels(provider);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        async Task WriterAsync(int writer)
        {
            var random = new Random(writer);
            for (var seq = 0; !stop.IsCancellationRequested; seq++)
            {
                var level = levels[random.Next(levels.Length)];
                var inserted = new List<long>();
                var written = new List<string>();
                var outcome = random.Next(10) switch { < 6 => "commit", < 9 => "rollback", _ => "throw" };
                try
                {
                    await using var conn = await dialect.OpenAsync(source.Connection, CancellationToken.None);
                    await using var tx = await conn.BeginTransactionAsync(level);

                    for (var i = random.Next(1, 4); i > 0; i--)
                    {
                        var value = $"w{writer}-{seq}-{i}";
                        written.Add(value);
                        inserted.Add(await ScalarAsync(conn, tx, provider == "mssql"
                            ? $"insert into {source.Table} (val) output inserted.id values ('{value}')"
                            : $"insert into {source.Table} (val) values ('{value}') returning id"));
                    }

                    if (!committedIds.IsEmpty && random.Next(2) == 0)
                    {
                        var target = committedIds.Keys.ElementAt(random.Next(committedIds.Count));
                        var value = $"w{writer}-{seq}-u";
                        written.Add(value);
                        await ExecuteAsync(conn, tx, $"update {source.Table} set val = '{value}' where id = {target}");
                    }

                    // Spans polls: Trignis reads while the transaction is open.
                    await Task.Delay(random.Next(0, 1500));

                    if (outcome == "throw") throw new InvalidOperationException("writer failed mid-transaction");
                    if (outcome == "rollback") { await tx.RollbackAsync(); }
                    else
                    {
                        await tx.CommitAsync();
                        foreach (var id in inserted) committedIds[id] = 0;
                        outcomes.AddOrUpdate($"commit {level}", 1, (_, n) => n + 1);
                        continue;
                    }
                }
                catch (DbException ex)
                {
                    // Deadlocks, update conflicts and serialization failures roll back.
                    outcome = $"conflict ({ex.GetType().Name})";
                }
                catch (InvalidOperationException) { }

                foreach (var id in inserted) rolledBackIds.Add(id);
                foreach (var value in written) rolledBackValues.Add(value);
                outcomes.AddOrUpdate($"{outcome.Split(' ')[0]} {level}", 1, (_, n) => n + 1);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() => WriterAsync(w))));

        var finalValues = await source.ValuesAsync();
        var clock = Stopwatch.StartNew();
        while (!finalValues.All(row => sink.AcceptedRows.Contains((row.Key, row.Value))))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(60))
            {
                var missing = finalValues.Where(row => !sink.AcceptedRows.Contains((row.Key, row.Value))).Take(5);
                Assert.Fail($"final values not delivered: {string.Join(", ", missing)}");
            }
            await Task.Delay(250);
        }
        await Task.Delay(2500);

        var delivered = sink.AcceptedRows;
        foreach (var (key, count) in outcomes.OrderBy(o => o.Key)) output.WriteLine($"{key,-32} {count,5}");
        output.WriteLine($"{finalValues.Count} committed rows, {rolledBackIds.Count} rolled-back inserts, {delivered.Count} rows delivered");

        Assert.Equal(committedIds.Keys.Order(), finalValues.Keys.Order());
        Assert.Empty(delivered.Select(r => r.Id).Intersect(rolledBackIds));
        Assert.Empty(delivered.Select(r => r.Val).Intersect(rolledBackValues));
        Assert.Contains(outcomes.Keys, k => k.StartsWith("rollback"));
        Assert.Contains(outcomes.Keys, k => k.StartsWith("throw"));
    }

    private static async Task<long> ScalarAsync(DbConnection conn, DbTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(DbConnection conn, DbTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
