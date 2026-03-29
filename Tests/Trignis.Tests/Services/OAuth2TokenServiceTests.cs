using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Trignis.MicrosoftSQL.Models;
using Trignis.MicrosoftSQL.Services;
using Xunit;

namespace Trignis.Tests.Services;

/// <summary>
/// Tests for OAuth2TokenService — validation, caching, HTTP integration, and error handling.
/// Uses a stub HttpMessageHandler so no real token endpoint is required.
/// </summary>
public class OAuth2TokenServiceTests
{
    // -------------------------------------------------------------------------
    // Infrastructure stubs
    // -------------------------------------------------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StubHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private static OAuth2TokenService BuildService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var client = new HttpClient(new StubHandler(handler));
        var factory = new StubHttpClientFactory(client);
        return new OAuth2TokenService(NullLogger<OAuth2TokenService>.Instance, factory);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ApiAuth ValidAuth(string? scope = null) => new()
    {
        Type = "OAuth2ClientCredentials",
        TokenEndpoint = "https://auth.example.com/token",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        Scope = scope
    };

    // -------------------------------------------------------------------------
    // Argument validation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NullAuth_ThrowsArgumentNullException()
    {
        var svc = BuildService(_ => JsonResponse(@"{""AccessToken"":""tok""}"));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            svc.GetAccessTokenAsync(null!, "key"));
    }

    [Fact]
    public async Task MissingTokenEndpoint_ThrowsArgumentException()
    {
        var svc = BuildService(_ => JsonResponse(@"{""AccessToken"":""tok""}"));
        var auth = new ApiAuth { ClientId = "id", ClientSecret = "secret" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GetAccessTokenAsync(auth, "key"));
        Assert.Contains("TokenEndpoint", ex.Message);
    }

    [Fact]
    public async Task MissingClientId_ThrowsArgumentException()
    {
        var svc = BuildService(_ => JsonResponse(@"{""AccessToken"":""tok""}"));
        var auth = new ApiAuth
        {
            TokenEndpoint = "https://auth.example.com/token",
            ClientSecret = "secret"
            // ClientId intentionally omitted
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GetAccessTokenAsync(auth, "key"));
        Assert.Contains("ClientId", ex.Message);
    }

    [Fact]
    public async Task MissingClientSecret_ThrowsArgumentException()
    {
        var svc = BuildService(_ => JsonResponse(@"{""AccessToken"":""tok""}"));
        var auth = new ApiAuth
        {
            TokenEndpoint = "https://auth.example.com/token",
            ClientId = "client-id"
            // ClientSecret intentionally omitted
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GetAccessTokenAsync(auth, "key"));
        Assert.Contains("ClientSecret", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidRequest_ReturnsTokenFromResponse()
    {
        var svc = BuildService(_ => JsonResponse(@"{""AccessToken"":""my-access-token""}"));
        var token = await svc.GetAccessTokenAsync(ValidAuth(), "k1");
        Assert.Equal("my-access-token", token);
    }

    [Fact]
    public async Task ValidRequest_SendsClientCredentialsGrantType()
    {
        string? capturedBody = null;
        var svc = BuildService(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(@"{""AccessToken"":""tok""}");
        });

        await svc.GetAccessTokenAsync(ValidAuth(), "k1");

        Assert.NotNull(capturedBody);
        Assert.Contains("grant_type=client_credentials", capturedBody);
        Assert.Contains("client_id=client-id", capturedBody);
        Assert.Contains("client_secret=client-secret", capturedBody);
    }

    [Fact]
    public async Task ValidRequest_WithScope_IncludesScopeInBody()
    {
        string? capturedBody = null;
        var svc = BuildService(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(@"{""AccessToken"":""tok""}");
        });

        await svc.GetAccessTokenAsync(ValidAuth("api.read api.write"), "k1");

        Assert.NotNull(capturedBody);
        Assert.Contains("scope=api.read+api.write", capturedBody);
    }

    // -------------------------------------------------------------------------
    // Caching
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SecondCall_WithSameKey_ReturnsCachedToken_WithoutHttpRequest()
    {
        var callCount = 0;
        var svc = BuildService(_ =>
        {
            callCount++;
            return JsonResponse(@"{""AccessToken"":""cached-token""}");
        });

        var first = await svc.GetAccessTokenAsync(ValidAuth(), "cache-key");
        var second = await svc.GetAccessTokenAsync(ValidAuth(), "cache-key");

        Assert.Equal("cached-token", first);
        Assert.Equal("cached-token", second);
        Assert.Equal(1, callCount); // HTTP called only once
    }

    [Fact]
    public async Task DifferentCacheKeys_FetchSeparateTokens()
    {
        var callCount = 0;
        var svc = BuildService(_ =>
        {
            callCount++;
            return JsonResponse($@"{{""AccessToken"":""token-{callCount}""}}");
        });

        var t1 = await svc.GetAccessTokenAsync(ValidAuth(), "key-a");
        var t2 = await svc.GetAccessTokenAsync(ValidAuth(), "key-b");

        Assert.Equal(2, callCount);
        Assert.NotEqual(t1, t2);
    }

    // -------------------------------------------------------------------------
    // Error paths
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HttpError_ThrowsHttpRequestException()
    {
        var svc = BuildService(_ => JsonResponse("{}", HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            svc.GetAccessTokenAsync(ValidAuth(), "k1"));
    }

    [Fact]
    public async Task EmptyTokenInResponse_ThrowsInvalidOperationException()
    {
        var svc = BuildService(_ => JsonResponse(@"{""AccessToken"":""""}"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.GetAccessTokenAsync(ValidAuth(), "k1"));
        Assert.Contains("access token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullTokenInResponse_ThrowsInvalidOperationException()
    {
        var svc = BuildService(_ => JsonResponse(@"{""AccessToken"":null}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.GetAccessTokenAsync(ValidAuth(), "k1"));
    }

    [Fact]
    public async Task CancellationRequested_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var svc = BuildService(_ =>
        {
            cts.Cancel();
            return JsonResponse(@"{""AccessToken"":""tok""}");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.GetAccessTokenAsync(ValidAuth(), "k1", cts.Token));
    }
}
