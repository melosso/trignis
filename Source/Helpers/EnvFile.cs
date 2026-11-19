using System;
using System.IO;

namespace Trignis.Helpers;

/// <summary>Loads <c>KEY=value</c> lines from a <c>.env</c> file into the process environment.</summary>
public static class EnvFile
{
    /// <summary>Sets unset variables. Skips blank lines and <c>#</c> comments.</summary>
    public static void Load(string path)
    {
        if (!File.Exists(path)) return;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..];

            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;

            var name = parts[0].Trim();
            var value = parts[1].Trim().Trim('"', '\'');
            if (name.Length > 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
