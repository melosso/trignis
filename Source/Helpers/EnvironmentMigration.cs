using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Serilog;
using Trignis.Models;

namespace Trignis.Helpers;

/// <summary>
/// Converts <c>environments/*.json</c> to the YAML layout. shortcut: ships in v0.5 only, remove in v0.6 together with the *.json watcher filter.
/// </summary>
public static class EnvironmentMigration
{
    public enum Outcome
    {
        /// <summary>The YAML file was written and the JSON file replaced by its backup.</summary>
        Migrated,

        /// <summary>A YAML file with the same name exists; the JSON file is ignored.</summary>
        YamlExists,

        /// <summary>The folder is read-only; load the JSON file in memory with <see cref="FromJson"/>.</summary>
        ReadOnly,

        /// <summary>The file could not be converted; it stays and the environment is invalid.</summary>
        Failed,
    }

    private static int _announced;

    /// <summary>Migrates every JSON environment file in <paramref name="envDir"/>. Returns the outcome per JSON file.</summary>
    public static IReadOnlyDictionary<string, Outcome> MigrateDirectory(string envDir, GlobalSettings globals, Func<string, string> encrypt, Func<string, bool> isEncrypted)
    {
        if (!Directory.Exists(envDir)) return new Dictionary<string, Outcome>();
        return Directory.GetFiles(envDir, "*.json").Order(StringComparer.Ordinal)
            .ToDictionary(json => json, json => MigrateFile(json, globals, encrypt, isEncrypted));
    }

    /// <summary>Writes <c>name.yaml</c> and replaces the JSON file with <c>name.json.migrated</c>, secrets encrypted.</summary>
    public static Outcome MigrateFile(string jsonPath, GlobalSettings globals, Func<string, string> encrypt, Func<string, bool> isEncrypted)
    {
        var name = Path.GetFileNameWithoutExtension(jsonPath);
        var yamlPath = Path.ChangeExtension(jsonPath, ".yaml");

        if (File.Exists(yamlPath))
        {
            Log.Warning("Environment '{Name}': {Yaml} exists, so {Json} is ignored. Delete or rename {Json}", name, Path.GetFileName(yamlPath), Path.GetFileName(jsonPath));
            return Outcome.YamlExists;
        }

        if (Interlocked.Exchange(ref _announced, 1) == 0)
            Log.Warning("Migrating environments to new format");

        string json, yaml;
        try
        {
            json = File.ReadAllText(jsonPath);
            yaml = Convert(json, name, globals, out var expected);

            // The YAML must load back to the same environment, or the JSON file stays.
            var loaded = YamlEnvironment.ToModel(yaml, name, text => text);
            if (YamlEnvironment.Write(loaded) != YamlEnvironment.Write(expected))
                throw new InvalidOperationException("the YAML file does not load back to the same environment");
        }
        catch (Exception ex)
        {
            Log.Error("Environment '{Name}': {Json} was not migrated: {Error}", name, Path.GetFileName(jsonPath), ex.Message);
            return Outcome.Failed;
        }

        var temp = yamlPath + ".tmp";
        try
        {
            File.WriteAllText(temp, yaml);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Warning("Environment '{Name}': the environments folder is not writable, so {Json} is loaded without migration. Convert it to {Yaml}: v0.6 does not read JSON files",
                name, Path.GetFileName(jsonPath), Path.GetFileName(yamlPath));
            return Outcome.ReadOnly;
        }
        File.Move(temp, yamlPath, overwrite: false);

        var backup = jsonPath + ".migrated";
        if (File.Exists(backup)) backup = $"{jsonPath}.{DateTime.UtcNow:yyyyMMddHHmmss}.migrated";
        // No plaintext secrets in the backup; v0.4 decrypts PWENC values.
        File.WriteAllText(backup, EncryptJson(json, YamlEnvironment.Secrets(yaml).Where(s => !isEncrypted(s)).ToHashSet(StringComparer.Ordinal), encrypt));
        File.Delete(jsonPath);

        Log.Warning("Environment '{Name}': migrated to {Yaml}. The original is stored as {Backup}", name, Path.GetFileName(yamlPath), Path.GetFileName(backup));
        return Outcome.Migrated;
    }

    /// <summary>Converts a v0.4 JSON environment file to the YAML layout, applying the global export defaults.</summary>
    public static string FromJson(string json, GlobalSettings globals) => Convert(json, "environment", globals, out _);

    private static string Convert(string json, string name, GlobalSettings globals, out EnvironmentConfig enabledOnly)
    {
        var model = Bind(json, name);
        var ct = model.ChangeTracking;

        // v0.4 fell back to appsettings.json; the YAML layout states every destination.
        var fileOn = ct.ExportToFile ?? globals.ExportToFile;
        var apiOn = ct.ExportToApi ?? globals.ExportToApi;
        var path = ct.FilePath ?? (fileOn ? globals.FilePath : null);

        var resolved = model with { ChangeTracking = ct with { ExportToFile = fileOn, FilePath = path, ExportToApi = apiOn } };
        enabledOnly = model with
        {
            ChangeTracking = ct with
            {
                ExportToFile = fileOn,
                FilePath = fileOn ? path : null,
                ApiEndpoints = apiOn ? ct.ApiEndpoints : [],
            },
        };
        return YamlEnvironment.Write(resolved, fileEnabled: fileOn, endpointsEnabled: apiOn);
    }

    // The v0.4 loader: configuration binding of the JSON file.
    private static EnvironmentConfig Bind(string json, string name)
    {
        var cfg = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var ct = cfg.GetSection("ChangeTracking");
        return new EnvironmentConfig
        {
            Name = name,
            Provider = cfg.GetValue<string?>("Provider") ?? "mssql",
            ConnectionStrings = cfg.GetSection("ConnectionStrings").GetChildren()
                .Where(c => !string.IsNullOrEmpty(c.Value)).ToDictionary(c => c.Key, c => c.Value!),
            ChangeTracking = new EnvironmentChangeTracking
            {
                TrackingObjects = ct.GetSection("TrackingObjects").Get<TrackingObject[]>() ?? [],
                ApiEndpoints = ct.GetSection("ApiEndpoints").Get<ApiEndpoint[]>() ?? [],
                PollingIntervalSeconds = ct.GetValue<int?>("PollingIntervalSeconds"),
                ExportToFile = ct.GetValue<bool?>("ExportToFile"),
                FilePath = ct.GetValue<string?>("FilePath"),
                ExportToApi = ct.GetValue<bool?>("ExportToApi"),
                RetryCount = ct.GetValue<int?>("RetryCount"),
                RetryDelaySeconds = ct.GetValue<int?>("RetryDelaySeconds"),
            },
        };
    }

    private static string EncryptJson(string json, IReadOnlySet<string> plain, Func<string, string> encrypt)
    {
        var root = JsonNode.Parse(json)!;
        Visit(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj.ToList())
                        if (child is JsonValue v && v.TryGetValue(out string? text) && plain.Contains(text)) obj[key] = encrypt(text);
                        else Visit(child);
                    break;
                case JsonArray arr:
                    foreach (var child in arr) Visit(child);
                    break;
            }
        }
    }
}
