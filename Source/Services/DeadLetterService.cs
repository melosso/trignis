using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trignis.Models;

namespace Trignis.Services;

public class DeadLetterService
{
    private readonly ILogger<DeadLetterService> _logger;
    private readonly string _sinkholeConnectionString;
    private readonly int _retentionDays;

    public DeadLetterService(
        ILogger<DeadLetterService> logger,
        IOptions<GlobalSettings> globalSettings)
    {
        _logger = logger;
        _sinkholeConnectionString = "Data Source=sinkhole.db";
        _retentionDays = globalSettings.Value.DeadletterRetentionDays;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = conn.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS DeadLetters (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceKey TEXT NOT NULL,
                TrackingObjectName TEXT NOT NULL,
                DatabaseName TEXT NOT NULL,
                DataHash TEXT NOT NULL,
                Data TEXT NOT NULL,
                ErrorMessage TEXT NOT NULL,
                Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(SourceKey, DataHash)
            );
            CREATE INDEX IF NOT EXISTS idx_timestamp ON DeadLetters(Timestamp);
        ";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Replay needs to know which environment to resend to. Older rows folded the environment
        // into SourceKey/TrackingObjectName, where an underscore in a name makes it unrecoverable,
        // those stay null and are not replayable.
        var columnExists = conn.CreateCommand();
        columnExists.CommandText = "SELECT COUNT(*) FROM pragma_table_info('DeadLetters') WHERE name = 'EnvironmentName'";
        if (Convert.ToInt64(await columnExists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
        {
            var addColumn = conn.CreateCommand();
            addColumn.CommandText = "ALTER TABLE DeadLetters ADD COLUMN EnvironmentName TEXT";
            await addColumn.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Added EnvironmentName to the dead letter table; existing rows cannot be replayed");
        }

        // Automatic replay state. Existing rows start at zero attempts and are due immediately,
        // so upgrading picks up whatever is already queued.
        await AddColumnIfMissingAsync(conn, "Attempts", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, "NextAttempt", "DATETIME", cancellationToken).ConfigureAwait(false);

        // Which destinations refused the payload, as a JSON array. Null on older rows, which
        // replay to every destination as they always did.
        await AddColumnIfMissingAsync(conn, "FailedTargets", "TEXT", cancellationToken).ConfigureAwait(false);

        var index = conn.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS idx_next_attempt ON DeadLetters(NextAttempt)";
        await index.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Dead letter database initialized");
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection conn, string name, string definition, CancellationToken cancellationToken)
    {
        var exists = conn.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM pragma_table_info('DeadLetters') WHERE name = @name";
        exists.Parameters.AddWithValue("@name", name);
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
            return;

        var add = conn.CreateCommand();
        add.CommandText = $"ALTER TABLE DeadLetters ADD COLUMN {name} {definition}";
        await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveDeadLetterAsync(string environmentName, string objectName, string databaseName, JsonElement data, Exception exception, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? failedTargets = null)
    {
        var dataJson = JsonSerializer.Serialize(data);
        var dataHash = ComputeSha256Hash(dataJson);

        // Kept in the original shape so existing rows and the UI's filters stay consistent
        var trackingObjectName = $"{environmentName}_{objectName}";
        var sourceKey = $"{trackingObjectName}_{databaseName}";

        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var tx = conn.BeginTransaction();
        try
        {
            // Check if this exact message was already saved
            var checkCommand = conn.CreateCommand();
            checkCommand.Transaction = tx;
            checkCommand.CommandText = "SELECT Id, FailedTargets FROM DeadLetters WHERE SourceKey = @sourceKey AND DataHash = @dataHash";
            checkCommand.Parameters.AddWithValue("@sourceKey", sourceKey);
            checkCommand.Parameters.AddWithValue("@dataHash", dataHash);

            long? existingId = null;
            string? existingTargets = null;
            using (var existing = await checkCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await existing.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existingId = existing.GetInt64(0);
                    existingTargets = existing.IsDBNull(1) ? null : existing.GetString(1);
                }
            }

            if (existingId is { } id)
            {
                // Same payload, possibly a different destination this time. A null on the row
                // already means "every destination", so there is nothing to widen.
                if (existingTargets != null && failedTargets is { Count: > 0 })
                {
                    var merge = conn.CreateCommand();
                    merge.Transaction = tx;
                    merge.CommandText = "UPDATE DeadLetters SET FailedTargets = @targets WHERE Id = @id";
                    merge.Parameters.AddWithValue("@id", id);
                    merge.Parameters.AddWithValue("@targets",
                        JsonSerializer.Serialize(ParseTargets(existingTargets)!.Union(failedTargets, StringComparer.Ordinal)));
                    await merge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                _logger.LogDebug($"Dead letter already exists for {sourceKey} with hash {dataHash}, skipping duplicate");
                return;
            }

            // Insert new dead letter
            var insertCommand = conn.CreateCommand();
            insertCommand.Transaction = tx;
            insertCommand.CommandText = @"
                INSERT INTO DeadLetters (SourceKey, TrackingObjectName, EnvironmentName, DatabaseName, DataHash, Data, ErrorMessage, FailedTargets)
                VALUES (@sourceKey, @trackingObjectName, @environmentName, @databaseName, @dataHash, @data, @errorMessage, @failedTargets)
            ";
            insertCommand.Parameters.AddWithValue("@sourceKey", sourceKey);
            insertCommand.Parameters.AddWithValue("@trackingObjectName", trackingObjectName);
            insertCommand.Parameters.AddWithValue("@environmentName", environmentName);
            insertCommand.Parameters.AddWithValue("@databaseName", databaseName);
            insertCommand.Parameters.AddWithValue("@dataHash", dataHash);
            insertCommand.Parameters.AddWithValue("@data", dataJson);
            insertCommand.Parameters.AddWithValue("@errorMessage", exception.Message);
            insertCommand.Parameters.AddWithValue("@failedTargets",
                failedTargets is { Count: > 0 } ? JsonSerializer.Serialize(failedTargets) : DBNull.Value);

            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning($"Saved dead letter for {objectName} in {environmentName} ({databaseName}): {exception.Message}");
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task PurgeOldDeadLettersAsync(CancellationToken cancellationToken = default)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-_retentionDays);

        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = conn.CreateCommand();
        command.CommandText = "DELETE FROM DeadLetters WHERE Timestamp < @cutoffDate";
        command.Parameters.AddWithValue("@cutoffDate", cutoffDate);

        var deletedCount = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (deletedCount > 0)
        {
            _logger.LogInformation($"Purged {deletedCount} dead letters older than {_retentionDays} days");
        }
    }

    /// <summary>Everything replay needs. Null EnvironmentName means the row predates the column.</summary>
    public sealed record DeadLetterRecord(long Id, string? EnvironmentName, string ObjectName, string DatabaseName, string Data, int Attempts,
        IReadOnlyList<string>? FailedTargets = null);

    /// <summary>Null (replay everywhere) for a missing or unreadable value: resending is safer than dropping.</summary>
    internal static List<string>? ParseTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<string>>(json); }
        catch (JsonException) { return null; }
    }

    public async Task<DeadLetterRecord?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = "SELECT Id, EnvironmentName, TrackingObjectName, DatabaseName, Data, Attempts, FailedTargets FROM DeadLetters WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return ReadRecord(reader);
    }

