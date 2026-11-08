using System.Text.RegularExpressions;
using MediaDownloadManager.Models;

namespace MediaDownloadManager.Core;

public static partial class UrlListParser
{
    [GeneratedRegex(@"(https?://[^\s<>""']+|magnet:\?xt=urn:btih:[a-zA-Z0-9]+[^\s<>""']*)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRe();

    [GeneratedRegex(@"[\r\n\t,;]+")]
    private static partial Regex SplitRe();

    public static IReadOnlyList<string> ExtractUrls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Prefer structured curl / HLS parse when present
        if (CurlCommandParser.LooksLikeCurl(text) || CurlCommandParser.LooksLikeM3u8(text))
        {
            var fromCurl = CurlCommandParser.Parse(text)
                .Select(r => r.Url)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (fromCurl.Count > 0) return fromCurl;
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in UrlRe().Matches(text))
        {
            var url = m.Groups[1].Value.TrimEnd(')', '.', ',', ']', '}', '"', '\'');
            if (!seen.Add(url)) continue;
            if (SourceDetector.Detect(url) == SourceType.Unknown) continue;
            found.Add(url);
        }

        foreach (var partRaw in SplitRe().Split(text))
        {
            var part = partRaw.Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(part) || seen.Contains(part)) continue;
            if (part.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
            {
                seen.Add(part);
                found.Add(part);
                continue;
            }
            if (!(part.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || part.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                  || part.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (SourceDetector.Detect(part) == SourceType.Unknown) continue;
            seen.Add(part);
            found.Add(part);
        }

        return found;
    }

    public static IReadOnlyList<string> LoadListFile(string path, long maxBytes = 2_000_000)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("List file not found", path);
        if (info.Length > maxBytes)
            throw new InvalidOperationException($"List file too large ({info.Length} bytes, max {maxBytes})");

        var found = new List<string>();
        var chunk = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            chunk.Add(line);
            if (chunk.Count < 200) continue;
            MergeInto(found, ExtractUrls(string.Join('\n', chunk)));
            chunk.Clear();
        }
        if (chunk.Count > 0)
            MergeInto(found, ExtractUrls(string.Join('\n', chunk)));
        return found;
    }

    public static IReadOnlyList<ParsedDownloadRequest> ExtractRequests(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        if (CurlCommandParser.LooksLikeCurl(text))
            return CurlCommandParser.Parse(text);

        return ExtractUrls(text).Select(u => new ParsedDownloadRequest
        {
            Url = u,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            IsHls = CurlCommandParser.IsHlsUrl(u)
        }).ToList();
    }

    public static IReadOnlyList<string> MergeLists(params IEnumerable<string>[] lists)
    {
        var outList = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in lists)
        {
            foreach (var url in group)
            {
                var t = url.Trim();
                if (string.IsNullOrEmpty(t) || !seen.Add(t)) continue;
                if (SourceDetector.Detect(t) == SourceType.Unknown &&
                    !t.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                    continue;
                outList.Add(t);
            }
        }
        return outList;
    }

    private static void MergeInto(List<string> target, IReadOnlyList<string> add)
    {
        var seen = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        foreach (var u in add)
        {
            if (seen.Add(u)) target.Add(u);
        }
    }
}
