using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Trignis.Tests.Stress;

/// <summary>The PostgreSQL tabs in .kb/docs/guide run on a database container and return the documented changes.</summary>
[Collection("SqliteTests")]
public sealed class DocsSnippetTests
{
    private static readonly string Guide = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.kb/docs/guide"));

    private static List<string> PostgresBlocks(string page) =>
        Regex.Matches(File.ReadAllText(Path.Combine(Guide, page)), @"```sql \[PostgreSQL[^\]]*\]\n(.*?)\n```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();

    [ParityFact]
    public async Task FirstTable_PostgresFunction_ReturnsLatestChangePerItem()
    {
        await using var db = await FreshDatabase.CreateAsync();
        await db.ExecAsync("create table public.items (item_code text primary key, description text, assortment text, sysguid uuid default gen_random_uuid())");
        foreach (var block in PostgresBlocks("first-table.md")) await db.ExecAsync(block);

        var seed = await db.SyncAsync("web.get_itemssync", 0, "seed");
        await db.ExecAsync("insert into public.items (item_code, description) values ('A', 'first'), ('B', 'second')");
        await db.ExecAsync("update public.items set description = 'changed' where item_code = 'A'");
        await db.ExecAsync("delete from public.items where item_code = 'B'");

        var diff = await db.SyncAsync("web.get_itemssync", seed.Version, "sync");
        Assert.Equal(["A:U:changed", "B:D:"], diff.Rows.Select(r => $"{r["ItemCode"]}:{r["$operation"]}:{r.GetValueOrDefault("Description")}").Order());

        var full = await db.SyncAsync("web.get_itemssync", 0, "sync");
        Assert.Equal(["A"], full.Rows.Select(r => r["ItemCode"]));
    }

    [ParityFact]
    public async Task Examples_PostgresTabs_RunAndReturnTheDocumentedChanges()
    {
        await using var db = await FreshDatabase.CreateAsync();
        var blocks = PostgresBlocks("examples.md");
        Assert.Equal(4, blocks.Count); // table, tracking, function, try it
        foreach (var block in blocks.Take(3)) await db.ExecAsync(block);

        var seed = await db.SyncAsync("public.get_productssync", 0, "seed");
        await db.ExecAsync(blocks[3]);

        var diff = await db.SyncAsync("public.get_productssync", seed.Version, "sync");
        Assert.Equal(["1:U:11.00", "2:D:"], diff.Rows.Select(r => $"{r["ProductId"]}:{r["$operation"]}:{r.GetValueOrDefault("UnitPrice")}").Order());
    }

    private sealed class FreshDatabase : IAsyncDisposable
    {
        private readonly string _admin = ParitySource.ConnectionString("postgres")!;
        private readonly string _name = $"docs_{Guid.NewGuid():N}";
        private string Connection => new NpgsqlConnectionStringBuilder(_admin) { Database = _name, Pooling = false }.ConnectionString;

        public static async Task<FreshDatabase> CreateAsync()
        {
            var db = new FreshDatabase();
            await using var conn = new NpgsqlConnection(db._admin);
            await conn.OpenAsync();
            await new NpgsqlCommand($"create database {db._name}", conn).ExecuteNonQueryAsync();
            return db;
        }

        public async Task ExecAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(Connection);
            await conn.OpenAsync();
            await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
        }

        public async Task<(long Version, List<Dictionary<string, string>> Rows)> SyncAsync(string function, long fromVersion, string mode)
        {
            await using var conn = new NpgsqlConnection(Connection);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"select {function}(@p::json)", conn);
            cmd.Parameters.AddWithValue("p", JsonSerializer.Serialize(new { fromVersion, mode }));
            using var doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync())!);
            var version = doc.RootElement.GetProperty("Metadata").GetProperty("Sync").GetProperty("Version").GetInt64();
            var rows = doc.RootElement.TryGetProperty("Data", out var data)
                ? data.EnumerateArray().Select(r => r.EnumerateObject()
                    .Where(p => p.Value.ValueKind != JsonValueKind.Null)
                    .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())).ToList()
                : [];
            return (version, rows);
        }

        public async ValueTask DisposeAsync()
        {
            await using var conn = new NpgsqlConnection(_admin);
            await conn.OpenAsync();
            await new NpgsqlCommand($"drop database if exists {_name} with (force)", conn).ExecuteNonQueryAsync();
        }
    }
}

/// <summary>Runs if the parity containers are up. Skipped otherwise.</summary>
public sealed class ParityFactAttribute : FactAttribute
{
    public ParityFactAttribute()
    {
        if (ParitySource.ConnectionString("postgres") is null)
            Skip = "Set TRIGNIS_PARITY_POSTGRES (see Tests/parity/run.sh)";
    }
}
