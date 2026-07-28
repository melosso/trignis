using Trignis.MicrosoftSQL.Services;
using Xunit;

namespace Trignis.Tests.Services;

/// <summary>
/// The export size limit now uses the template's root rather than a hardcoded directory
/// </summary>
public class ExportRootTests
{
    [Theory]
    // Default template: everything under the fixed prefix is subject to the limit.
    [InlineData("exports/{environment}/{object}/{database}/changes-{timestamp}.json", "exports")]
    [InlineData("exports/{object}/changes.json", "exports")]

    // A custom location must be honoured, not ignored in favour of "exports".
    [InlineData("/var/lib/trignis/dumps/{object}/{timestamp}.json", "/var/lib/trignis/dumps")]
    [InlineData("archive/nested/deep/{timestamp}.json", "archive/nested/deep")]

    // No placeholder at all: the containing directory still bounds the sweep.
    [InlineData("exports/dump.json", "exports")]
    public void DerivesTheFixedPrefixDirectory(string template, string expected)
    {
        Assert.Equal(expected, ChangeTrackingBackgroundService.ExportRoot(template));
    }

    [Theory]
    // A bare filename or a leading placeholder would resolve to the working directory.
    // Sweeping that for space is never what the setting means, so cleanup is skipped.
    [InlineData("{object}-{timestamp}.json")]
    [InlineData("changes.json")]
    public void SkipsCleanupWhenRootWouldBeTheWorkingDirectory(string template)
    {
        Assert.Null(ChangeTrackingBackgroundService.ExportRoot(template));
    }
}
