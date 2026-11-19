using System.Collections.Generic;
using Trignis.Helpers;
using Xunit;

namespace Trignis.Tests;

public sealed class EnvironmentSettingTests
{
    [Fact]
    public void Names_AreCanonicalPrefixedSnakeAndBare_InPrecedenceOrder()
    {
        Assert.Equal(["Health__Port", "TRIGNIS_HEALTH_PORT", "HEALTH_PORT", "HealthPort"], EnvironmentSetting.Names("Health:Port", "HealthPort"));
        Assert.Equal(["Trignis__EncryptionKey", "TRIGNIS_ENCRYPTION_KEY", "ENCRYPTION_KEY", "EncryptionKey"], EnvironmentSetting.Names("Trignis:EncryptionKey", "EncryptionKey"));
    }

    [Fact]
    public void Names_OmitUnprefixedNamesTheHostReads() =>
        Assert.Equal(["Health__Port", "TRIGNIS_PORT"], EnvironmentSetting.Names("Health:Port", "Port"));

    [Fact]
    public void Aliases_UseTheHighestPrecedenceName_AndCoverGlobalSettings()
    {
        var env = new Dictionary<string, string?>
        {
            ["DATA_PATH"] = "low",
            ["TRIGNIS_DATA_PATH"] = "high",
            ["POLLING_INTERVAL_SECONDS"] = "10",
        };

        var aliases = EnvironmentSetting.Aliases(name => env.GetValueOrDefault(name));

        Assert.Equal("high", aliases["ChangeTracking:DataPath"]);
        Assert.Equal("10", aliases["ChangeTracking:GlobalSettings:PollingIntervalSeconds"]);
        Assert.Equal(2, aliases.Count);
    }
}
