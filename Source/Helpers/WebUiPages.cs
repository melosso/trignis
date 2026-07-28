using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Trignis.MicrosoftSQL.Helpers;

/// <summary>
/// Beacon-style page rendering: _shell.html + views/{page}.html + _footer.html composed into one
/// document. Templates live outside wwwroot, so the static file middleware can never hand out a
/// raw fragment, and only names present in <see cref="Titles"/> resolve to a file — no request
/// value reaches the filesystem.
/// </summary>
internal static partial class WebUiPages
{
    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        ["dashboard"] = "Dashboard",
        ["environments"] = "Environments",
        ["settings"] = "Settings",
        ["deadletters"] = "Dead Letters",
        ["logs"] = "Logs"
    };

    public static IResult Compose(string uiRoot, string page, string title, string version)
    {
        var shellPath = Path.Combine(uiRoot, "_shell.html");
        var viewPath = Path.Combine(uiRoot, "views", $"{page}.html");
        var footerPath = Path.Combine(uiRoot, "_footer.html");

        if (!File.Exists(shellPath) || !File.Exists(viewPath))
            return Results.NotFound();

        var html = File.ReadAllText(shellPath)
                .Replace("<!-- PAGE_TITLE -->", HtmlEncoder.Default.Encode($"{title} · Trignis"))
            + File.ReadAllText(viewPath)
            + (File.Exists(footerPath) ? File.ReadAllText(footerPath) : "\n</body>\n</html>\n");

        return Render(html, version);
    }

    public static IResult ServePage(string filePath, string version) =>
        File.Exists(filePath) ? Render(File.ReadAllText(filePath), version) : Results.NotFound();

    /// <summary>Cache-busts local css/js so an upgrade cannot leave a stale asset in the browser.</summary>
    private static IResult Render(string html, string version)
    {
        var v = Uri.EscapeDataString(version);
        html = LocalAsset().Replace(html, m => $"{m.Groups[1].Value}=\"{m.Groups[2].Value}?v={v}\"");
        return Results.Content(html, "text/html; charset=utf-8");
    }

    [GeneratedRegex(@"(href|src)=""(?!https?://)([^""]+\.(?:css|js))""")]
    private static partial Regex LocalAsset();
}
