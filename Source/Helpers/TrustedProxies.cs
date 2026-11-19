using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Trignis.Helpers;

/// <summary>Forwarded-header options trusting loopback and configured proxies.</summary>
public static class TrustedProxies
{
    /// <summary>Accepts addresses and CIDR ranges; returns unparsable entries.</summary>
    public static ForwardedHeadersOptions CreateOptions(IEnumerable<string>? entries, out IReadOnlyList<string> invalid)
    {
        // Defaults already trust loopback.
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };

        var rejected = new List<string>();
        foreach (var raw in entries ?? [])
        {
            var entry = raw.Trim();
            if (IPAddress.TryParse(entry, out var address))
                options.KnownProxies.Add(address);
            else if (System.Net.IPNetwork.TryParse(entry, out var network))
                options.KnownIPNetworks.Add(network);
            else
                rejected.Add(raw);
        }

        invalid = rejected;
        return options;
    }
}
