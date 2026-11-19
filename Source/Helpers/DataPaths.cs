using System.IO;
using Microsoft.Extensions.Configuration;

namespace Trignis.Helpers;

/// <summary>
/// Files that must survive an upgrade: state.db, sinkhole.db and the .core key folder. They live in <c>ChangeTracking:DataPath</c>, by default the working directory. A relative <c>StateDbPath</c> resolves inside it.
/// </summary>
public static class DataPaths
{
    private const string DataPathKey = "ChangeTracking:DataPath";

    public static string State(IConfiguration config) =>
        Path.Combine(Directory(config), config["ChangeTracking:StateDbPath"] ?? "state.db");

    public static string Sinkhole(IConfiguration config) => Path.Combine(Directory(config), "sinkhole.db");

    /// <summary>The key folder, or null when no data path is set: <see cref="Services.EncryptionService"/> then keeps its own default.</summary>
    public static string? Core(IConfiguration config) =>
        string.IsNullOrWhiteSpace(config[DataPathKey]) ? null : Path.Combine(Directory(config), ".core");

    private static string Directory(IConfiguration config) => config[DataPathKey] ?? "";
}
