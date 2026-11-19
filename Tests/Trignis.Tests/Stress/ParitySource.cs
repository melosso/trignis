using System.Collections.Generic;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Trignis.Data;
using Xunit;

namespace Trignis.Tests.Stress;

/// <summary>
/// Runs if the parity containers are up (Tests/parity/docker-compose.yml) and their connection strings are exported. Skipped otherwise.
/// </summary>
public sealed class ParityTheoryAttribute : TheoryAttribute
{
    public ParityTheoryAttribute()
    {
        if (ParitySource.ConnectionString("mssql") is null || ParitySource.ConnectionString("postgres") is null)
            Skip = "Set TRIGNIS_PARITY_MSSQL and TRIGNIS_PARITY_POSTGRES (see Tests/parity/run.sh)";
    }
}

/// <summary>
/// A tracked table and its change procedure on a database server, one per test. Contract: {"Metadata":{"Sync":{"Version":n}},"Data":[{..,"$version":n}]}.
/// </summary>
internal sealed class ParitySource : IAsyncDisposable
{
    public static string? ConnectionString(string provider) =>
        Environment.GetEnvironmentVariable(provider == "mssql" ? "TRIGNIS_PARITY_MSSQL" : "TRIGNIS_PARITY_POSTGRES");

    private readonly SqlDialect _dialect;
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];

    public string Provider { get; }
    public bool Transactional { get; private init; }
    public string Connection { get; private set; }
    public string Table => Provider == "mssql" ? $"dbo.items_{_suffix}" : $"public.items_{_suffix}";
    public string Procedure => Provider == "mssql" ? $"dbo.changes_{_suffix}" : $"public.changes_{_suffix}";

    private ParitySource(string provider)
    {
        Provider = provider;
        _dialect = SqlDialect.Parse(provider);
        Connection = ConnectionString(provider)!;
    }

    /// <param name="transactional">Versions that stay correct under concurrent transactions: SNAPSHOT reads on SQL Server, transaction ids on PostgreSQL. Tracks updates on both.</param>
    public static async Task<ParitySource> CreateAsync(string provider, bool transactional = false)
    {
        var source = new ParitySource(provider) { Transactional = transactional };

        if (provider == "mssql")
        {
            // Change tracking is per database, so the tests get their own
            await source.ExecuteAsync("""
                if db_id('trignis_parity') is null create database trignis_parity;
                if not exists (select 1 from sys.change_tracking_databases where database_id = db_id('trignis_parity'))
                    alter database trignis_parity set change_tracking = on (change_retention = 2 days, auto_cleanup = on);
                """);
            source.Connection += ";Database=trignis_parity";
            if (transactional)
                await source.ExecuteAsync("alter database trignis_parity set allow_snapshot_isolation on");

            await source.ExecuteAsync($"""
                create table {source.Table} (id bigint identity primary key, val nvarchar(100) not null);
                alter table {source.Table} enable change_tracking;
                """);
            await source.ExecuteAsync($"""
                create procedure {source.Procedure} @Json nvarchar(max) as
                begin
                    set nocount on;
                    {(transactional ? "set transaction isolation level snapshot; begin tran;" : "")}
                    declare @from bigint = json_value(@Json, '$.fromVersion');
                    declare @current bigint = change_tracking_current_version();
                    select (
                        select @current as 'Metadata.Sync.Version',
                               (select t.id, t.val, ct.SYS_CHANGE_VERSION as [$version]
                                  from changetable(changes {source.Table}, @from) ct
                                  join {source.Table} t on t.id = ct.id
                                 where ct.SYS_CHANGE_VERSION <= @current
                                   for json path) as Data
                           for json path, without_array_wrapper);
                    {(transactional ? "commit;" : "")}
                end
                """);
        }
        else if (transactional)
        {
            // Rows carry the writing transaction's id; everything below the snapshot xmin is committed or gone.
            await source.ExecuteAsync($"""
                create table {source.Table} (id bigserial primary key, val text not null, xv xid8 not null default pg_current_xact_id());
                create function {source.Procedure}_touch() returns trigger language plpgsql as $$
                begin new.xv := pg_current_xact_id(); return new; end $$;
                create trigger touch before update on {source.Table} for each row execute function {source.Procedure}_touch();
                """);
            await source.ExecuteAsync($"""
                create function {source.Procedure}(payload json) returns json language plpgsql as $$
                declare
                    from_version bigint := coalesce((payload ->> 'fromVersion')::bigint, 0);
                    safe bigint := pg_snapshot_xmin(pg_current_snapshot())::text::bigint;
                begin
                    if coalesce(payload ->> 'mode', 'sync') = 'seed' then
                        return json_build_object('Metadata', json_build_object('Sync', json_build_object('Version', safe - 1)));
                    end if;
                    return json_build_object(
                        'Metadata', json_build_object('Sync', json_build_object('Version', safe - 1)),
                        'Data', (select coalesce(json_agg(json_build_object('id', o.id, 'val', o.val, '$version', o.xv::text::bigint) order by o.xv), '[]'::json)
                                   from {source.Table} o where o.xv::text::bigint > from_version and o.xv::text::bigint < safe));
                end $$
                """);
        }
        else
        {
            await source.ExecuteAsync($"create table {source.Table} (id bigserial primary key, val text not null)");
            await source.ExecuteAsync($"""
                create function {source.Procedure}(payload json) returns json language plpgsql as $$
                declare
                    from_version bigint := coalesce((payload ->> 'fromVersion')::bigint, 0);
                    seeding boolean := coalesce(payload ->> 'mode', 'sync') = 'seed';
                    max_version bigint;
                begin
                    select coalesce(max(id), from_version) into max_version from {source.Table};
                    if seeding then
                        return json_build_object('Metadata', json_build_object('Sync', json_build_object('Version', max_version)));
                    end if;
                    return json_build_object(
                        'Metadata', json_build_object('Sync', json_build_object('Version', max_version)),
                        'Data', (select coalesce(json_agg(json_build_object('id', o.id, 'val', o.val, '$version', o.id) order by o.id), '[]'::json)
                                   from {source.Table} o where o.id > from_version and o.id <= max_version));
                end $$
                """);
        }

        return source;
    }

    /// <summary>Inserts <paramref name="count"/> rows in one transaction and returns nothing; ids are 1..n in insert order.</summary>
    public async Task InsertAsync(int count, CancellationToken ct = default)
    {
        await using var conn = await _dialect.OpenAsync(Connection, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        for (var i = 0; i < count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"insert into {Table} (val) values ('v')";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Inserts <paramref name="count"/> rows in one statement.</summary>
    public Task BulkInsertAsync(int count) => ExecuteAsync(Provider == "mssql"
        ? $"insert into {Table} (val) select top ({count}) 'v' from sys.all_objects a cross join sys.all_objects b"
        : $"insert into {Table} (val) select 'v' from generate_series(1, {count})");

    /// <summary>Updates up to <paramref name="count"/> existing rows. Tracked on PostgreSQL only by a transactional source.</summary>
    public Task BulkUpdateAsync(int count, string value) => ExecuteAsync(Provider == "mssql"
        ? $"update top ({count}) {Table} set val = '{value}' where val <> '{value}'"
        : $"update {Table} set val = '{value}' where id in (select id from {Table} where val <> '{value}' limit {count})");

    /// <summary>
    /// Returns the newest version the procedure can return: the database-wide change tracking version on SQL Server, the highest row id on PostgreSQL.
    /// </summary>
    public async Task<long> CurrentVersionAsync()
    {
        await using var conn = await _dialect.OpenAsync(Connection, CancellationToken.None);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = Provider == "mssql"
            ? "select change_tracking_current_version()"
            : Transactional ? $"select coalesce(max(xv::text::bigint), 0) from {Table}" : $"select coalesce(max(id), 0) from {Table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Current value of every row.</summary>
    public async Task<Dictionary<long, string>> ValuesAsync()
    {
        await using var conn = await _dialect.OpenAsync(Connection, CancellationToken.None);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"select id, val from {Table}";
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new Dictionary<long, string>();
        while (await reader.ReadAsync()) result[reader.GetInt64(0)] = reader.GetString(1);
        return result;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var conn = await _dialect.OpenAsync(Connection, CancellationToken.None);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var routine = Provider == "mssql" ? "procedure" : "function";
        try
        {
            await ExecuteAsync($"drop {routine} if exists {Procedure}");
            await ExecuteAsync($"drop table if exists {Table}");
            if (Provider != "mssql") await ExecuteAsync($"drop function if exists {Procedure}_touch()");
        }
        catch (Exception)
        {
            // Best effort: the containers are thrown away after the run anyway
        }
    }
}
