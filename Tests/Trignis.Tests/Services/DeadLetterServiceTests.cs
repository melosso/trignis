using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Trignis.MicrosoftSQL.Services;
using Xunit;

namespace Trignis.Tests.Services;

/// <summary>
/// Tests for DeadLetterService — schema initialisation, record persistence,
/// deduplication, and purge logic.
///
/// DeadLetterService hard-codes "Data Source=sinkhole.db" (relative path).
/// To isolate each test:
///   - A unique temp directory is created per test instance.
///   - Environment.CurrentDirectory is set to that directory in InitializeAsync
///     (not the constructor) so it takes effect just-in-time, after xUnit has
///     finished constructing all instances for the collection.
///   - SqliteConnection.ClearAllPools() is called in DisposeAsync so pooled
///     handles do not bleed across tests.
///
/// [Collection("SqliteTests")] ensures these tests run sequentially and never
/// race against each other on Environment.CurrentDirectory.
/// </summary>
[Collection("SqliteTests")]
public sealed class DeadLetterServiceTests : IAsyncLifetime
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"trignis-dlq-{Guid.NewGuid():N}");

    private string? _savedWorkingDir;
    private DeadLetterService _svc = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _savedWorkingDir = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tempDir;

        var config = new ConfigurationBuilder().Build();
        _svc = new DeadLetterService(NullLogger<DeadLetterService>.Instance, config);
        await _svc.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools(); // release pooled handles before deleting the file
        if (_savedWorkingDir != null)
            Environment.CurrentDirectory = _savedWorkingDir;
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Schema initialisation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_CreatesDeadLettersTable()
    {
        await using var conn = Open();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='DeadLetters'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_WhenCalledTwice()
    {
        await _svc.InitializeAsync(); // must not throw on existing schema
    }

    // -------------------------------------------------------------------------
    // SaveDeadLetterAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveDeadLetter_InsertsRecord()
    {
        var data = JsonDocument.Parse(@"{""id"":1,""name"":""Test""}").RootElement;

        await _svc.SaveDeadLetterAsync("Orders", "SalesDB", data, new InvalidOperationException("downstream timeout"));

        Assert.Equal(1L, await CountRowsAsync());
    }

    [Fact]
    public async Task SaveDeadLetter_StoresCorrectFields()
    {
        var data = JsonDocument.Parse(@"{""orderId"":42}").RootElement;

        await _svc.SaveDeadLetterAsync("Orders", "SalesDB", data, new Exception("connection refused"));

        await using var conn = Open();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TrackingObjectName, DatabaseName, ErrorMessage FROM DeadLetters LIMIT 1";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Orders", reader.GetString(0));
        Assert.Equal("SalesDB", reader.GetString(1));
        Assert.Equal("connection refused", reader.GetString(2));
    }

    [Fact]
    public async Task SaveDeadLetter_SkipsDuplicate_SameTrackingObjectAndData()
    {
        var data = JsonDocument.Parse(@"{""id"":99}").RootElement;
        var ex = new Exception("fail");

        await _svc.SaveDeadLetterAsync("Orders", "SalesDB", data, ex);
        await _svc.SaveDeadLetterAsync("Orders", "SalesDB", data, ex); // exact duplicate

        Assert.Equal(1L, await CountRowsAsync()); // only one row written
    }

    [Fact]
    public async Task SaveDeadLetter_InsertsBoth_WhenDataDiffers()
    {
        var data1 = JsonDocument.Parse(@"{""id"":1}").RootElement;
        var data2 = JsonDocument.Parse(@"{""id"":2}").RootElement;
        var ex = new Exception("fail");

        await _svc.SaveDeadLetterAsync("Orders", "SalesDB", data1, ex);
        await _svc.SaveDeadLetterAsync("Orders", "SalesDB", data2, ex);

        Assert.Equal(2L, await CountRowsAsync());
    }

    [Fact]
    public async Task SaveDeadLetter_InsertsBoth_WhenTrackingObjectDiffers()
    {
        var data = JsonDocument.Parse(@"{""id"":1}").RootElement;
        var ex = new Exception("fail");

        await _svc.SaveDeadLetterAsync("Orders", "SalesDB", data, ex);
        await _svc.SaveDeadLetterAsync("Customers", "SalesDB", data, ex);

        Assert.Equal(2L, await CountRowsAsync());
    }

    // -------------------------------------------------------------------------
    // PurgeOldDeadLettersAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PurgeOldDeadLetters_RemovesRecordsOlderThanRetention()
    {
        await InsertOldRecordAsync("old_SalesDB", "old", "SalesDB", "hash-old", 90);

        await _svc.PurgeOldDeadLettersAsync();

        Assert.Equal(0L, await CountRowsAsync());
    }

    [Fact]
    public async Task PurgeOldDeadLetters_PreservesRecentRecords()
    {
        var data = JsonDocument.Parse(@"{""id"":1}").RootElement;
        await _svc.SaveDeadLetterAsync("Orders", "SalesDB", data, new Exception("err"));

        await _svc.PurgeOldDeadLettersAsync();

        Assert.Equal(1L, await CountRowsAsync());
    }

    [Fact]
    public async Task PurgeOldDeadLetters_OnlyRemovesOldRecords_WhenMixed()
    {
        var data = JsonDocument.Parse(@"{""id"":1}").RootElement;
        await _svc.SaveDeadLetterAsync("Orders", "SalesDB", data, new Exception("err"));
        await InsertOldRecordAsync("stale_SalesDB", "stale", "SalesDB", "hash-stale", 100);

        Assert.Equal(2L, await CountRowsAsync());

        await _svc.PurgeOldDeadLettersAsync();

        Assert.Equal(1L, await CountRowsAsync()); // recent record survives
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// Opens a non-pooled connection to the current test's sinkhole.db.
    private static SqliteConnection Open() =>
        new("Data Source=sinkhole.db;Pooling=False");

    private static async Task<long> CountRowsAsync()
    {
        await using var conn = Open();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DeadLetters";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task InsertOldRecordAsync(
        string sourceKey, string trackingName, string dbName, string hash, int daysAgo)
    {
        await using var conn = Open();
        await conn.OpenAsync();
        var insert = conn.CreateCommand();
        insert.CommandText = $@"
            INSERT INTO DeadLetters (SourceKey, TrackingObjectName, DatabaseName, DataHash, Data, ErrorMessage, Timestamp)
            VALUES (@sk, @tn, @dn, @h, '{{}}', 'err', datetime('now', '-{daysAgo} days'))";
        insert.Parameters.AddWithValue("@sk", sourceKey);
        insert.Parameters.AddWithValue("@tn", trackingName);
        insert.Parameters.AddWithValue("@dn", dbName);
        insert.Parameters.AddWithValue("@h", hash);
        await insert.ExecuteNonQueryAsync();
    }
}
