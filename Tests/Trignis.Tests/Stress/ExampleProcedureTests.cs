using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Trignis.Data;
using Xunit;

namespace Trignis.Tests.Stress;

/// <summary>The SQL Server scripts in Examples/ on a database container, called the way Trignis calls them.</summary>
[Collection("SqliteTests")]
public sealed class ExampleProcedureTests
{
    private static readonly string ExamplesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Examples"));

    private const string Bedryf = "CREATE TABLE dbo.bedryf (bedrnr VARCHAR(10) NOT NULL); INSERT dbo.bedryf VALUES ('600');";

    private sealed record Example(string Script, string Procedure, string Key, string Fixture, string Insert, string Update, string UpdatedColumn, string Delete, bool CompanyNumber);

    private static readonly Dictionary<string, Example> Examples = new()
    {
        ["Items"] = new("Exact Globe/column-tracking_dbo.Items.sql", "web.get_itemssync", "ItemCode",
            "CREATE TABLE dbo.Items (ItemCode VARCHAR(20) NOT NULL PRIMARY KEY, Description VARCHAR(200) NULL, Assortment VARCHAR(50) NULL, sysguid UNIQUEIDENTIFIER NULL);",
            "INSERT dbo.Items VALUES ('1', 'first', 'X', NEWID()), ('2', 'second', 'Y', NEWID());",
            "UPDATE dbo.Items SET Description = 'changed' WHERE ItemCode = '1';", "Description",
            "DELETE dbo.Items WHERE ItemCode = '2';", CompanyNumber: false),
        ["pwruc"] = new("Exact Globe/table-tracking_dbo.pwruc.sql", "web.get_pwrucsync", "ID",
            Bedryf + """
            CREATE TABLE dbo.pwruc (ID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_pwruc PRIMARY KEY NONCLUSTERED, res_id INT NOT NULL, role_id INT NOT NULL, rolelevel INT NOT NULL,
                Division SMALLINT NULL, syscreated DATETIME NOT NULL, syscreator INT NOT NULL, sysmodified DATETIME NOT NULL, sysmodifier INT NOT NULL,
                sysguid UNIQUEIDENTIFIER NOT NULL, [timestamp] TIMESTAMP NOT NULL);
            """,
            "INSERT dbo.pwruc (res_id, role_id, rolelevel, Division, syscreated, syscreator, sysmodified, sysmodifier, sysguid) VALUES (1, 10, 1, 600, GETDATE(), 1, GETDATE(), 1, NEWID()), (2, 20, 1, 600, GETDATE(), 1, GETDATE(), 1, NEWID());",
            "UPDATE dbo.pwruc SET rolelevel = 2 WHERE ID = 1;", "rolelevel",
            "DELETE dbo.pwruc WHERE ID = 2;", CompanyNumber: true),
        ["HRRoles"] = new("Exact Synergy/table-tracking_dbo.HRRoles.sql", "web.get_hrrolessync", "ID",
            Bedryf + "CREATE TABLE dbo.HRRoles (ID INT PRIMARY KEY, RoleID INT, EmpID INT, RoleLevel INT, Division INT);",
            "INSERT dbo.HRRoles VALUES (1, 10, 100, 1, 600), (2, 20, 200, 1, 600);",
            "UPDATE dbo.HRRoles SET RoleLevel = 2 WHERE ID = 1;", "RoleLevel",
            "DELETE dbo.HRRoles WHERE ID = 2;", CompanyNumber: true),
    };

    public static TheoryData<string> Names() => new(Examples.Keys);

    [ParityTheory, MemberData(nameof(Names))]
    public async Task Example_InstallsOnAFreshDatabase_AndReturnsTheSameColumnsForFullAndDiff(string name)
    {
        var example = Examples[name];
        await using var db = await FreshDatabase.CreateAsync();
        await db.ExecAsync(example.Fixture);
        // Twice: the setup runs again on an installed database
        for (var run = 0; run < 2; run++)
            foreach (var batch in Regex.Split(File.ReadAllText(Path.Combine(ExamplesDir, example.Script)), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) await db.ExecAsync(batch);

        await db.ExecAsync(example.Insert);
        var full = await db.SyncAsync(example.Procedure, 0);
        Assert.Equal(2, full.Rows.Count);
        var fullColumns = full.Rows[0].Keys.Where(k => !k.StartsWith('$')).Order().ToList();

        await db.ExecAsync(example.Update);
        await db.ExecAsync(example.Delete);
        var diff = await db.SyncAsync(example.Procedure, full.Version);

        var updated = diff.Rows.Single(r => r["$operation"].ToString() == "U");
        var deleted = diff.Rows.Single(r => r["$operation"].ToString() == "D");
        Assert.Equal("1", updated[example.Key].ToString());
        Assert.Equal("2", deleted[example.Key].ToString());

        if (example.CompanyNumber)
        {
            Assert.Equal(fullColumns, updated.Keys.Where(k => !k.StartsWith('$')).Order());
            Assert.Equal("600", deleted["CompanyNumber"].ToString());
        }
        else
        {
            // Column tracking: an update returns the key, the changed columns and their names in $changed
            Assert.Equal([example.UpdatedColumn], updated["$changed"].EnumerateArray().Select(c => c.GetString()));
            Assert.Equal(new[] { example.Key, example.UpdatedColumn }.Order(), updated.Keys.Where(k => !k.StartsWith('$')).Order());
        }

        // Versions are 64-bit: a watermark beyond int.MaxValue must not fail the call
        await db.SyncAsync(example.Procedure, 3_000_000_000);
    }

    private sealed class FreshDatabase : IAsyncDisposable
    {
        private readonly string _admin = ParitySource.ConnectionString("mssql")!;
        private readonly string _name = $"examples_{Guid.NewGuid():N}";
        private string Connection => $"{_admin};Database={_name}";

        public static async Task<FreshDatabase> CreateAsync()
        {
            var db = new FreshDatabase();
            await using var conn = new SqlConnection(db._admin);
            await conn.OpenAsync();
            await new SqlCommand($"CREATE DATABASE [{db._name}]", conn).ExecuteNonQueryAsync();
            return db;
        }

        public async Task ExecAsync(string sql)
        {
            await using var conn = new SqlConnection(Connection);
            await conn.OpenAsync();
            await new SqlCommand(sql, conn).ExecuteNonQueryAsync();
        }

        public async Task<(long Version, List<Dictionary<string, JsonElement>> Rows)> SyncAsync(string procedure, long fromVersion)
        {
            using var conn = await SqlDialect.Mssql.OpenAsync(Connection, CancellationToken.None);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.Format(SqlDialect.Mssql.CallProcedure, procedure);
            var param = cmd.CreateParameter();
            param.ParameterName = SqlDialect.JsonParameter;
            param.Value = JsonSerializer.Serialize(new { fromVersion, mode = "sync" });
            cmd.Parameters.Add(param);

            // Chunked like the service reads it: one JSON document across rows
            var json = new StringBuilder();
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    if (!reader.IsDBNull(0)) json.Append(reader.GetString(0));

            using var doc = JsonDocument.Parse(json.ToString());
            var rows = doc.RootElement.TryGetProperty("Data", out var data)
                ? data.EnumerateArray().Select(r => r.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())).ToList()
                : [];
            return (doc.RootElement.GetProperty("Metadata").GetProperty("Sync").GetProperty("Version").GetInt64(), rows);
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await using var conn = new SqlConnection(_admin);
            await conn.OpenAsync();
            await new SqlCommand($"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]", conn).ExecuteNonQueryAsync();
        }
    }
}
