using System;
using System.IO;
using Trignis.MicrosoftSQL.Services;
using Xunit;

namespace Trignis.Tests.Services;

/// <summary>
/// Tests for EncryptionService — round-trip encryption, IsEncrypted detection,
/// and error handling for invalid input.
///
/// Each test instance gets its own isolated temp directory.
/// FindProjectRoot searches upward for an "environments" folder; we create one
/// inside the temp dir so the service roots itself there.
/// </summary>
public sealed class EncryptionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EncryptionService _svc;

    public EncryptionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trignis-enc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "environments")); // anchor for FindProjectRoot
        _svc = new EncryptionService(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // -------------------------------------------------------------------------
    // IsEncrypted
    // -------------------------------------------------------------------------

    [Fact]
    public void IsEncrypted_ReturnsFalse_ForPlaintext()
    {
        Assert.False(_svc.IsEncrypted("hello world"));
    }

    [Fact]
    public void IsEncrypted_ReturnsFalse_ForEmpty()
    {
        Assert.False(_svc.IsEncrypted(string.Empty));
    }

    [Fact]
    public void IsEncrypted_ReturnsTrue_ForEncryptedContent()
    {
        var encrypted = _svc.Encrypt("secret");
        Assert.True(_svc.IsEncrypted(encrypted));
    }

    [Fact]
    public void IsEncrypted_ReturnsTrue_OnlyWhenPwencPrefixPresent()
    {
        Assert.True(_svc.IsEncrypted("PWENC:somedata::moredata"));
        Assert.False(_svc.IsEncrypted("pwenc:lowercase"));   // prefix is case-sensitive
        Assert.False(_svc.IsEncrypted("NOTENC:data"));
    }

    // -------------------------------------------------------------------------
    // Encrypt
    // -------------------------------------------------------------------------

    [Fact]
    public void Encrypt_ReturnsPwencPrefixedString()
    {
        var result = _svc.Encrypt("my-secret-value");
        Assert.StartsWith("PWENC:", result);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertext_ForSamePlaintext()
    {
        // AES uses a random IV, so two encryptions must differ
        var a = _svc.Encrypt("same input");
        var b = _svc.Encrypt("same input");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Encrypt_DoesNotReturnPlaintext()
    {
        var result = _svc.Encrypt("super-secret");
        Assert.DoesNotContain("super-secret", result);
    }

    // -------------------------------------------------------------------------
    // Round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void Encrypt_ThenDecrypt_ReturnsOriginalValue()
    {
        const string original = "Server=prod;Password=hunter2;";
        var encrypted = _svc.Encrypt(original);
        var decrypted = _svc.Decrypt(encrypted);
        Assert.Equal(original, decrypted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("unicode: \u00e9\u00e0\u00fc")]
    [InlineData("connection string with spaces and symbols: @#$%")]
    [InlineData("very long value: " + "x")]  // short — full long strings handled in next test
    public void Encrypt_Decrypt_RoundTrip_Various(string plaintext)
    {
        var decrypted = _svc.Decrypt(_svc.Encrypt(plaintext));
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_LongValue()
    {
        var longValue = new string('A', 10_000);
        Assert.Equal(longValue, _svc.Decrypt(_svc.Encrypt(longValue)));
    }

    // -------------------------------------------------------------------------
    // Decrypt error handling
    // -------------------------------------------------------------------------

    [Fact]
    public void Decrypt_ThrowsInvalidOperation_ForPlaintext()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _svc.Decrypt("this is plaintext, not encrypted"));
        Assert.Contains("not encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decrypt_ThrowsFormatException_ForMalformedEncryptedString()
    {
        // Has PWENC: prefix but body is not valid Base64/format
        Assert.Throws<FormatException>(() =>
            _svc.Decrypt("PWENC:garbage::more-garbage"));
    }

    [Fact]
    public void Decrypt_ThrowsFormatException_ForPartialPwencHeader()
    {
        // "PWENC:" prefix exists but content section is missing — service throws FormatException
        Assert.Throws<FormatException>(() =>
            _svc.Decrypt("PWENC:"));
    }

    // -------------------------------------------------------------------------
    // Key pair persistence
    // -------------------------------------------------------------------------

    [Fact]
    public void SecondInstance_SameDirectory_ReusesExistingKeyPair()
    {
        // A second service pointing at the same root must use the same keys
        var svc2 = new EncryptionService(_tempDir);

        const string plaintext = "cross-instance secret";
        var encrypted = _svc.Encrypt(plaintext);
        var decrypted = svc2.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DifferentDirectories_HaveDifferentKeyPairs_CannotCrossDecrypt()
    {
        var tempDir2 = Path.Combine(Path.GetTempPath(), $"trignis-enc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir2, "environments"));
        try
        {
            var svc2 = new EncryptionService(tempDir2);
            var encrypted = _svc.Encrypt("my value");

            // Decrypting with a different key pair should fail
            Assert.ThrowsAny<Exception>(() => svc2.Decrypt(encrypted));
        }
        finally
        {
            try { Directory.Delete(tempDir2, recursive: true); } catch { /* best-effort */ }
        }
    }
}
