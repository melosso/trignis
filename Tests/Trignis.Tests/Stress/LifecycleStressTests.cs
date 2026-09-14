using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests.Stress;

[Collection("SqliteTests")]
public sealed class LifecycleStressTests
{
    private const int EnvCount = 4;
    private const string Unused = "Server=127.0.0.1,1;Database=none;User ID=sa;Password=none;Encrypt=False;Connect Timeout=1";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ReloadStorm_LeavesExactlyOneLiveTaskPerEnvironment()
    {
        await using var host = await StartAsync();

        for (var round = 0; round < 12; round++)
        {
            var before = await host.LiveTasksAsync();
            WriteAll(host);
            await WaitUntil(async () => AllReplaced(await host.LiveTasksAsync(), before));
        }
    }

    [Fact]
    public async Task ConcurrentReloadsOfOneEnvironment_LeaveOneLiveTask()
    {
        await using var host = await StartAsync();
        var before = await host.LiveTasksAsync();
        var e0 = host.Config("e0");

        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
            host.RaiseConfigurationChanged(new EnvironmentChangeEvent { Updated = [e0] }))));

        await WaitUntil(async () =>
        {
            var live = await host.LiveTasksAsync();
            return AllLive(live) && !ReferenceEquals(live["e0"], before["e0"]);
        });
    }

    [Fact]
    public async Task DeleteAndRecreateStorm_NeverLeavesAStaleEntry()
    {
        await using var host = await StartAsync();

        for (var round = 0; round < 8; round++)
        {
            File.Delete(host.EnvFile("e0"));
            await WaitUntil(async () => !(await host.LiveTasksAsync()).ContainsKey("e0"));

            host.WriteEnv("e0", Unused, storedProcedure: null);
            await WaitUntil(async () => AllLive(await host.LiveTasksAsync()));
        }
    }

    [Fact]
    public async Task Shutdown_DuringReloadStorm_CompletesAndDrainsEveryTask()
    {
        var host = await StartAsync();
        var all = Enumerable.Range(0, EnvCount).Select(i => host.Config($"e{i}")).ToList();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
            host.RaiseConfigurationChanged(new EnvironmentChangeEvent { Updated = all }))));

        await host.StopAsync();

        Assert.Empty(await host.LiveTasksAsync());
        await host.DisposeAsync();
    }

    private static async Task<StressHost> StartAsync()
    {
        var host = new StressHost();
        WriteAll(host);
        await host.StartAsync();
        await WaitUntil(async () => AllLive(await host.LiveTasksAsync()));
        return host;
    }

    private static void WriteAll(StressHost host)
    {
        for (var i = 0; i < EnvCount; i++) host.WriteEnv($"e{i}", Unused, storedProcedure: null);
    }

    private static bool AllLive(IReadOnlyDictionary<string, Task> live) =>
        live.Count == EnvCount && live.Values.All(task => !task.IsCompleted);

    private static bool AllReplaced(IReadOnlyDictionary<string, Task> live, IReadOnlyDictionary<string, Task> before) =>
        AllLive(live) && live.All(kv => !ReferenceEquals(kv.Value, before[kv.Key]));

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (!await condition())
        {
            if (timeout.IsCancellationRequested) Assert.Fail($"condition not met within {Deadline}");
            await tick.WaitForNextTickAsync();
        }
    }
}
