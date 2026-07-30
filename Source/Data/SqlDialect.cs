using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.Data.SqlClient;

namespace Trignis.Data;

/// <summary>
/// Everything that differs per RDBMS. The rest of the pipeline — reading the JSON, parsing it,
/// exporting, dead-lettering, storing the watermark — is provider-agnostic, because the change
/// tracking itself lives in the procedure the user writes.
/// </summary>
public sealed record SqlDialect
{
    /// <summary>Name of the single parameter every tracking procedure takes.</summary>
    public const string JsonParameter = "JsonParam";

    public required string Name { get; init; }

    public required DbProviderFactory Factory { get; init; }

    /// <summary>Statement run once per connection before the procedure. Null when there is nothing to set.</summary>
    public string? SessionPrep { get; init; }

    /// <summary>
    /// Reads the server's own watermark, used to seed an incremental first sync without
    /// transferring history. Null when the platform has no server-side equivalent, in which
    /// case the procedure is asked to report its watermark instead (see <c>mode: "seed"</c>).
    /// </summary>
    public string? CurrentVersionSql { get; init; }

    /// <summary>Invokes the tracking procedure. <c>{0}</c> is the procedure name.</summary>
    public required string CallProcedure { get; init; }

    /// <summary>
    /// Connection string keys applied only when the user has not set them.
    /// Matching is on the literal key, so a user who writes a provider synonym
    /// (<c>App</c> for <c>Application Name</c>) gets the default applied as well; harmless,
    /// because the provider resolves the duplicate in the user's favour.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConnectionDefaults { get; init; }
        = new Dictionary<string, string>();

    public static readonly SqlDialect Mssql = new()
    {
        Name = "mssql",
        Factory = SqlClientFactory.Instance,
        // Large results come back as chunked NVARCHAR; the default TEXTSIZE truncates them.
        SessionPrep = "SET TEXTSIZE 2147483647; SET ANSI_WARNINGS OFF;",
        CurrentVersionSql = "SELECT CHANGE_TRACKING_CURRENT_VERSION()",
        CallProcedure = $"SET NOCOUNT ON; EXEC {{0}} @Json = @{JsonParameter};",
        ConnectionDefaults = new Dictionary<string, string>
        {
            ["Application Name"] = "Trignis",
            ["Packet Size"] = "32768",
            ["Connect Timeout"] = "30",
        },
    };

    public static readonly SqlDialect Postgres = new()
    {
        Name = "postgres",
        Factory = Npgsql.NpgsqlFactory.Instance,
        // No TEXTSIZE equivalent; a json/text return arrives whole.
        SessionPrep = null,
        // PostgreSQL has no server-side change tracking watermark. The function reports its own.
        CurrentVersionSql = null,
        // Must be a FUNCTION, not a PROCEDURE: Trignis needs a returned value.
        CallProcedure = $"SELECT {{0}}(@{JsonParameter}::json)",
        ConnectionDefaults = new Dictionary<string, string>
        {
            ["Application Name"] = "Trignis",
            ["Timeout"] = "30",
        },
    };

    private static readonly Dictionary<string, SqlDialect> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mssql"] = Mssql,
        ["sqlserver"] = Mssql,
        ["postgres"] = Postgres,
        ["postgresql"] = Postgres,
        ["pgsql"] = Postgres,
    };

    /// <summary>Provider names accepted by <see cref="Parse"/>, for error messages.</summary>
    public static string Supported => string.Join(", ", ByName.Keys);

    /// <summary>Every distinct dialect, so callers can hold all of them to the same contract.</summary>
    public static IReadOnlyCollection<SqlDialect> All { get; } = [.. ByName.Values.Distinct()];

    /// <summary>Every accepted name, including aliases.</summary>
    public static IReadOnlyCollection<string> Aliases => [.. ByName.Keys];

    /// <summary>Blank means mssql, so environments written before providers existed keep working.</summary>
    public static bool TryParse(string? name, out SqlDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            dialect = Mssql;
            return true;
        }

        return ByName.TryGetValue(name.Trim(), out dialect!);
    }

    public static SqlDialect Parse(string? name) => TryParse(name, out var dialect)
        ? dialect
        : throw new ArgumentException($"Unknown database provider '{name}'. Supported: {Supported}.", nameof(name));

    /// <summary>Opens a connection with <see cref="ConnectionDefaults"/> applied and <see cref="SessionPrep"/> run.</summary>
    public async System.Threading.Tasks.Task<DbConnection> OpenAsync(
        string connectionString, System.Threading.CancellationToken ct)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var (key, value) in ConnectionDefaults)
            if (!builder.ContainsKey(key))
                builder[key] = value;

        var conn = Factory.CreateConnection()
            ?? throw new InvalidOperationException($"Provider '{Name}' returned no connection.");

        try
        {
            conn.ConnectionString = builder.ConnectionString;
            await conn.OpenAsync(ct);

            if (SessionPrep is not null)
            {
                using var prep = conn.CreateCommand();
                prep.CommandText = SessionPrep;
                await prep.ExecuteNonQueryAsync(ct);
            }

            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }
}
