using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Trignis.Tests.Stress;

// TEMPORARY stress tests for the environment task lifecycle
// No container needed: environments carry no tracking objects so the poller only idles
[Collection("SqliteTests")]
public sealed class LifecycleStressTests
{
    private const int EnvCount = 4;
    private const string Unused = "Server=127.0.0.1,1;Database=none;User ID=sa;Password=none;Encrypt=False;Connect Timeout=1";

    [Fact]
    public async Task ReloadStorm_LeavesExactlyOneLiveTaskPerEnvironment()
    {
        await using var host = new StressHost();
        for (var i = 0; i < EnvCount; i++) host.WriteEnv($"e{i}", Unused, storedProcedure: null);

        await host.StartAsync();
        await WaitUntil(() => host.LiveTasks().Count == EnvCount, TimeSpan.FromSeconds(30));

        // Rewrite every file at once so all debounce timers fire together
        for (var round = 0; round < 12; round++)
        {
            for (var i = 0; i < EnvCount; i++) host.WriteEnv($"e{i}", Unused, storedProcedure: null);
            await Task.Delay(600);
        }

        await Task.Delay(2000);

        var live = host.LiveTasks();
        Assert.Equal(EnvCount, live.Count);
        Assert.All(live.Values, task => Assert.False(task.IsCompleted, "a registered environment task had already finished"));
    }

    [Fact]
    public async Task ConcurrentReloadsOfOneEnvironment_LeaveOneLiveTask()
    {
        await using var host = new StressHost();
        for (var i = 0; i < EnvCount; i++) host.WriteEnv($"e{i}", Unused, storedProcedure: null);

        await host.StartAsync();
        await WaitUntil(() => host.LiveTasks().Count == EnvCount, TimeSpan.FromSeconds(30));

        // A watcher burst delivers these back to back; nothing spaces them out
        var reloads = Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
            host.RaiseConfigurationChanged(new Trignis.Services.EnvironmentChangeEvent
            {
                Updated = [host.Config("e0")]
            })));

        await Task.WhenAll(reloads);
        await Task.Delay(8000);

        var live = host.LiveTasks();
        Assert.Equal(EnvCount, live.Count);
        Assert.All(live.Values, task => Assert.False(task.IsCompleted));
    }

    [Fact]
    public async Task DeleteAndRecreateStorm_NeverLeavesAStaleEntry()
    {
        await using var host = new StressHost();
        for (var i = 0; i < EnvCount; i++) host.WriteEnv($"e{i}", Unused, storedProcedure: null);

        await host.StartAsync();
        await WaitUntil(() => host.LiveTasks().Count == EnvCount, TimeSpan.FromSeconds(30));

        for (var round = 0; round < 8; round++)
        {
            File.Delete(host.EnvFile("e0"));
            await Task.Delay(150);
            host.WriteEnv("e0", Unused, storedProcedure: null);
            await Task.Delay(700);
        }

        await Task.Delay(2000);

        var live = host.LiveTasks();
        Assert.Equal(EnvCount, live.Count);
        Assert.Contains("e0", live.Keys);
        Assert.All(live.Values, task => Assert.False(task.IsCompleted));
    }

    [Fact]
    public async Task Shutdown_DuringReloadStorm_CompletesAndDrainsEveryTask()
    {
        var host = new StressHost();
        for (var i = 0; i < EnvCount; i++) host.WriteEnv($"e{i}", Unused, storedProcedure: null);

        await host.StartAsync();
        await WaitUntil(() => host.LiveTasks().Count == EnvCount, TimeSpan.FromSeconds(30));

        // Keep rewriting while the host is torn down underneath the reload path
        var churn = Task.Run(async () =>
        {
            for (var round = 0; round < 20; round++)
            {
                for (var i = 0; i < EnvCount; i++)
                {
                    try { host.WriteEnv($"e{i}", Unused, storedProcedure: null); }
                    catch (DirectoryNotFoundException) { return; }
                }
                await Task.Delay(120);
            }
        });

        await Task.Delay(700);

        var sw = Stopwatch.StartNew();
        await host.DisposeAsync();
        sw.Stop();

        await churn;

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"shutdown took {sw.Elapsed}");
        Assert.Empty(host.LiveTasks());
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"condition not met within {timeout}");
    }
}
