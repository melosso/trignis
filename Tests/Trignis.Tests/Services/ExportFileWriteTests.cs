using System.IO;
using System.Threading.Tasks;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests.Services;

public sealed class ExportFileWriteTests : System.IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trignis-export").FullName;

    [Fact]
    public async Task SameFileName_IsSuffixed_AndNoTempFileRemains()
    {
        var path = Path.Combine(_dir, "changes-20261119060000.json");

        var first = await ExportService.WriteNewFileAsync(path, "[1]");
        var second = await ExportService.WriteNewFileAsync(path, "[2]");

        Assert.Equal(path, first);
        Assert.Equal(Path.Combine(_dir, "changes-20261119060000-1.json"), second);
        Assert.Equal("[1]", File.ReadAllText(first));
        Assert.Equal("[2]", File.ReadAllText(second));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
