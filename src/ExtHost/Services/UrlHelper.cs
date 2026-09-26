using System.IO;
using System.Text.RegularExpressions;

namespace ExtHost.Services;

public static class UrlHelper
{
    public const string ExtensionsPageUrl = "exthost://extensions";

    private static readonly string[] PassThroughPrefixes =
    {
        "about:", "data:", "edge:", "chrome:", "view-source:", "blob:", "javascript:", "mailto:",
    };

    private static readonly Regex HostWithPort = new(@"^[A-Za-z0-9\-\.]+:\d{1,5}(/.*)?$", RegexOptions.Compiled);
    private static readonly Regex IpAddress = new(@"^\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?(/.*)?$", RegexOptions.Compiled);

    /// <summary>把網址列輸入轉成可導覽的網址；空字串回傳 null。</summary>
    public static string? ToNavigableUrl(string input, string searchUrl)
    {
        var text = input.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var url = FixupUrl(text);
        if (url != null)
        {
            return url;
        }

        var template = string.IsNullOrWhiteSpace(searchUrl) ? "https://www.google.com/search?q={0}" : searchUrl;
        return template.Replace("{0}", Uri.EscapeDataString(text));
    }

    /// <summary>把輸入整理成網址（例如 google.com → https://google.com）；看起來不像網址則回傳 null。</summary>
    public static string? FixupUrl(string input)
    {
        var text = input.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            return text;
        }

        foreach (var p in PassThroughPrefixes)
        {
            if (text.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
        }

        // 本機路徑，例如 C:\temp\a.html 或 \\server\share\a.html
        if (Regex.IsMatch(text, @"^[A-Za-z]:\\") || text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            try
            {
                return new Uri(text).AbsoluteUri;
            }
            catch
            {
                // 不是網址
            }
        }

        if (!text.Contains(' '))
        {
            if (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || IpAddress.IsMatch(text))
            {
                return "http://" + text;
            }

            if (HostWithPort.IsMatch(text))
            {
                return "http://" + text;
            }

            var hostPart = text.Split('/', 2)[0];
            if (hostPart.Contains('.') && !hostPart.StartsWith('.') && !hostPart.EndsWith('.'))
            {
                return "https://" + text;
            }

            // 單一名稱加路徑的內網網址，例如 intranet/forms
            if (text.Contains('/') && hostPart.Length > 0)
            {
                return "http://" + text;
            }
        }

        return null;
    }

    public static bool IsSecure(string? url) =>
        url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static bool IsExtensionUrl(string? url) =>
        url != null && url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

    public static bool IsBlank(string? url) =>
        string.IsNullOrEmpty(url) || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeFolder(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
