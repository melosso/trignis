using System;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Trignis.MicrosoftSQL.Services;

public class DeadLetterService
{
    private readonly ILogger<DeadLetterService> _logger;
    private readonly IConfiguration _config;
    private readonly string _sinkholeConnectionString;
    private readonly int _retentionDays;

    public DeadLetterService(
        ILogger<DeadLetterService> logger,
        IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _sinkholeConnectionString = "Data Source=sinkhole.db";
        _retentionDays = _config.GetValue<int>("ChangeTracking:DeadletterRetentionDays", 60);
    }

    public async Task InitializeAsync()
    {
        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync();
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
        await command.ExecuteNonQueryAsync();
        _logger.LogDebug("Dead letter database initialized");
    }

    public async Task SaveDeadLetterAsync(string trackingObjectName, string databaseName, JsonElement data, Exception exception)
    {
        var dataJson = JsonSerializer.Serialize(data);
        var dataHash = ComputeSha256Hash(dataJson);
        var sourceKey = $"{trackingObjectName}_{databaseName}";

        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync();

        // Check if this exact message was already saved
        var checkCommand = conn.CreateCommand();
        checkCommand.CommandText = "SELECT COUNT(*) FROM DeadLetters WHERE SourceKey = @sourceKey AND DataHash = @dataHash";
        checkCommand.Parameters.AddWithValue("@sourceKey", sourceKey);
        checkCommand.Parameters.AddWithValue("@dataHash", dataHash);
        var result = await checkCommand.ExecuteScalarAsync();
        var count = (result != null && result != DBNull.Value) ? Convert.ToInt64(result) : 0;

        if (count > 0)
        {
            _logger.LogDebug($"Dead letter already exists for {sourceKey} with hash {dataHash}, skipping duplicate");
            return;
        }

        // Insert new dead letter
        var insertCommand = conn.CreateCommand();
        insertCommand.CommandText = @"
            INSERT INTO DeadLetters (SourceKey, TrackingObjectName, DatabaseName, DataHash, Data, ErrorMessage)
            VALUES (@sourceKey, @trackingObjectName, @databaseName, @dataHash, @data, @errorMessage)
        ";
        insertCommand.Parameters.AddWithValue("@sourceKey", sourceKey);
        insertCommand.Parameters.AddWithValue("@trackingObjectName", trackingObjectName);
        insertCommand.Parameters.AddWithValue("@databaseName", databaseName);
        insertCommand.Parameters.AddWithValue("@dataHash", dataHash);
        insertCommand.Parameters.AddWithValue("@data", dataJson);
        insertCommand.Parameters.AddWithValue("@errorMessage", exception.Message);

        await insertCommand.ExecuteNonQueryAsync();
        _logger.LogWarning($"Saved dead letter for {trackingObjectName} ({databaseName}): {exception.Message}");
    }

    public async Task PurgeOldDeadLettersAsync()
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-_retentionDays);

        using var conn = new SqliteConnection(_sinkholeConnectionString);
        await conn.OpenAsync();
        var command = conn.CreateCommand();
        command.CommandText = "DELETE FROM DeadLetters WHERE Timestamp < @cutoffDate";
        command.Parameters.AddWithValue("@cutoffDate", cutoffDate);

        var deletedCount = await command.ExecuteNonQueryAsync();
        if (deletedCount > 0)
        {
            _logger.LogInformation($"Purged {deletedCount} dead letters older than {_retentionDays} days");
        }
    }

    private static string ComputeSha256Hash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}