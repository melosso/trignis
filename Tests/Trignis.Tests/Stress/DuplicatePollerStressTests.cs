using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Trignis.Services;
using Testcontainers.MsSql;
using Xunit;

namespace Trignis.Tests.Stress;

// TEMPORARY end to end stress test against a real SQL Server
// Every poll writes an interval row so two pollers on one environment show up as overlap
[Collection("SqliteTests")]
public sealed class DuplicatePollerStressTests : IAsyncLifetime
{
    private const int EnvCount = 3;
    private const string ProbeJson = """{"Metadata":{"Sync":{"Version":1}},"Data":[]}""";
    private readonly MsSqlContainer _sql = new MsSqlBuilder().Build();

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();

        await ExecuteAsync("""
            CREATE TABLE dbo.ProbeLog (
                Id        INT IDENTITY(1,1) PRIMARY KEY,
                Env       NVARCHAR(50)  NOT NULL,
                StartedAt DATETIME2(3)  NOT NULL,
                EndedAt   DATETIME2(3)  NULL
            );
            """);

        for (var i = 0; i < EnvCount; i++)
        {
            await ExecuteAsync($$"""
                CREATE PROCEDURE dbo.sp_probe_e{{i}} @Json NVARCHAR(MAX) AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.ProbeLog (Env, StartedAt) VALUES (N'e{{i}}', SYSUTCDATETIME());
                    DECLARE @id INT = CAST(SCOPE_IDENTITY() AS INT);
                    WAITFOR DELAY '00:00:00.700';
                    UPDATE dbo.ProbeLog SET EndedAt = SYSUTCDATETIME() WHERE Id = @id;
                    SELECT N'{{ProbeJson}}';
                END
                """);
        }
    }

    public async Task DisposeAsync() => await _sql.DisposeAsync();

    [Fact]
    public async Task ReloadStorm_NeverRunsTwoPollersForOneEnvironment()
    {
        await using var host = new StressHost();
        for (var i = 0; i < EnvCount; i++)
            host.WriteEnv($"e{i}", _sql.GetConnectionString(), $"dbo.sp_probe_e{i}");

        await host.StartAsync();
        await WaitForProbes(minimumRows: EnvCount, timeout: TimeSpan.FromSeconds(60));

        for (var round = 0; round < 12; round++)
        {
            for (var i = 0; i < EnvCount; i++)
                host.WriteEnv($"e{i}", _sql.GetConnectionString(), $"dbo.sp_probe_e{i}");
            await Task.Delay(600);
        }

        await Task.Delay(3000);

        Assert.Equal(0, await CountOverlapsAsync());
    }

    // Two pollers on one environment produce probe intervals that overlap in time
    private Task<int> CountOverlapsAsync() => ScalarAsync("""
        SELECT COUNT(*)
        FROM dbo.ProbeLog a
        JOIN dbo.ProbeLog b ON a.Env = b.Env AND a.Id < b.Id
        WHERE a.EndedAt IS NOT NULL
          AND b.EndedAt IS NOT NULL
          AND a.StartedAt < b.EndedAt
          AND b.StartedAt < a.EndedAt;
        """);

    [Fact]
    public async Task ConcurrentReloadBurst_NeverRunsTwoPollersForOneEnvironment()
    {
        await using var host = new StressHost();
        for (var i = 0; i < EnvCount; i++)
            host.WriteEnv($"e{i}", _sql.GetConnectionString(), $"dbo.sp_probe_e{i}");

        await host.StartAsync();
        await WaitForProbes(minimumRows: EnvCount, timeout: TimeSpan.FromSeconds(60));

        for (var round = 0; round < 4; round++)
        {
            var reloads = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                host.RaiseConfigurationChanged(new EnvironmentChangeEvent { Updated = [host.Config("e0")] })));

            await Task.WhenAll(reloads);
            await Task.Delay(2000);
        }

        await Task.Delay(4000);

        Assert.Equal(0, await CountOverlapsAsync());
    }

    [Fact]
    public async Task Shutdown_StopsEveryPoller()
    {
        var host = new StressHost();
        for (var i = 0; i < EnvCount; i++)
            host.WriteEnv($"e{i}", _sql.GetConnectionString(), $"dbo.sp_probe_e{i}");

        await host.StartAsync();
        await WaitForProbes(minimumRows: EnvCount, timeout: TimeSpan.FromSeconds(60));
        await host.DisposeAsync();

        var before = await ScalarAsync("SELECT COUNT(*) FROM dbo.ProbeLog");
        await Task.Delay(4000);
        var after = await ScalarAsync("SELECT COUNT(*) FROM dbo.ProbeLog");

        Assert.Equal(before, after);
    }

    private async Task WaitForProbes(int minimumRows, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ScalarAsync("SELECT COUNT(DISTINCT Env) FROM dbo.ProbeLog") >= minimumRows) return;
            await Task.Delay(250);
        }
        Assert.Fail($"pollers did not reach the database within {timeout}");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new SqlConnection(_sql.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(_sql.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
