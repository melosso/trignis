using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Trignis.Helpers;

/// <summary>
/// Web UI brute-force and CSRF protection with request-driven, opportunistic cache pruning.
/// </summary>
public sealed class WebUiAuth
{
    private const int MaxFailuresBeforeLockout = 10;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CsrfTokenLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Attempt> _failedAttempts = new();
    private readonly ConcurrentDictionary<string, DateTime> _csrfTokens = new();
    private long _lastPruneTicks = DateTime.UtcNow.Ticks;

    // Bounded: a flood evicts the oldest tokens.
    private const int MaxCsrfTokens = 10_000;

    private readonly ConcurrentDictionary<string, DateTime> _revokedSessions = new();
    private readonly string? _stateConnectionString;

    public WebUiAuth() { }

    /// <summary>Persists revoked sessions in the state database.</summary>
    public WebUiAuth(string stateDbPath)
    {
        _stateConnectionString = $"Data Source={stateDbPath}";
        using var conn = new SqliteConnection(_stateConnectionString);
        conn.Open();
        var command = conn.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS RevokedSessions (SessionId TEXT PRIMARY KEY, ExpiresAt INTEGER NOT NULL);
            DELETE FROM RevokedSessions WHERE ExpiresAt < @now;
            SELECT SessionId, ExpiresAt FROM RevokedSessions;";
        command.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            _revokedSessions[reader.GetString(0)] = new DateTime(reader.GetInt64(1), DateTimeKind.Utc);
    }

    private readonly record struct Attempt(int Failures, DateTime LastFailure, DateTime? LockedUntil);

    /// <summary>Returns a message to show the caller when blocked, or null when allowed.</summary>
    public string? CheckAccess(string clientIp)
    {
        Prune();

        if (!_failedAttempts.TryGetValue(clientIp, out var attempt) || attempt.LockedUntil is not { } lockedUntil)
            return null;

        if (lockedUntil <= DateTime.UtcNow)
            return null;

        var remaining = lockedUntil - DateTime.UtcNow;
        return $"Too many failed attempts. Try again in {Math.Max(1, (int)remaining.TotalMinutes)} minute(s).";
    }

    public void RecordFailedAttempt(string clientIp)
    {
        var now = DateTime.UtcNow;

        _failedAttempts.AddOrUpdate(
            clientIp,
            _ => new Attempt(1, now, null),
            (_, existing) =>
            {
                // An expired lockout or failures older than the lockout duration reset the failure count.
                var failures = (existing.LockedUntil is { } until && until < now) || now - existing.LastFailure > LockoutDuration
                    ? 1
                    : existing.Failures + 1;

                return new Attempt(
                    failures,
                    now,
                    failures >= MaxFailuresBeforeLockout ? now.Add(LockoutDuration) : null);
            });
    }

    public void ClearFailedAttempts(string clientIp) => _failedAttempts.TryRemove(clientIp, out _);

    public string GenerateCsrfToken()
    {
        Prune();

        // The endpoint is unauthenticated, so the store has to be bounded
        if (_csrfTokens.Count >= MaxCsrfTokens)
            foreach (var oldest in _csrfTokens.OrderBy(kvp => kvp.Value).Take(MaxCsrfTokens / 10).Select(kvp => kvp.Key).ToList())
                _csrfTokens.TryRemove(oldest, out _);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _csrfTokens[token] = DateTime.UtcNow.AddSeconds(CsrfTokenLifetime.TotalSeconds);
        return token;
    }

    public bool ValidateCsrfToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        if (!_csrfTokens.TryGetValue(token, out var expiresAt))
            return false;

        if (expiresAt > DateTime.UtcNow)
            return true;

        _csrfTokens.TryRemove(token, out _);
        return false;
    }

    /// <summary>One-time use: a login token cannot be replayed.</summary>
    public void ConsumeCsrfToken(string token) => _csrfTokens.TryRemove(token, out _);

    /// <summary>
    /// Double-submit check for mutating requests: the header must equal the session cookie. A cross-site page cannot read the cookie to set the header.
    /// </summary>
    public static bool IsDoubleSubmitValid(string? headerValue, string? cookieValue) =>
        !string.IsNullOrEmpty(headerValue) &&
        !string.IsNullOrEmpty(cookieValue) &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(headerValue),
            System.Text.Encoding.UTF8.GetBytes(cookieValue));

    /// <summary>
    /// Revokes a session on the server: a copied cookie is rejected after logout.
    /// </summary>
    public void RevokeSession(string sessionId, DateTime expiresAt)
    {
        _revokedSessions[sessionId] = expiresAt;
        if (_stateConnectionString is null)
            return;

        using var conn = new SqliteConnection(_stateConnectionString);
        conn.Open();
        var command = conn.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO RevokedSessions (SessionId, ExpiresAt) VALUES (@id, @expiresAt)";
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@expiresAt", expiresAt.Ticks);
        command.ExecuteNonQuery();
    }

    public bool IsSessionRevoked(string sessionId) => _revokedSessions.ContainsKey(sessionId);

    public static string NewSessionCsrf() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private void Prune()
    {
        var now = DateTime.UtcNow;
        var last = new DateTime(Interlocked.Read(ref _lastPruneTicks), DateTimeKind.Utc);
        if (now - last < PruneInterval)
            return;

        // A racing thread doing the same sweep is harmless, so no lock.
        Interlocked.Exchange(ref _lastPruneTicks, now.Ticks);

        foreach (var token in _csrfTokens.Where(kvp => kvp.Value < now).Select(kvp => kvp.Key).ToList())
            _csrfTokens.TryRemove(token, out _);

        foreach (var session in _revokedSessions.Where(kvp => kvp.Value < now).Select(kvp => kvp.Key).ToList())
            _revokedSessions.TryRemove(session, out _);

        foreach (var ip in _failedAttempts
                     .Where(kvp => kvp.Value.LockedUntil is { } until ? until < now : now - kvp.Value.LastFailure > LockoutDuration)
                     .Select(kvp => kvp.Key)
                     .ToList())
            _failedAttempts.TryRemove(ip, out _);
    }
}
