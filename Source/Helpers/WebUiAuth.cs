using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

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

    // Enough for every real login form in flight; a flood past this evicts the oldest instead of growing
    private const int MaxCsrfTokens = 10_000;

    private readonly ConcurrentDictionary<string, DateTime> _revokedSessions = new();

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
                // A lapsed lockout, or failures older than a lockout would have lasted, start the
                // count over: ten typos spread over a year are not a brute-force attempt.
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
    /// Double-submit check for mutating requests: the header must match the session cookie.
    /// An attacker's page can force the cookie to be sent but cannot read it to set the header.
    /// </summary>
    public static bool IsDoubleSubmitValid(string? headerValue, string? cookieValue) =>
        !string.IsNullOrEmpty(headerValue) &&
        !string.IsNullOrEmpty(cookieValue) &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(headerValue),
            System.Text.Encoding.UTF8.GetBytes(cookieValue));

    /// <summary>
    /// Ends a session on the server, so a copied cookie stops working at logout rather than at expiry.
    /// ponytail: held in memory, so a restart forgets it and the cookie works again until its
    /// own 24h expiry. Persist the ids if that window matters.
    /// </summary>
    public void RevokeSession(string sessionId, DateTime expiresAt) => _revokedSessions[sessionId] = expiresAt;

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
