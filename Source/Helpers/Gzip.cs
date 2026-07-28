using System.IO;
using System.IO.Compression;
using System.Text;

namespace Trignis.Helpers;

internal static class Gzip
{
    public static byte[] Compress(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }
}
