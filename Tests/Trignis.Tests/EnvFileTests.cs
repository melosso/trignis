using System;
using System.IO;
using Trignis.Helpers;
using Xunit;

namespace Trignis.Tests;

public class EnvFileTests
{
    [Fact]
    public void Load_SetsUnsetVariables_KeepsExistingOnes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        string fresh = $"TRIGNIS_TEST_FRESH_{suffix}", existing = $"TRIGNIS_TEST_EXISTING_{suffix}", exported = $"TRIGNIS_TEST_EXPORTED_{suffix}";
        var path = Path.Combine(Path.GetTempPath(), $"trignis-{suffix}.env");
        File.WriteAllLines(path, ["# comment", "", $"{fresh}=\"from-file\"", $"{existing}=from-file", $"export {exported}='quoted'", "not a pair"]);
        Environment.SetEnvironmentVariable(existing, "from-env");

        try
        {
            EnvFile.Load(path);

            Assert.Equal("from-file", Environment.GetEnvironmentVariable(fresh));
            Assert.Equal("from-env", Environment.GetEnvironmentVariable(existing));
            Assert.Equal("quoted", Environment.GetEnvironmentVariable(exported));
        }
        finally
        {
            foreach (var name in new[] { fresh, existing, exported }) Environment.SetEnvironmentVariable(name, null);
            File.Delete(path);
        }
    }
}
