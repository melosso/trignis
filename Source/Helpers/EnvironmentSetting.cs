using System;
using System.Collections.Generic;
using System.Linq;
using Trignis.Models;

namespace Trignis.Helpers;

/// <summary>
/// Environment variable names accepted per setting, as in Beacon: <c>Health__Port</c>, <c>TRIGNIS_HEALTH_PORT</c>, <c>HEALTH_PORT</c>, <c>HealthPort</c>. The first non-empty one wins.
/// </summary>
public static class EnvironmentSetting
{
    /// <summary>Configuration path and the name its aliases derive from. Global settings follow their property names.</summary>
    public static readonly IReadOnlyList<(string Path, string Name)> Settings =
    [
        ("Trignis:AdminApiKey", "AdminApiKey"),
        ("Trignis:EncryptionKey", "EncryptionKey"),
        ("SelectedEnvironment", "SelectedEnvironment"),
        ("Health:Enabled", "HealthEnabled"),
        ("Health:Port", "HealthPort"),
        ("Health:Host", "HealthHost"),
        ("Health:CacheDurationSeconds", "HealthCacheDurationSeconds"),
        ("WebHost:Enabled", "WebHostEnabled"),
        ("WebHost:Host", "WebHostHost"),
        ("Windows:UseEventLog", "UseEventLog"),
        ("ChangeTracking:StateDbPath", "StateDbPath"),
        ("ChangeTracking:DataPath", "DataPath"),
        .. typeof(GlobalSettings).GetProperties().Select(p => ($"ChangeTracking:GlobalSettings:{p.Name}", p.Name)),
    ];

    // Names the .NET host reads itself; an unprefixed alias must not claim them.
    private static readonly HashSet<string> HostReserved = new(StringComparer.OrdinalIgnoreCase) { "URLS", "PORT", "HTTP_PORTS", "HTTPS_PORTS" };

    /// <summary>Names for a setting in precedence order. Unprefixed names the .NET host reads are omitted.</summary>
    public static IReadOnlyList<string> Names(string path, string name)
    {
        var snake = ToScreamingSnake(name);
        var names = new List<string>(4) { path.Replace(":", "__"), $"TRIGNIS_{snake}" };
        if (!IsHostReserved(snake)) names.Add(snake);
        if (!IsHostReserved(name)) names.Add(name);
        return names;
    }

    /// <summary>First non-empty value among the names of the setting called <paramref name="name"/>, or null.</summary>
    public static string? Resolve(string name, Func<string, string?>? lookup = null)
    {
        var (path, _) = Settings.First(s => s.Name == name);
        return Names(path, name).Select(lookup ?? Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }

    /// <summary>Configuration entries for every setting set under any of its names; add it after the other sources.</summary>
    public static Dictionary<string, string?> Aliases(Func<string, string?>? lookup = null) =>
        Settings.Select(s => (s.Path, Value: Resolve(s.Name, lookup)))
            .Where(s => s.Value is not null)
            .ToDictionary(s => s.Path, s => s.Value);

    private static bool IsHostReserved(string name) =>
        HostReserved.Contains(name)
        || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase);

    // "StateDbPath" -> "STATE_DB_PATH"
    private static string ToScreamingSnake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + c : c.ToString())).ToUpperInvariant();
}