    /// <summary>Expects the column order of the SELECT in <see cref="GetAsync"/>.</summary>
    private static DeadLetterRecord ReadRecord(SqliteDataReader reader)
    {
        var environmentName = reader.IsDBNull(1) ? null : reader.GetString(1);
        var trackingObjectName = reader.GetString(2);

        // TrackingObjectName is "{environment}_{object}"; knowing the environment makes the
        // split unambiguous even when either name contains an underscore.
        var objectName = environmentName != null && trackingObjectName.StartsWith(environmentName + "_", StringComparison.Ordinal)
            ? trackingObjectName[(environmentName.Length + 1)..]
            : trackingObjectName;

        return new DeadLetterRecord(reader.GetInt64(0), environmentName, objectName, reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
            ParseTargets(reader.IsDBNull(6) ? null : reader.GetString(6)));
    }

    /// <summary>
    /// Rows whose backoff has elapsed and that have attempts left, oldest first.
    /// A row that exhausts <paramref name="maxAttempts"/> stops being returned and waits
    /// for a human to replay or discard it from the dashboard.
    /// </summary>
    public async Task<IReadOnlyList<DeadLetterRecord>> GetDueForReplayAsync(int maxAttempts, int limit, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = @"
            SELECT Id, EnvironmentName, TrackingObjectName, DatabaseName, Data, Attempts, FailedTargets
            FROM DeadLetters
            WHERE Attempts < @maxAttempts
              AND (NextAttempt IS NULL OR NextAttempt <= @now)
              AND EnvironmentName IS NOT NULL
            ORDER BY Timestamp
            LIMIT @limit
        ";
        command.Parameters.AddWithValue("@maxAttempts", maxAttempts);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow);
        command.Parameters.AddWithValue("@limit", limit);

        var records = new List<DeadLetterRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            records.Add(ReadRecord(reader));

        return records;
    }

    /// <summary>Records a failed replay: one more attempt, a later due time, and the newest error.</summary>
    public async Task RecordReplayFailureAsync(long id, string error, DateTime nextAttempt, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = @"
            UPDATE DeadLetters
            SET Attempts = Attempts + 1, NextAttempt = @nextAttempt, ErrorMessage = @error
            WHERE Id = @id
        ";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@error", error);
        command.Parameters.AddWithValue("@nextAttempt", nextAttempt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Narrows a row to the destinations still refusing it, so a retry leaves the healthy ones alone.</summary>
    public async Task SetFailedTargetsAsync(long id, IEnumerable<string> targets, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = "UPDATE DeadLetters SET FailedTargets = @targets WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@targets", JsonSerializer.Serialize(targets));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Postpones a row without spending an attempt, for a replay that was never tried.</summary>
    public async Task DeferAsync(long id, DateTime nextAttempt, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = "UPDATE DeadLetters SET NextAttempt = @nextAttempt WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@nextAttempt", nextAttempt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts a row back in the automatic rotation, used when a manual replay fails.</summary>
    public async Task ResetAttemptsAsync(long id, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = "UPDATE DeadLetters SET Attempts = 0, NextAttempt = NULL WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = conn.CreateCommand();
        command.CommandText = "DELETE FROM DeadLetters WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Deletes every row matching the same filters the list view uses.</summary>
    public async Task<int> PurgeAsync(string? search, string? objectFilter, CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(search))
            conditions.Add("(TrackingObjectName LIKE @search OR ErrorMessage LIKE @search OR DatabaseName LIKE @search)");
        if (!string.IsNullOrEmpty(objectFilter))
            conditions.Add("TrackingObjectName = @objectFilter");

        var command = conn.CreateCommand();
        command.CommandText = "DELETE FROM DeadLetters" + (conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "");
        if (!string.IsNullOrEmpty(search)) command.Parameters.AddWithValue("@search", $"%{search}%");
        if (!string.IsNullOrEmpty(objectFilter)) command.Parameters.AddWithValue("@objectFilter", objectFilter);

        var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Purged {Count} dead letter(s) from the web UI", deleted);
        return deleted;
    }

    private static string ComputeSha256Hash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}