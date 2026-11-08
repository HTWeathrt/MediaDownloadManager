using System.Text.RegularExpressions;
using MediaDownloadManager.Models;

namespace MediaDownloadManager.Core;

public static partial class SourceDetector
{
    [GeneratedRegex(@"(?:youtube\.com|youtu\.be|youtube-nocookie\.com|music\.youtube\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex YoutubeRe();

    [GeneratedRegex(@"(?:tiktok\.com|vm\.tiktok\.com|vt\.tiktok\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex TiktokRe();

    [GeneratedRegex(@"(?:instagram\.com|instagr\.am)", RegexOptions.IgnoreCase)]
    private static partial Regex InstagramRe();

    [GeneratedRegex(@"(?:facebook\.com|fb\.watch|fb\.com|fbcdn\.net)", RegexOptions.IgnoreCase)]
    private static partial Regex FacebookRe();

    [GeneratedRegex(@"^magnet:\?xt=urn:btih:", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetRe();

    [GeneratedRegex(@"\.torrent(?:$|\?)", RegexOptions.IgnoreCase)]
    private static partial Regex TorrentFileRe();

    public static SourceType Detect(string? urlOrPath)
    {
        var text = (urlOrPath ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return SourceType.Unknown;

        if (MagnetRe().IsMatch(text) || TorrentFileRe().IsMatch(text) || text.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
            return SourceType.Torrent;

        if (CurlCommandParser.IsHlsUrl(text) ||
            text.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/manifest", StringComparison.OrdinalIgnoreCase))
            return SourceType.Hls;

        if (YoutubeRe().IsMatch(text)) return SourceType.Youtube;
        if (TiktokRe().IsMatch(text)) return SourceType.Tiktok;
        if (InstagramRe().IsMatch(text)) return SourceType.Instagram;
        if (FacebookRe().IsMatch(text)) return SourceType.Facebook;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrEmpty(uri.Host))
            return SourceType.Generic;

        return SourceType.Unknown;
    }

    public static bool IsSupported(string urlOrPath) => Detect(urlOrPath) != SourceType.Unknown;
}
