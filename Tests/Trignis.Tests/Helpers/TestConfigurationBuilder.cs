using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace Trignis.Tests.Helpers;

/// <summary>
/// Builds IConfiguration objects for use in unit tests, using flat in-memory key-value pairs
/// that mirror the format ConfigurationValidator expects.
/// </summary>
internal static class TestConfigurationBuilder
{
    private const string EnvBase = "ChangeTracking:Environments:0";

    /// <summary>Key prefix for the first API endpoint in the first environment.</summary>
    public const string EP = $"{EnvBase}:ChangeTracking:ApiEndpoints:0";

    /// <summary>
    /// Returns a minimal set of valid key-value pairs for one environment with one tracking object.
    /// ExportToApi is set to true so the validator will evaluate any ApiEndpoints you add.
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
