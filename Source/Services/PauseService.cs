using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Trignis.Services;

/// <summary>
/// Which environments and tracking objects are currently held. Deliberately not part of the
/// environment files: those are the operator's declared intent, and a pause is an operational
/// override that has to survive a config redeploy and never rewrite a file under version control.
/// Stored alongside the watermarks in state.db, so losing one loses the other and the pair stays
/// consistent.
/// </summary>
public sealed class PauseService
{
    private readonly string _stateConnectionString;

    public PauseService(IConfiguration config)
    {
        var stateDbPath = config.GetValue<string>("ChangeTracking:StateDbPath", "state.db");
        _stateConnectionString = $"Data Source={stateDbPath}";
    }

    /// <summary>Holds every object in the environment, including ones added while it is paused.</summary>
    public static string EnvironmentScope(string environmentName) =>
        $"env:{environmentName.ToLowerInvariant()}";

    public static string ObjectScope(string environmentName, string objectName) =>
        $"obj:{environmentName.ToLowerInvariant()}/{objectName.ToLowerInvariant()}";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Pauses (
                Scope TEXT PRIMARY KEY,
                Reason TEXT,
                PausedBy TEXT,
                PausedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );
        ";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every held scope, read once per cycle so a paused object costs one query rather than one
    /// per object. Small enough that caching it would be premature.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetPausedScopesAsync(CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = "SELECT Scope FROM Pauses";

        var scopes = new HashSet<string>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            scopes.Add(reader.GetString(0));

        return scopes;
    }

    public sealed record PauseRecord(string Scope, string? Reason, string? PausedBy, DateTime PausedAt);

    public async Task<IReadOnlyList<PauseRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = "SELECT Scope, Reason, PausedBy, PausedAt FROM Pauses ORDER BY PausedAt DESC";

        var records = new List<PauseRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new PauseRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDateTime(3)));
        }

        return records;
    }

    /// <summary>Re-pausing an already paused scope refreshes the reason rather than failing.</summary>
    public async Task PauseAsync(string scope, string? reason, string? pausedBy, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = @"
            INSERT INTO Pauses (Scope, Reason, PausedBy, PausedAt)
            VALUES (@scope, @reason, @pausedBy, CURRENT_TIMESTAMP)
            ON CONFLICT(Scope) DO UPDATE SET
                Reason = @reason,
                PausedBy = @pausedBy,
                PausedAt = CURRENT_TIMESTAMP
        ";
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("@pausedBy", (object?)pausedBy ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>False when the scope was not paused, so the caller can report a no-op honestly.</summary>
    public async Task<bool> ResumeAsync(string scope, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_stateConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = "DELETE FROM Pauses WHERE Scope = @scope";
        command.Parameters.AddWithValue("@scope", scope);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
