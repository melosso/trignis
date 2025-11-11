namespace Trignis.MicrosoftSQL.Models;

public class TrackingObject
{
    public required string Name { get; set; }
    public required string Database { get; set; }
    public required string TableName { get; set; }
    public required string StoredProcedureName { get; set; }
    public string? EnvironmentFile { get; set; }
    public string InitialSyncMode { get; set; } = "Incremental";
}
