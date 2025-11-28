using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Trignis.MicrosoftSQL.Models;

namespace Trignis.MicrosoftSQL.Services;

public class OAuth2TokenService
{
    private readonly ILogger<OAuth2TokenService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, TokenCacheEntry> _tokenCache = new();

    public OAuth2TokenService(ILogger<OAuth2TokenService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetAccessTokenAsync(ApiAuth auth, string cacheKey)
    {
        if (auth == null)
        {
            throw new ArgumentNullException(nameof(auth));
        }

        // Check cache first
        if (_tokenCache.TryGetValue(cacheKey, out var cachedToken) && !cachedToken.IsExpired)
        {
            _logger.LogDebug($"Using cached OAuth2 token for {cacheKey}");
            return cachedToken.AccessToken;
        }

        // Get new token
        var token = await RequestAccessTokenAsync(auth);
        var expiresIn = auth.TokenExpirationSeconds ?? 3600; // Default 1 hour
        var expiration = DateTime.UtcNow.AddSeconds(expiresIn - 60); // Refresh 1 minute early

        _tokenCache[cacheKey] = new TokenCacheEntry(token, expiration);
        _logger.LogInformation($"Obtained new OAuth2 token for {cacheKey}, expires at {expiration}");

        return token;
    }

    private async Task<string> RequestAccessTokenAsync(ApiAuth auth)
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

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
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