using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Trignis.MicrosoftSQL.Models;

namespace Trignis.MicrosoftSQL.Services;

public class OAuth2TokenService
{
    private readonly ILogger<OAuth2TokenService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, TokenCacheEntry> _tokenCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();

    public OAuth2TokenService(ILogger<OAuth2TokenService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetAccessTokenAsync(ApiAuth auth, string cacheKey, CancellationToken cancellationToken = default)
    {
        if (auth == null)
        {
            throw new ArgumentNullException(nameof(auth));
        }

        // Fast path: check cache before acquiring semaphore
        if (_tokenCache.TryGetValue(cacheKey, out var cachedToken) && !cachedToken.IsExpired)
        {
            _logger.LogDebug($"Using cached OAuth2 token for {cacheKey}");
            return cachedToken.AccessToken;
        }

        // Per-key semaphore prevents thundering-herd on token expiry
        var sem = _refreshLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check inside the lock (double-checked locking)
            if (_tokenCache.TryGetValue(cacheKey, out var fresh) && !fresh.IsExpired)
                return fresh.AccessToken;

            var token = await RequestAccessTokenAsync(auth, cancellationToken).ConfigureAwait(false);
            var expiration = DateTime.UtcNow.AddSeconds(auth.TokenExpirationSeconds ?? 3600).AddMinutes(-1);
            _tokenCache[cacheKey] = new TokenCacheEntry(token, expiration);
            _logger.LogInformation($"Obtained new OAuth2 token for {cacheKey}, expires at {expiration}");
            return token;
        }
        finally { sem.Release(); }
    }

    private async Task<string> RequestAccessTokenAsync(ApiAuth auth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(auth.TokenEndpoint))
        {
            throw new ArgumentException("TokenEndpoint is required for OAuth2 authentication");
        }

        if (string.IsNullOrEmpty(auth.ClientId) || string.IsNullOrEmpty(auth.ClientSecret))
        {
            throw new ArgumentException("ClientId and ClientSecret are required for OAuth2 client credentials flow");
        }

        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, auth.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", auth.ClientId),
                new KeyValuePair<string, string>("client_secret", auth.ClientSecret),
                new KeyValuePair<string, string>("scope", auth.Scope ?? "")
            })
        };

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
        {
            throw new InvalidOperationException("Failed to obtain access token from OAuth2 endpoint");
        }

        return tokenResponse.AccessToken;
    }

    private class TokenCacheEntry
    {
        public string AccessToken { get; }
        public DateTime Expiration { get; }

        public bool IsExpired => DateTime.UtcNow >= Expiration;

        public TokenCacheEntry(string accessToken, DateTime expiration)
        {
            AccessToken = accessToken;
            Expiration = expiration;
        }
    }

    private class TokenResponse
    {
        public string? AccessToken { get; set; }
        public string? TokenType { get; set; }
        public int? ExpiresIn { get; set; }
        public string? Scope { get; set; }
    }
}