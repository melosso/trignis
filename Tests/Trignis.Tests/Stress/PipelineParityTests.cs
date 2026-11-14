using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests.Stress;

/// <summary>
/// The whole pipeline against real databases and real HTTP destinations: rows go into a tracked
/// table, and the assertions are on what the destinations actually received. Every scenario runs
/// on every platform, so a behaviour that only holds on one of them fails here.
/// </summary>
[Collection("SqliteTests")]
public sealed class PipelineParityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    public static TheoryData<string> Providers() => new() { "mssql", "postgres" };

    [ParityTheory, MemberData(nameof(Providers))]
    public async Task Success_EveryRowReachesEveryDestinationExactlyOnce(string provider)
    {
        await using var rig = await Rig.StartAsync(provider);

        for (var batch = 0; batch < 5; batch++)
        {
            await rig.Source.InsertAsync(40);
            await Task.Delay(300);
        }

        await WaitUntil(() => rig.A.Accepted.Count >= 200 && rig.B.Accepted.Count >= 200);
        await Task.Delay(2500); // two more cycles: anything sent twice would show up now

        AssertExactlyOnce(rig.A.Accepted, 200);
        AssertExactlyOnce(rig.B.Accepted, 200);
        Assert.Equal(0, await rig.DeadLetterCountAsync());
        Assert.Equal(await rig.Source.CurrentVersionAsync(), await rig.WatermarkAsync());
    }

    [ParityTheory, MemberData(nameof(Providers))]
    public async Task Failure_IsDeadLetteredForTheFailedDestinationOnly_AndReplayDeliversItOnce(string provider)
    {
        await using var rig = await Rig.StartAsync(provider);
        rig.B.Status = 503;

        await rig.Source.InsertAsync(25);
        await WaitUntil(async () => await rig.DeadLetterCountAsync() == 1);

        // The healthy destination got everything; the watermark moved on regardless
        AssertExactlyOnce(rig.A.Accepted, 25);
        Assert.Empty(rig.B.Accepted);
        Assert.Equal(await rig.Source.CurrentVersionAsync(), await rig.WatermarkAsync());

        var record = await rig.OnlyDeadLetterAsync();
        Assert.Equal(["HTTP:b"], record.FailedTargets);

        // Still down: the replay fails, the row stays, and the healthy side is left alone
        var failed = await rig.Host.Replayer.ReplayAsync(record, CancellationToken.None);
        Assert.Equal(DeadLetterReplayer.Outcome.Failed, failed.Outcome);
        Assert.Equal(1, await rig.DeadLetterCountAsync());
        AssertExactlyOnce(rig.A.Accepted, 25);

        rig.B.Status = 200;
        var replayed = await rig.Host.Replayer.ReplayAsync(record, CancellationToken.None);

        Assert.Equal(DeadLetterReplayer.Outcome.Replayed, replayed.Outcome);
        Assert.Equal(0, await rig.DeadLetterCountAsync());
        AssertExactlyOnce(rig.B.Accepted, 25);
        AssertExactlyOnce(rig.A.Accepted, 25); // not resent to the destination that never failed
    }

    [ParityTheory, MemberData(nameof(Providers))]
    public async Task Pause_HoldsNewChangesAndReplays_UntilResumed(string provider)
    {
        await using var rig = await Rig.StartAsync(provider);
        rig.B.Status = 500;

        await rig.Source.InsertAsync(10);
        await WaitUntil(async () => await rig.DeadLetterCountAsync() == 1);
        rig.B.Status = 200;

        var scope = PauseService.EnvironmentScope(Rig.Env);
        await rig.Host.Pauses.PauseAsync(scope, "test", "test");
        await Task.Delay(1500); // let the loop see the pause

        var requestsBefore = rig.A.Requests + rig.B.Requests;
        await rig.Source.InsertAsync(10);

        var held = await rig.Host.Replayer.ReplayAsync(await rig.OnlyDeadLetterAsync(), CancellationToken.None);
        await Task.Delay(2500);

        Assert.Equal(DeadLetterReplayer.Outcome.Paused, held.Outcome);
        Assert.Equal(requestsBefore, rig.A.Requests + rig.B.Requests);
        Assert.Equal(1, await rig.DeadLetterCountAsync());

        await rig.Host.Pauses.ResumeAsync(scope);
        await WaitUntil(() => rig.A.Accepted.Count >= 20);

        var resumed = await rig.Host.Replayer.ReplayAsync(await rig.OnlyDeadLetterAsync(), CancellationToken.None);
        Assert.Equal(DeadLetterReplayer.Outcome.Replayed, resumed.Outcome);

        await WaitUntil(() => rig.B.Accepted.Count >= 20);
        AssertExactlyOnce(rig.A.Accepted, 20);
        AssertExactlyOnce(rig.B.Accepted, 20);
    }

    [ParityTheory, MemberData(nameof(Providers))]
    public async Task ConcurrentReplaysOfOneDeadLetter_SendItOnce(string provider)
    {
        await using var rig = await Rig.StartAsync(provider);
        rig.B.Status = 500;

        await rig.Source.InsertAsync(5);
        await WaitUntil(async () => await rig.DeadLetterCountAsync() == 1);

        rig.B.Status = 200;
        rig.B.BeforeRespond = () => Task.Delay(400); // keep the first replay in flight
        var record = await rig.OnlyDeadLetterAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => rig.Host.Replayer.ReplayAsync(record, CancellationToken.None))));

        // Whoever lost the race either saw it in flight or found the row already gone
        Assert.All(results, r => Assert.Contains(r.Outcome,
            new[] { DeadLetterReplayer.Outcome.Replayed, DeadLetterReplayer.Outcome.InProgress }));
        AssertExactlyOnce(rig.B.Accepted, 5);
        Assert.Equal(0, await rig.DeadLetterCountAsync());
    }

    [ParityTheory, MemberData(nameof(Providers))]
    public async Task ResetDuringACycle_IsNotOverwrittenByThatCycle(string provider)
    {
        await using var rig = await Rig.StartAsync(provider);

        await rig.Source.InsertAsync(5);
        await WaitUntil(() => rig.A.Accepted.Count >= 5);
        await WaitUntil(rig.CaughtUpAsync);

        // Hold the next export open, reset underneath it, then let it finish
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.A.BeforeRespond = async () => { entered.TrySetResult(); await release.Task; };

        await rig.Source.InsertAsync(5);
        await entered.Task.WaitAsync(Deadline);
        await rig.ResetWatermarkAsync();
        rig.A.BeforeRespond = null;
        release.SetResult();

        // The reset survives, so the object re-initialises: a full resync resends rows 1..10
        await WaitUntil(() => rig.A.Accepted.Count(id => id == 1) >= 2);
        await WaitUntil(rig.CaughtUpAsync);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), rig.A.Accepted.Distinct().Order());
    }

    [ParityTheory, MemberData(nameof(Providers))]
    public async Task FirstChangesOnAnEmptySource_AreNotSkippedByIncrementalSeeding(string provider)
    {
        // SQL Server cannot express "since version 0" without asking for the whole table, so
        // there the guarantee comes from a full first sync; Postgres seeds from the procedure.
        await using var rig = await Rig.StartAsync(provider, provider == "mssql" ? "Full" : "Incremental");

        await Task.Delay(2500); // several cycles against an empty table
        await rig.Source.InsertAsync(3);
        await WaitUntil(() => rig.A.Accepted.Count >= 3);
        await rig.Source.InsertAsync(3);
        await WaitUntil(() => rig.A.Accepted.Count >= 6);

        AssertExactlyOnce(rig.A.Accepted, 6);
    }

    [ParityTheory, MemberData(nameof(Providers))]
    public async Task Storm_FlappingDestinationAndConfigReloads_LoseNothing(string provider)
    {
        const int total = 600;
        await using var rig = await Rig.StartAsync(provider);
        using var stop = new CancellationTokenSource();

        var flapping = Task.Run(async () =>
        {
            var random = new Random(7);
            while (!stop.IsCancellationRequested)
            {
                rig.B.Status = random.Next(3) == 0 ? 502 : 200;
                await Task.Delay(150);
            }
            rig.B.Status = 200;
        });

        var reloading = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                rig.WriteEnv(); // same settings, fresh ciphertext: every write restarts the loop
                await Task.Delay(700);
            }
        });

        for (var written = 0; written < total; written += 20)
        {
            await rig.Source.InsertAsync(20);
            await Task.Delay(100);
        }

        await stop.CancelAsync();
        await Task.WhenAll(flapping, reloading);

        // Destinations are healthy again; drain whatever was dead-lettered while they were not
        await WaitUntil(rig.CaughtUpAsync);
        await WaitUntil(async () =>
        {
            foreach (var record in await rig.Host.DeadLetters.GetDueForReplayAsync(int.MaxValue, 100))
                await rig.Host.Replayer.ReplayAsync(record, CancellationToken.None);
            return await rig.DeadLetterCountAsync() == 0;
        });

        // At-least-once: a cycle cancelled by a reload may resend, but nothing may be missing
        var expected = Enumerable.Range(1, total).Select(i => (long)i).ToList();
        Assert.Equal(expected, rig.A.Accepted.Distinct().Order());
        Assert.Equal(expected, rig.B.Accepted.Distinct().Order());

        var live = await rig.Host.LiveTasksAsync();
        Assert.Single(live);
        Assert.False(live[Rig.Env].IsCompleted);
    }

    private static void AssertExactlyOnce(IReadOnlyList<long> accepted, int count) =>
        Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), accepted.Order());

    private static Task WaitUntil(Func<bool> condition) => WaitUntil(() => Task.FromResult(condition()));

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (!await condition())
        {
            if (timeout.IsCancellationRequested) Assert.Fail($"condition not met within {Deadline}");
            await tick.WaitForNextTickAsync();
        }
    }

    /// <summary>One environment, one tracked table, two HTTP destinations.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public const string Env = "parity";
        private const string Object = "items";

        public required StressHost Host { get; init; }
        public required ParitySource Source { get; init; }
        public required HttpSink A { get; init; }
        public required HttpSink B { get; init; }
        private string _initialSyncMode = "Full";

        public static async Task<Rig> StartAsync(string provider, string initialSyncMode = "Full")
        {
            var rig = new Rig
            {
                Source = await ParitySource.CreateAsync(provider),
                A = new HttpSink(),
                B = new HttpSink(),
                Host = new StressHost(),
                _initialSyncMode = initialSyncMode
            };

            rig.WriteEnv();
            await rig.Host.StartAsync();
            return rig;
        }

        public void WriteEnv() => Host.WriteEnvJson(Env, $$"""
            {
              "Provider": "{{Source.Provider}}",
              "ConnectionStrings": { "src": "{{Source.Connection}}" },
              "ChangeTracking": {
                "PollingIntervalSeconds": 1,
                "ExportToFile": false,
                "ExportToApi": true,
                "RetryCount": 0,
                "TrackingObjects": [{
                  "Name": "{{Object}}",
                  "Database": "src",
                  "TableName": "{{Source.Table}}",
                  "StoredProcedureName": "{{Source.Procedure}}",
                  "InitialSyncMode": "{{_initialSyncMode}}"
                }],
                "ApiEndpoints": [
                  { "Key": "a", "Url": "{{A.Url}}" },
                  { "Key": "b", "Url": "{{B.Url}}" }
                ]
              }
            }
            """);

        public async Task<long?> WatermarkAsync() =>
            await ScalarAsync(Host.StateDb, "SELECT LastVersion FROM LastVersions WHERE EnvironmentName = 'parity' AND ObjectName = 'items'") as long?;

        /// <summary>The stored watermark has reached the source's newest version.</summary>
        public async Task<bool> CaughtUpAsync() => await WatermarkAsync() == await Source.CurrentVersionAsync();

        public async Task ResetWatermarkAsync() =>
            await ScalarAsync(Host.StateDb, "DELETE FROM LastVersions WHERE EnvironmentName = 'parity' AND ObjectName = 'items'");

        public async Task<long> DeadLetterCountAsync() =>
            (long)(await ScalarAsync("sinkhole.db", "SELECT COUNT(*) FROM DeadLetters"))!;

        public async Task<DeadLetterService.DeadLetterRecord> OnlyDeadLetterAsync()
        {
            var id = (long)(await ScalarAsync("sinkhole.db", "SELECT Id FROM DeadLetters"))!;
            return (await Host.DeadLetters.GetAsync(id))!;
        }

        private static async Task<object?> ScalarAsync(string db, string sql)
        {
            await using var conn = new SqliteConnection($"Data Source={db}");
            await conn.OpenAsync();
            var command = conn.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await A.DisposeAsync();
            await B.DisposeAsync();
            await Source.DisposeAsync();
        }
    }
}
