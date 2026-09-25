using System.Text.RegularExpressions;

namespace ExtHost.Services;

/// <summary>
/// Chrome 擴充功能 match pattern 的簡化實作，用來判斷目前頁面會被哪些 content script 注入。
/// 只用於顯示，不影響實際注入（實際注入由 WebView2 處理）。
/// </summary>
public sealed class MatchPattern
{
    private readonly Regex? _regex;
    private readonly bool _all;

    private MatchPattern(Regex? regex, bool all)
    {
        _regex = regex;
        _all = all;
    }

    public static MatchPattern? TryParse(string pattern)
    {
        if (pattern == "<all_urls>")
        {
            return new MatchPattern(null, true);
        }

        var m = Regex.Match(pattern, @"^(\*|https?|file|ftp|wss?)://([^/]*)(/.*)$");
        if (!m.Success)
        {
            return null;
        }

        var scheme = m.Groups[1].Value;
        var host = m.Groups[2].Value;
        var path = m.Groups[3].Value;

        var schemeRe = scheme == "*" ? "https?" : Regex.Escape(scheme);

        string hostRe;
        if (host == "*")
        {
            hostRe = "[^/]*";
        }
        else if (host.StartsWith("*.", StringComparison.Ordinal))
        {
            hostRe = @"([^/]*\.)?" + Regex.Escape(host[2..]) + "(:\\d+)?";
        }
        else
        {
            hostRe = Regex.Escape(host) + "(:\\d+)?";
        }

        var pathRe = Regex.Escape(path).Replace(@"\*", ".*");
        var full = "^" + schemeRe + "://" + hostRe + pathRe + "$";
        return new MatchPattern(new Regex(full, RegexOptions.IgnoreCase | RegexOptions.Compiled), false);
    }

    public bool IsMatch(string url)
    {
        if (_all)
        {
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
        }

        if (_regex == null)
        {
            return false;
        }

        // match pattern 不比對 query 與 fragment 以外的差異，這裡一併納入比對
        var hashIndex = url.IndexOf('#');
        if (hashIndex >= 0)
        {
            url = url[..hashIndex];
        }
        return _regex.IsMatch(url);
    }
}
