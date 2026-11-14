using System;
using System.Linq;
using Trignis.Helpers;
using Xunit;

namespace Trignis.Tests;

public class WebUiAuthTests
{
    [Fact]
    public void TenFailures_LockTheClientOut_AndASuccessClearsIt()
    {
        var auth = new WebUiAuth();

        for (var i = 0; i < 9; i++) auth.RecordFailedAttempt("1.2.3.4");
        Assert.Null(auth.CheckAccess("1.2.3.4"));

        auth.RecordFailedAttempt("1.2.3.4");
        Assert.NotNull(auth.CheckAccess("1.2.3.4"));
        Assert.Null(auth.CheckAccess("5.6.7.8")); // per client, not global

        auth.ClearFailedAttempts("1.2.3.4");
        Assert.Null(auth.CheckAccess("1.2.3.4"));
    }

    [Fact]
    public void LoginCsrfToken_IsSingleUse_AndUnknownTokensAreRefused()
    {
        var auth = new WebUiAuth();
        var token = auth.GenerateCsrfToken();

        Assert.True(auth.ValidateCsrfToken(token));
        Assert.False(auth.ValidateCsrfToken("made-up"));
        Assert.False(auth.ValidateCsrfToken(null));

        auth.ConsumeCsrfToken(token);
        Assert.False(auth.ValidateCsrfToken(token));
    }

    [Fact]
    public void LoginCsrfTokens_AreBounded_AndTheNewestSurviveAFlood()
    {
        var auth = new WebUiAuth();

        var tokens = Enumerable.Range(0, 12_000).Select(_ => auth.GenerateCsrfToken()).ToList();

        Assert.True(auth.ValidateCsrfToken(tokens[^1]));
        Assert.True(tokens.Count(auth.ValidateCsrfToken) <= 10_000);
    }

    [Fact]
    public void RevokedSession_IsReportedRevoked()
    {
        var auth = new WebUiAuth();

        Assert.False(auth.IsSessionRevoked("abc"));
        auth.RevokeSession("abc", DateTime.UtcNow.AddHours(1));
        Assert.True(auth.IsSessionRevoked("abc"));
    }

    [Theory]
    [InlineData("t", "t", true)]
    [InlineData("t", "u", false)]
    [InlineData("", "", false)]
    [InlineData(null, "t", false)]
    [InlineData("t", null, false)]
    public void DoubleSubmit_RequiresMatchingNonEmptyValues(string? header, string? cookie, bool expected) =>
        Assert.Equal(expected, WebUiAuth.IsDoubleSubmitValid(header, cookie));
}
