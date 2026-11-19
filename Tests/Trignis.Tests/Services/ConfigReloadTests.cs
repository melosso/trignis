using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests.Services;

[Collection("SqliteTests")]
public sealed class ConfigReloadTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"reload-{Guid.NewGuid():N}")).FullName;
    private readonly string _savedWorkingDir = Environment.CurrentDirectory;

    // Config files resolve from the app directory, as in production.
    public ConfigReloadTests() => Environment.CurrentDirectory = AppContext.BaseDirectory;

    private static async Task WaitFor(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
        Assert.True(condition());
    }

    [Fact]
    public async Task RevertingARejectedEdit_ClearsTheLoadError()
    {
        var envDir = Directory.CreateDirectory(Path.Combine(_root, "environments")).FullName;
        var file = Path.Combine(envDir, "prod.json");
        const string valid = """{"ChangeTracking":{"TrackingObjects":[],"ApiEndpoints":[]}}""";
        File.WriteAllText(file, valid);

        using var service = new EnvironmentConfigService(NullLogger<EnvironmentConfigService>.Instance, new EncryptionService(_root));
        service.Initialize([service.LoadFile(file)!], envDir);
        service.StartWatching();

        // First save records the applied hash.
        File.WriteAllText(file, valid + " ");
        await Task.Delay(1500);
        File.WriteAllText(file, "{ broken");
        await WaitFor(() => service.LoadErrors.ContainsKey("prod"));

        File.WriteAllText(file, valid + " ");
        await WaitFor(() => !service.LoadErrors.ContainsKey("prod"));
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _savedWorkingDir;
        Directory.Delete(_root, recursive: true);
    }
}
