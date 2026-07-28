using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Trignis.MicrosoftSQL.Helpers;

/// <summary>
/// Rewrites string properties of a JSON object in place. Encryption and decryption walk the same path, so this is a single implementation.
/// </summary>
internal static class JsonSecrets
{
    public static readonly string[] AuthProps = ["Token", "Password", "ApiKey", "ClientSecret", "ClientId"];

    public static readonly string[] MessageQueueProps = ["Password", "ConnectionString", "SecretAccessKey", "AccessKeyId"];

    /// <param name="names">Properties to visit, or null to visit every string property.</param>
    /// <param name="map">Receives (key, value); returns the replacement, or null to leave it alone.</param>
    public static void MapProps(JsonObject obj, string[]? names, Func<string, string, string?> map)
    {
        foreach (var key in names ?? obj.Select(p => p.Key).ToArray())
        {
            if (obj.TryGetPropertyValue(key, out var node) &&
                node is JsonValue value &&
                value.TryGetValue(out string? text) &&
                text != null &&
                map(key, text) is { } replacement)
            {
                obj[key] = replacement;
            }
        }
    }
}
