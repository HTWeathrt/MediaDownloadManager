using System.Text.RegularExpressions;

namespace MediaDownloadManager.Core;

public sealed class ParsedDownloadRequest
{
    public string Url { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsHls { get; init; }
}

public static partial class CurlCommandParser
{
    [GeneratedRegex(@"curl\b", RegexOptions.IgnoreCase)]
    private static partial Regex CurlWord();

    [GeneratedRegex(@"--url\s+['""](?<u>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex UrlFlag();

    [GeneratedRegex(@"['""](https?://[^'""\s]+)['""]")]
    private static partial Regex QuotedHttp();

    [GeneratedRegex(@"\s(https?://[^\s\\]+)")]
    private static partial Regex BareHttp();

    [GeneratedRegex(@"-H\s+['""](?<h>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderFlag();

    [GeneratedRegex(@"-A\s+['""](?<ua>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex UserAgentFlag();

    [GeneratedRegex(@"--user-agent\s+['""](?<ua>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex UserAgentLong();

    [GeneratedRegex(@"--referer\s+['""](?<r>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex RefererFlag();

    public static bool LooksLikeCurl(string? text)
        => !string.IsNullOrWhiteSpace(text) && CurlWord().IsMatch(text);

    public static bool LooksLikeM3u8(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimStart();
        return t.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)
               || t.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
               || t.Contains("/manifest", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ParsedDownloadRequest> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var results = new List<ParsedDownloadRequest>();

        if (LooksLikeCurl(text))
        {
            var normalized = text.Replace("\\\r\n", " ").Replace("\\\n", " ").Replace("\r\n", "\n");
            // Split multiple curl commands loosely
            var chunks = Regex.Split(normalized, @"(?=curl\b)", RegexOptions.IgnoreCase)
                .Where(c => CurlWord().IsMatch(c))
                .ToList();
            if (chunks.Count == 0) chunks.Add(normalized);

            foreach (var chunk in chunks)
            {
                var url = ExtractUrl(chunk);
                if (string.IsNullOrEmpty(url)) continue;
                var headers = ExtractHeaders(chunk);
                results.Add(new ParsedDownloadRequest
                {
                    Url = url,
                    Headers = headers,
                    IsHls = IsHlsUrl(url)
                });
            }
        }

        // Also pick plain URLs that look like HLS manifests
        foreach (Match m in QuotedHttp().Matches(text ?? ""))
        {
            var u = m.Groups[1].Value;
            if (results.Any(r => string.Equals(r.Url, u, StringComparison.OrdinalIgnoreCase))) continue;
            if (!IsHlsUrl(u) && !SourceDetector.IsSupported(u)) continue;
            results.Add(new ParsedDownloadRequest
            {
                Url = u,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                IsHls = IsHlsUrl(u)
            });
        }

        return results;
    }

    public static bool IsHlsUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains(".vl", StringComparison.OrdinalIgnoreCase) &&
            (url.Contains("manifest", StringComparison.OrdinalIgnoreCase) ||
             url.Contains("googlevideo", StringComparison.OrdinalIgnoreCase) ||
             url.Contains("qooglevideo", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (url.Contains("/manifest", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains("manifest-s1", StringComparison.OrdinalIgnoreCase)) return true;
        if ((url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
             url.Contains("qooglevideo.com", StringComparison.OrdinalIgnoreCase)) &&
            url.Contains("manifest", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string ExtractUrl(string chunk)
    {
        var m = UrlFlag().Match(chunk);
        if (m.Success) return m.Groups["u"].Value.Trim();

        m = QuotedHttp().Match(chunk);
        if (m.Success) return m.Groups[1].Value.Trim();

        m = BareHttp().Match(chunk);
        if (m.Success) return m.Groups[1].Value.Trim().TrimEnd('\\');

        return "";
    }

    private static Dictionary<string, string> ExtractHeaders(string chunk)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in HeaderFlag().Matches(chunk))
        {
            var raw = m.Groups["h"].Value.Trim();
            if (string.IsNullOrEmpty(raw)) continue;

            // curl -H 'Referer;' → empty referer; skip empty values
            var colon = raw.IndexOf(':');
            if (colon < 0)
            {
                var name = raw.TrimEnd(';').Trim();
                if (!string.IsNullOrEmpty(name))
                    headers[name] = "";
                continue;
            }

            var key = raw[..colon].Trim();
            var val = raw[(colon + 1)..].Trim();
            if (string.IsNullOrEmpty(key)) continue;
            headers[key] = val;
        }

        var ua = UserAgentFlag().Match(chunk);
        if (ua.Success) headers["User-Agent"] = ua.Groups["ua"].Value;
        ua = UserAgentLong().Match(chunk);
        if (ua.Success) headers["User-Agent"] = ua.Groups["ua"].Value;

        var referer = RefererFlag().Match(chunk);
        if (referer.Success) headers["Referer"] = referer.Groups["r"].Value;

        return headers;
    }
}
