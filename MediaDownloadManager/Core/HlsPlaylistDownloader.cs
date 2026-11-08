using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaDownloadManager.Core;

/// <summary>
/// Download HLS/m3u8-like manifests (including .vl / googlevideo manifest-s1)
/// by fetching the playlist, downloading each segment, then ffmpeg-concat to MP4.
/// </summary>
public static class HlsPlaylistDownloader
{
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return c;
    }

    public static async Task<DownloadResult> DownloadAsync(
        string playlistUrl,
        string outputDir,
        Dictionary<string, string>? headers = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        using var http = CreateClient();
        ApplyHeaders(http, headers);

        progress?.Report(new DownloadProgress { Status = "downloading", Progress = 1, Name = "Fetching playlist…" });
        var playlistText = await http.GetStringAsync(playlistUrl, ct).ConfigureAwait(false);

        if (!playlistText.Contains("#EXT", StringComparison.OrdinalIgnoreCase) &&
            !playlistText.Contains("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("URL did not return an HLS/m3u8 playlist.");

        // Master playlist → pick highest bandwidth variant
        if (playlistText.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            var variant = PickBestVariant(playlistText, playlistUrl);
            if (variant is null)
                throw new InvalidOperationException("Master playlist has no usable variants.");
            progress?.Report(new DownloadProgress { Status = "downloading", Progress = 3, Name = "Fetching media playlist…" });
            playlistText = await http.GetStringAsync(variant, ct).ConfigureAwait(false);
            playlistUrl = variant;
        }

        var segments = ParseSegments(playlistText, playlistUrl);
        if (segments.Count == 0)
            throw new InvalidOperationException("Playlist has no media segments.");

        var work = Path.Combine(Path.GetTempPath(), "mdm_hls_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var partFiles = new List<string>();
            // URL often has no .png/.jpg — detect PNG-wrapped MPEG-TS from file magic after first seg.
            var pngTs = segments.Any(IsFakeImageSegment);
            long totalBytes = 0;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var segUrl = segments[i];
                var part = Path.Combine(work, $"seg_{i:D5}.bin");
                var segBytes = await DownloadFileAsync(http, segUrl, part, ct).ConfigureAwait(false);
                if (i == 0 && !pngTs)
                    pngTs = IsPngWrappedTs(part);
                partFiles.Add(part);
                totalBytes += segBytes;

                var done = i + 1;
                var pct = 5 + done * 85.0 / segments.Count;
                var elapsed = sw.Elapsed.TotalSeconds;
                double speed = 0;
                string? eta = null;
                if (elapsed > 0.2 && totalBytes > 0)
                {
                    speed = totalBytes / elapsed;
                    var remaining = segments.Count - done;
                    if (remaining > 0 && done > 0)
                    {
                        var secPerSeg = elapsed / done;
                        eta = FormatEta(TimeSpan.FromSeconds(secPerSeg * remaining));
                    }
                    else
                        eta = "00:00";
                }

                progress?.Report(new DownloadProgress
                {
                    Status = "downloading",
                    Progress = pct,
                    Speed = speed,
                    Eta = eta,
                    Name = $"Segment {done}/{segments.Count}"
                });
            }

            progress?.Report(new DownloadProgress
            {
                Status = "downloading",
                Progress = 92,
                Speed = 0,
                Eta = "—",
                Name = "Merging…"
            });
            var outPath = Path.Combine(outputDir, $"hls_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            await MergeAsync(partFiles, outPath, pngTs, ct).ConfigureAwait(false);

            progress?.Report(new DownloadProgress
            {
                Status = "finished",
                Progress = 100,
                Speed = 0,
                Eta = "",
                Filename = outPath
            });
            return new DownloadResult
            {
                Source = "hls",
                Url = playlistUrl,
                Title = Path.GetFileNameWithoutExtension(outPath),
                Filepath = outPath,
                Files = [outPath],
                Filesize = new FileInfo(outPath).Length
            };
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* ignore */ }
        }
    }

    private static void ApplyHeaders(HttpClient http, Dictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value)) continue;
            http.DefaultRequestHeaders.Remove(key);
            http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
    }

    private static string? PickBestVariant(string master, string baseUrl)
    {
        var lines = master.Replace("\r\n", "\n").Split('\n');
        var bestBw = -1L;
        string? bestUrl = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
            long bw = 0;
            var m = Regex.Match(line, @"BANDWIDTH=(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) long.TryParse(m.Groups[1].Value, out bw);
            // next non-empty non-tag line is URI
            for (var j = i + 1; j < lines.Length; j++)
            {
                var u = lines[j].Trim();
                if (string.IsNullOrEmpty(u) || u.StartsWith('#')) continue;
                if (bw >= bestBw)
                {
                    bestBw = bw;
                    bestUrl = ResolveUrl(baseUrl, u);
                }
                break;
            }
        }
        return bestUrl;
    }

    private static List<string> ParseSegments(string playlist, string baseUrl)
    {
        var list = new List<string>();
        foreach (var raw in playlist.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
            list.Add(ResolveUrl(baseUrl, line));
        }
        return list;
    }

    private static string ResolveUrl(string baseUrl, string maybeRelative)
    {
        if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out var abs))
            return abs.ToString();
        if (Uri.TryCreate(new Uri(baseUrl), maybeRelative, out var joined))
            return joined.ToString();
        return maybeRelative;
    }

    private static string GuessSegmentExt(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return ".ts";
        var path = uri.AbsolutePath;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return ".bin";
        // CDN often serves media under .png / .jpg / no meaningful ext — keep as .bin for concat
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return ".bin";
        return ext;
    }

    private static async Task<long> DownloadFileAsync(HttpClient http, string url, string dest, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(dest);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        return new FileInfo(dest).Length;
    }

    private static string FormatEta(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private static bool IsFakeImageSegment(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return true;
        var host = uri.Host;
        return host.Contains("ibyteimg", StringComparison.OrdinalIgnoreCase)
               || host.Contains("byteimg", StringComparison.OrdinalIgnoreCase)
               || host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PNG signature + MPEG-TS payload after IEND.</summary>
    private static bool IsPngWrappedTs(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[8];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(head) < 8) return false;
            }
            if (head[0] != 0x89 || head[1] != 0x50 || head[2] != 0x4E || head[3] != 0x47)
                return false;
            var ts = FfmpegConverter.ExtractTsFromPng(path);
            if (ts is null) return false;
            try { File.Delete(ts); } catch { /* ignore */ }
            return true;
        }
        catch { return false; }
    }

    private static async Task MergeAsync(List<string> parts, string outputMp4, bool pngTs, CancellationToken ct)
    {
        var ffmpeg = FfmpegConverter.FindFfmpeg()
            ?? throw new FileNotFoundException("ffmpeg not found — required to merge HLS segments");

        var work = Path.GetDirectoryName(parts[0])!;
        if (!pngTs && parts.Count > 0)
            pngTs = IsPngWrappedTs(parts[0]);

        string inputArgs;
        if (pngTs)
        {
            // Each file is a PNG with MPEG-TS payload after IEND.
            var joined = Path.Combine(work, "joined.ts");
            await using (var output = File.Create(joined))
            {
                foreach (var part in parts)
                {
                    ct.ThrowIfCancellationRequested();
                    var ts = FfmpegConverter.ExtractTsFromPng(part);
                    var src = ts ?? part;
                    await using var input = File.OpenRead(src);
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    if (ts is not null) { try { File.Delete(ts); } catch { /* ignore */ } }
                }
            }
            inputArgs = $"-f mpegts -i {FfmpegArg(joined)}";
        }
        else
        {
            var listFile = Path.Combine(work, "concat.txt");
            var sb = new StringBuilder();
            foreach (var p in parts)
                sb.Append("file '").Append(p.Replace("'", "'\\''")).AppendLine("'");
            await File.WriteAllTextAsync(listFile, sb.ToString(), ct).ConfigureAwait(false);
            inputArgs = $"-f concat -safe 0 -i {FfmpegArg(listFile)}";
        }

        var copyArgs = $"-y {inputArgs} -c copy -map 0 -movflags +faststart {FfmpegArg(outputMp4)}";
        var (ok, _) = await RunFfmpegAsync(ffmpeg, copyArgs, ct).ConfigureAwait(false);
        if (ok && File.Exists(outputMp4) && new FileInfo(outputMp4).Length > 0)
            return;

        try { if (File.Exists(outputMp4)) File.Delete(outputMp4); } catch { /* ignore */ }

        var reencodeArgs =
            $"-y {inputArgs} -map 0:v:0? -map 0:a? -c:v libx264 -preset veryfast -crf 23 " +
            $"-pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart {FfmpegArg(outputMp4)}";
        (ok, var err) = await RunFfmpegAsync(ffmpeg, reencodeArgs, ct).ConfigureAwait(false);
        if (!ok || !File.Exists(outputMp4) || new FileInfo(outputMp4).Length == 0)
            throw new InvalidOperationException("ffmpeg merge failed: " + Trim(err));
    }

    private static async Task<(bool Ok, string Err)> RunFfmpegAsync(string ffmpeg, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return (false, "failed to start ffmpeg");
        var err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode == 0, err);
    }

    private static string FfmpegArg(string path)
        => "\"" + Path.GetFullPath(path).Replace("\"", "\\\"") + "\"";

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length > 400 ? s[^400..] : s;
    }
}
