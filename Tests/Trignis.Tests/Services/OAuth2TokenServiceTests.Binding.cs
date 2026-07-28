using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Trignis.MicrosoftSQL.Models;
using Trignis.MicrosoftSQL.Services;
using Xunit;

namespace Trignis.Tests.Services;

/// <summary>
/// Regression cover for the snake_case token response. The web JSON defaults are camelCase and
/// case-insensitive, which never matches "access_token", so before the explicit
/// JsonPropertyName attributes every OAuth2 export failed with "Failed to obtain access token".
/// </summary>
public class OAuth2TokenBindingTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static ApiAuth Auth(int? expirationSeconds = null) => new()
    {
        Type = "OAuth2ClientCredentials",
        TokenEndpoint = "https://id.example.com/token",
        ClientId = "client",
        ClientSecret = "secret",
        TokenExpirationSeconds = expirationSeconds
    };

    private static OAuth2TokenService Service(StubHandler handler) =>
        new(NullLogger<OAuth2TokenService>.Instance, new StubFactory(handler));

    [Fact]
    public async Task SnakeCaseAccessToken_IsBound()
    {
        var handler = new StubHandler("""{"access_token":"tok-123","token_type":"Bearer","expires_in":3600}""");

        var token = await Service(handler).GetAccessTokenAsync(Auth(), "key");

        Assert.Equal("tok-123", token);
    }

    [Fact]
    public async Task MissingAccessToken_Throws()
    {
        var handler = new StubHandler("""{"token_type":"Bearer"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(handler).GetAccessTokenAsync(Auth(), "key"));
    }

    [Fact]
    public async Task CachedToken_IsReusedWithinItsLifetime()
    {
        var handler = new StubHandler("""{"access_token":"tok-123","expires_in":3600}""");
        var service = Service(handler);

        await service.GetAccessTokenAsync(Auth(), "key");
        await service.GetAccessTokenAsync(Auth(), "key");

        Assert.Equal(1, handler.Calls);
    }

    /// <summary>
    /// expires_in drives the cache lifetime when nothing is configured. A 60-second token is
    /// already past the one-minute safety margin, so the next call must re-request.
    /// </summary>
    [Fact]
    public async Task ShortExpiresIn_ForcesReRequest()
    {
        var handler = new StubHandler("""{"access_token":"tok-123","expires_in":60}""");
        var service = Service(handler);

        await service.GetAccessTokenAsync(Auth(), "key");
        await service.GetAccessTokenAsync(Auth(), "key");

        Assert.Equal(2, handler.Calls);
    }

    /// <summary>A configured lifetime overrides whatever the server reports.</summary>
    [Fact]
    public async Task ConfiguredExpiration_WinsOverExpiresIn()
    {
        var handler = new StubHandler("""{"access_token":"tok-123","expires_in":60}""");
        var service = Service(handler);

        await service.GetAccessTokenAsync(Auth(expirationSeconds: 3600), "key");
        await service.GetAccessTokenAsync(Auth(expirationSeconds: 3600), "key");

        Assert.Equal(1, handler.Calls);
    }
}
