using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trignis.Models;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests.Services;

[Collection("SqliteTests")]
public sealed class UnroutableDeadLetterTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"trignis-unroutable-{Guid.NewGuid():N}");
    private string? _savedWorkingDir;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _savedWorkingDir = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tempDir;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (_savedWorkingDir != null) Environment.CurrentDirectory = _savedWorkingDir;
        Directory.Delete(_tempDir, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UnroutableDeadLetter_IsDeferred_WithoutSpendingAnAttempt()
    {
        var settings = Options.Create(new GlobalSettings());
        var deadLetters = new DeadLetterService(NullLogger<DeadLetterService>.Instance, settings);
        await deadLetters.InitializeAsync();
        using var data = JsonDocument.Parse("""[{"id":1}]""");
        await deadLetters.SaveDeadLetterAsync("removed", "Items", "Main", data.RootElement, new Exception("down"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
        {
            ["ChangeTracking:StateDbPath"] = Path.Combine(_tempDir, "state.db")
        }).Build();
        var pauses = new PauseService(config);
        await pauses.InitializeAsync();
        var environments = new EnvironmentConfigService(NullLogger<EnvironmentConfigService>.Instance, new EncryptionService(_tempDir));
        var exports = new ExportService(NullLogger<ExportService>.Instance, null!, new MessageQueueService(NullLogger<MessageQueueService>.Instance),
            null!, new RetryPolicies(NullLogger<RetryPolicies>.Instance, settings), settings);
        var service = new DeadLetterReplayService(NullLogger<DeadLetterReplayService>.Instance, deadLetters,
            new DeadLetterReplayer(deadLetters, exports, environments, pauses), settings);

        var before = DateTime.UtcNow;
        await service.ReplayDueAsync(5, CancellationToken.None);

        Assert.Empty(await deadLetters.GetDueForReplayAsync(5, 10, CancellationToken.None));
        var row = await ReadRowAsync();
        Assert.Equal(0, row.Attempts);
        Assert.InRange(row.NextAttempt, before + DeadLetterReplayService.UnroutableRecheck - TimeSpan.FromMinutes(1), before + DeadLetterReplayService.UnroutableRecheck + TimeSpan.FromMinutes(1));
    }

    private async Task<(int Attempts, DateTime NextAttempt)> ReadRowAsync()
    {
        await using var conn = new SqliteConnection("Data Source=sinkhole.db");
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Attempts, NextAttempt FROM DeadLetters";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetDateTime(1));
    }
}
