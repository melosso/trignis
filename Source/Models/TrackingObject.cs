namespace Trignis.MicrosoftSQL.Models;

public record class TrackingObject
{
    public required string Name { get; init; }
    public required string Database { get; init; }
    public required string TableName { get; init; }
    public required string StoredProcedureName { get; init; }
    public string? EnvironmentFile { get; init; }
    public string InitialSyncMode { get; init; } = "Incremental";
}
