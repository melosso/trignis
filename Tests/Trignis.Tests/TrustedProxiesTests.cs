using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trignis.Helpers;
using Xunit;

namespace Trignis.Tests;

public class TrustedProxiesTests
{
    [Theory]
    [InlineData("10.0.0.5", "203.0.113.7", "203.0.113.7")]  // configured IP
    [InlineData("172.18.0.9", "203.0.113.7", "203.0.113.7")] // configured range
    [InlineData("127.0.0.1", "203.0.113.7", "203.0.113.7")]  // loopback trusted by default
    [InlineData("198.51.100.1", "203.0.113.7", "198.51.100.1")] // untrusted sender cannot spoof
    public async Task RemoteIpAddress_IsReadFromForwardedFor_ForTrustedProxies(string remote, string forwardedFor, string expected)
    {
        var options = TrustedProxies.CreateOptions(["10.0.0.5", " 172.18.0.0/16 ", "bogus"], out var invalid);
        Assert.Equal(["bogus"], invalid);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;

        await new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options))
            .Invoke(context);

        Assert.Equal(IPAddress.Parse(expected), context.Connection.RemoteIpAddress);
    }
}
