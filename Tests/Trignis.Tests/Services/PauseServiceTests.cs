using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests.Services;

/// <summary>
/// PauseService takes its database path from configuration, so each test gets its own file
/// and no working-directory juggling is needed.
/// </summary>
public sealed class PauseServiceTests : IAsyncLifetime
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"trignis-pause-{Guid.NewGuid():N}");

    private PauseService _svc = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChangeTracking:StateDbPath"] = Path.Combine(_tempDir, "state.db")
            })
            .Build();

        _svc = new PauseService(config);
        await _svc.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Nothing_is_paused_on_a_fresh_database()
    {
        Assert.Empty(await _svc.GetPausedScopesAsync());
    }

    [Fact]
    public async Task Pausing_an_environment_shows_up_as_paused()
    {
        await _svc.PauseAsync(PauseService.EnvironmentScope("Production"), "migrating", "10.0.0.1");

        var paused = await _svc.GetPausedScopesAsync();
        Assert.Contains(PauseService.EnvironmentScope("Production"), paused);
    }

    [Fact]
    public async Task Pausing_an_environment_does_not_pause_a_similarly_named_one()
    {
        await _svc.PauseAsync(PauseService.EnvironmentScope("prod"), null, null);

        var paused = await _svc.GetPausedScopesAsync();
        Assert.DoesNotContain(PauseService.EnvironmentScope("production"), paused);
    }

    [Fact]
    public async Task Pausing_one_object_leaves_its_siblings_running()
    {
        await _svc.PauseAsync(PauseService.ObjectScope("prod", "Orders"), null, null);

        var paused = await _svc.GetPausedScopesAsync();
        Assert.Contains(PauseService.ObjectScope("prod", "Orders"), paused);
        Assert.DoesNotContain(PauseService.ObjectScope("prod", "Items"), paused);
        // Pausing an object must not take the whole environment down with it.
        Assert.DoesNotContain(PauseService.EnvironmentScope("prod"), paused);
    }

    [Fact]
    public async Task An_object_and_an_environment_of_the_same_name_are_different_scopes()
    {
        await _svc.PauseAsync(PauseService.EnvironmentScope("orders"), null, null);

        var paused = await _svc.GetPausedScopesAsync();
        Assert.DoesNotContain(PauseService.ObjectScope("orders", "orders"), paused);
    }

    [Theory]
    [InlineData("Production", "production")]
    [InlineData("PRODUCTION", "Production")]
    public void Scopes_are_case_insensitive_so_config_casing_cannot_orphan_a_pause(string a, string b)
    {
        Assert.Equal(PauseService.EnvironmentScope(a), PauseService.EnvironmentScope(b));
        Assert.Equal(PauseService.ObjectScope(a, "Orders"), PauseService.ObjectScope(b, "orders"));
    }

    [Fact]
    public async Task Pausing_twice_refreshes_the_reason_instead_of_failing()
    {
        var scope = PauseService.EnvironmentScope("prod");

        await _svc.PauseAsync(scope, "first reason", "10.0.0.1");
        await _svc.PauseAsync(scope, "second reason", "10.0.0.2");

        var records = await _svc.ListAsync();
        var record = Assert.Single(records);
        Assert.Equal("second reason", record.Reason);
        Assert.Equal("10.0.0.2", record.PausedBy);
    }

    [Fact]
    public async Task Resuming_clears_the_pause()
    {
        var scope = PauseService.EnvironmentScope("prod");
        await _svc.PauseAsync(scope, null, null);

        Assert.True(await _svc.ResumeAsync(scope));
        Assert.Empty(await _svc.GetPausedScopesAsync());
    }

    [Fact]
    public async Task Resuming_something_that_was_not_paused_reports_no_op_rather_than_throwing()
    {
        Assert.False(await _svc.ResumeAsync(PauseService.EnvironmentScope("never-paused")));
    }

    [Fact]
    public async Task Resuming_an_environment_does_not_resume_its_individually_paused_objects()
    {
        // Otherwise resuming an environment would silently restart an object someone
        // deliberately held for a different reason.
        await _svc.PauseAsync(PauseService.EnvironmentScope("prod"), null, null);
        await _svc.PauseAsync(PauseService.ObjectScope("prod", "Orders"), null, null);

        await _svc.ResumeAsync(PauseService.EnvironmentScope("prod"));

        var paused = await _svc.GetPausedScopesAsync();
        Assert.Contains(PauseService.ObjectScope("prod", "Orders"), paused);
    }

    [Fact]
    public async Task A_pause_survives_a_restart()
    {
        await _svc.PauseAsync(PauseService.EnvironmentScope("prod"), "overnight", null);

        // Same file, fresh service: this is what a service restart looks like.
        var reopened = new PauseService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChangeTracking:StateDbPath"] = Path.Combine(_tempDir, "state.db")
            })
            .Build());
        await reopened.InitializeAsync();

        Assert.Contains(PauseService.EnvironmentScope("prod"), await reopened.GetPausedScopesAsync());
    }

    [Fact]
    public async Task InitializeAsync_is_safe_to_run_again()
    {
        await _svc.PauseAsync(PauseService.EnvironmentScope("prod"), null, null);
        await _svc.InitializeAsync();

        Assert.Single(await _svc.ListAsync());
    }

    [Fact]
    public async Task ListAsync_returns_the_newest_pause_first()
    {
        await _svc.PauseAsync(PauseService.EnvironmentScope("first"), null, null);
        await Task.Delay(1100); // CURRENT_TIMESTAMP has one-second resolution
        await _svc.PauseAsync(PauseService.EnvironmentScope("second"), null, null);

        var records = await _svc.ListAsync();
        Assert.Equal(PauseService.EnvironmentScope("second"), records.First().Scope);
    }
}
