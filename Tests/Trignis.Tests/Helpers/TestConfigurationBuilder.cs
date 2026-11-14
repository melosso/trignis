using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace Trignis.Tests.Helpers;

/// <summary>
/// Builds IConfiguration objects from flat in-memory key-value pairs in the format that ConfigurationValidator reads.
/// </summary>
internal static class TestConfigurationBuilder
{
    private const string EnvBase = "ChangeTracking:Environments:0";

    /// <summary>Key prefix for the first API endpoint in the first environment.</summary>
    public const string EP = $"{EnvBase}:ChangeTracking:ApiEndpoints:0";

    /// <summary>
    /// Returns the minimal valid key-value pairs for one environment with one tracking object. ExportToApi is true: the validator evaluates added ApiEndpoints.
    /// </summary>
    public static Dictionary<string, string?> MinimalValidEnvironment() => new()
    {
        [$"{EnvBase}:Name"] = "TestEnv",
        [$"{EnvBase}:ConnectionStrings:TestDB"] = "Server=localhost;Database=TestDB;Trusted_Connection=True;",
        [$"{EnvBase}:ChangeTracking:TrackingObjects:0:Name"] = "Orders",
        [$"{EnvBase}:ChangeTracking:TrackingObjects:0:Database"] = "TestDB",
        [$"{EnvBase}:ChangeTracking:TrackingObjects:0:TableName"] = "dbo.Orders",
        [$"{EnvBase}:ChangeTracking:TrackingObjects:0:StoredProcedureName"] = "sp_GetOrders",
        [$"{EnvBase}:ChangeTracking:ExportToApi"] = "true",
    };

    /// <summary>Builds an IConfiguration from the provided flat key-value pairs.</summary>
    public static IConfiguration Build(Dictionary<string, string?> entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries)
            .Build();
}
