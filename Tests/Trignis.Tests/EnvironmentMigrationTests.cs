using System;
using System.IO;
using Trignis.Helpers;
using Trignis.Models;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests;

public sealed class EnvironmentMigrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trignis-migrate").FullName;
    private string File(string name) => Path.Combine(_dir, name);
    private static string Encrypt(string text) => "PWENC:" + text.Length;
    private static bool IsEncrypted(string text) => text.StartsWith("PWENC:");

    [Fact]
    public void JsonFile_IsReplacedByToml_AndKeptAsBackup()
    {
        System.IO.File.WriteAllText(File("prod.json"), """{ "ConnectionStrings": { "Main": "Password=plain" }, "ChangeTracking": { "PollingIntervalSeconds": 15, "TrackingObjects": [ { "Name": "a", "Database": "Main", "TableName": "t", "StoredProcedureName": "p" } ] } }""");

        EnvironmentMigration.MigrateDirectory(_dir, new GlobalSettings(), Encrypt, IsEncrypted);

        Assert.False(System.IO.File.Exists(File("prod.json")));
        Assert.DoesNotContain("Password=plain", System.IO.File.ReadAllText(File("prod.json.migrated")));
        var model = YamlEnvironment.ToModel(System.IO.File.ReadAllText(File("prod.yaml")), "prod", text => text);
        Assert.Equal(15, model.ChangeTracking.PollingIntervalSeconds);
        Assert.Equal("a", model.ChangeTracking.TrackingObjects[0].Name);

        EnvironmentMigration.MigrateDirectory(_dir, new GlobalSettings(), Encrypt, IsEncrypted); // nothing left to migrate
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void ExistingToml_Wins_AndTheJsonFileIsLeftAlone()
    {
        System.IO.File.WriteAllText(File("prod.json"), """{ "ChangeTracking": { "PollingIntervalSeconds": 15 } }""");
        System.IO.File.WriteAllText(File("prod.yaml"), "polling:\n  interval: 30s\n");

        Assert.Equal(EnvironmentMigration.Outcome.YamlExists, EnvironmentMigration.MigrateFile(File("prod.json"), new GlobalSettings(), Encrypt, IsEncrypted));

        Assert.True(System.IO.File.Exists(File("prod.json")));
        Assert.Contains("30s", System.IO.File.ReadAllText(File("prod.yaml")));
    }

    [Fact]
    public void InvalidJson_IsNotMigrated()
    {
        System.IO.File.WriteAllText(File("broken.json"), "{ not json");

        Assert.Equal(EnvironmentMigration.Outcome.Failed, EnvironmentMigration.MigrateFile(File("broken.json"), new GlobalSettings(), Encrypt, IsEncrypted));

        Assert.True(System.IO.File.Exists(File("broken.json")));
        Assert.False(System.IO.File.Exists(File("broken.yaml")));
    }

    [Fact]
    public void ReadOnlyFolder_LeavesTheJsonFile_AndItLoadsInMemory()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // root ignores the mode
        System.IO.File.WriteAllText(File("prod.json"), """{ "ConnectionStrings": { "Main": "Server=db" }, "ChangeTracking": { "TrackingObjects": [ { "Name": "a", "Database": "Main", "TableName": "t", "StoredProcedureName": "p" } ] } }""");
        System.IO.File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(EnvironmentMigration.Outcome.ReadOnly, EnvironmentMigration.MigrateFile(File("prod.json"), new GlobalSettings(), Encrypt, IsEncrypted));
            Assert.True(System.IO.File.Exists(File("prod.json")));

            var service = new EnvironmentConfigService(Microsoft.Extensions.Logging.Abstractions.NullLogger<EnvironmentConfigService>.Instance, new EncryptionService(Directory.CreateTempSubdirectory("trignis-key").FullName));
            var env = service.LoadFile(File("prod.json"), out var error);
            Assert.Null(error);
            Assert.Equal("a", env!.ChangeTracking.TrackingObjects[0].Name);
        }
        finally
        {
            System.IO.File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void JsonThatFailsTheRoundTrip_IsNotMigrated()
    {
        // An unknown auth type cannot be written as YAML.
        System.IO.File.WriteAllText(File("odd.json"), """{ "ChangeTracking": { "ApiEndpoints": [ { "Key": "a", "Url": "http://x/", "Auth": { "Type": "Digest" } } ] } }""");

        Assert.Equal(EnvironmentMigration.Outcome.Failed, EnvironmentMigration.MigrateFile(File("odd.json"), new GlobalSettings { ExportToApi = true }, Encrypt, IsEncrypted));
        Assert.True(System.IO.File.Exists(File("odd.json")));
        Assert.False(System.IO.File.Exists(File("odd.yaml")));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
