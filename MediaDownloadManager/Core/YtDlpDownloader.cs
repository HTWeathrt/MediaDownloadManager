using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaDownloadManager.Models;

namespace MediaDownloadManager.Core;

public sealed class YtDlpDownloader
{
    private static readonly Regex ProgressRe = new(
        @"\[download\]\s+(?<pct>\d+(?:\.\d+)?)%.*?at\s+(?<speed>[\d.]+[KMG]?i?B)/s(?:\s+ETA\s+(?<eta>\S+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProgressPctOnlyRe = new(
        @"\[download\]\s+(?<pct>\d+(?:\.\d+)?)%",
        RegexOptions.Compiled);

    private static readonly HashSet<SourceType> CookieOk =
    [
        SourceType.Youtube, SourceType.Tiktok, SourceType.Instagram, SourceType.Facebook
    ];

    public string? YtDlpPath { get; set; }
    public string OutputDir { get; set; } = "downloads";
    public bool AudioOnly { get; set; }
    public string? CookiesFromBrowser { get; set; }
    public string? Proxy { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DownloadResult> DownloadAsync(
        string url,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var source = SourceDetector.Detect(url);
        var mode = CookiesFromBrowser?.Trim();
        if (!string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase) || !CookieOk.Contains(source))
            return await DownloadCoreAsync(url, progress, ct, mode).ConfigureAwait(false);

        // Auto: try without cookies first, then each installed browser if the site asks for login.
        try
        {
            return await DownloadCoreAsync(url, progress, ct, null).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (NeedsCookies(ex.Message))
        {
            var first = ex;
            Exception last = first;
            var candidates = new List<string>();
            var cookieFile = FindCookiesFile();
            if (cookieFile is not null) candidates.Add("file:" + cookieFile);
            candidates.AddRange(DetectBrowsers());

            foreach (var cand in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await DownloadCoreAsync(url, progress, ct, cand).ConfigureAwait(false);
                }
                catch (InvalidOperationException bex) when (NeedsCookies(bex.Message))
                {
                    last = bex;
                    ErrorLog.Write($"auto cookies: {cand} failed — {bex.Message}");
                }
            }
            throw new InvalidOperationException(
                "YouTube needs login cookies and none could be read. Use Tools > Sign in to YouTube... " +
                "(in-app login), then Start / retry selected. Last error: " + last.Message);
        }
    }

    private static string? FindCookiesFile()
    {
        if (CookieStore.Exists) return CookieStore.FilePath;
        foreach (var dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var p = Path.Combine(dir, "cookies.txt");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static bool NeedsCookies(string message)
        => message.Contains("Sign in", StringComparison.OrdinalIgnoreCase)
           || message.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
           || message.Contains("cookie", StringComparison.OrdinalIgnoreCase)
           || message.Contains("DPAPI", StringComparison.OrdinalIgnoreCase);

    private static List<string> DetectBrowsers()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var list = new List<string>();
        // Firefox first: its cookie DB is readable while the browser is open.
        if (Directory.Exists(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"))) list.Add("firefox");
        if (Directory.Exists(Path.Combine(local, "Google", "Chrome", "User Data"))) list.Add("chrome");
        if (Directory.Exists(Path.Combine(local, "Microsoft", "Edge", "User Data"))) list.Add("edge");
        if (Directory.Exists(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"))) list.Add("brave");
        return list;
    }

    private async Task<DownloadResult> DownloadCoreAsync(
        string url,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string? cookies)
    {
        var source = SourceDetector.Detect(url);
        if (source is SourceType.Unknown or SourceType.Torrent)
            throw new InvalidOperationException($"yt-dlp does not support: {url}");

        Directory.CreateDirectory(OutputDir);

        var isHls = source == SourceType.Hls || CurlCommandParser.IsHlsUrl(url);
        // Prefer native HLS for .vl / googlevideo-style manifests (segment list → ffmpeg merge).
        if (isHls)
        {
            try
            {
                return await HlsPlaylistDownloader.DownloadAsync(
                    url, OutputDir, Headers, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ErrorLog.Write($"HLS native failed, fallback yt-dlp: {ex.Message}");
            }
        }

        if (IsFakePng(url) && !isHls)
            return await DownloadFakePngAsync(url, source, progress, ct).ConfigureAwait(false);

        var exe = ResolveYtDlp();
        var outTpl = isHls
            ? Path.Combine(OutputDir, "hls_%(epoch)s.%(ext)s")
            : Path.Combine(OutputDir, "%(title).200B [%(id)s].%(ext)s");
        var args = new StringBuilder();
        args.Append("-o ").Append(Quote(outTpl));
        args.Append(" --newline --no-colors");
        args.Append(" --retries 3 --fragment-retries 10");
        args.Append(" --concurrent-fragments 4");

        // Never select HEVC/H.265 (or AV1). If only those exist, transcode to H.264.
        const string h264Format =
            "bv*[vcodec^=avc1][height<=1080]+ba[acodec^=mp4a]/" +
            "bv*[vcodec^=avc][height<=1080]+ba/" +
            "bv*[vcodec^=avc1]+ba[acodec^=mp4a]/" +
            "bv*[vcodec^=avc]+ba/" +
            "b[vcodec^=avc]/" +
            "bv*[vcodec!*=hevc][vcodec!*=hev1][vcodec!*=hvc1][vcodec!*=av01]+ba/" +
            "b[vcodec!*=hevc][vcodec!*=hev1][vcodec!*=hvc1][vcodec!*=av01]/" +
            "bv*+ba/b";

        if (AudioOnly)
        {
            if (!isHls)
                args.Append(" --no-playlist");
            args.Append(" -x --audio-format mp3 --audio-quality 192");
        }
        else
        {
            if (!isHls)
                args.Append(" --no-playlist");
            args.Append(" -f \"").Append(h264Format).Append('"');
            args.Append(" --merge-output-format mp4");
            args.Append(" -S vcodec:h264,res,acodec:aac");
        }

        if (!string.IsNullOrWhiteSpace(cookies) && CookieOk.Contains(source)
            && !string.Equals(cookies.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (cookies.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                args.Append(" --cookies ").Append(Quote(cookies[5..]));
            else
                args.Append(" --cookies-from-browser ").Append(cookies.Trim());
        }

        if (!string.IsNullOrWhiteSpace(Proxy))
            args.Append(" --proxy ").Append(Quote(Proxy.Trim()));

        foreach (var (key, value) in Headers)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            // Skip empty header values (e.g. curl -H 'Referer;')
            if (string.IsNullOrEmpty(value) &&
                !key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                continue;
            args.Append(" --add-header ").Append(Quote($"{key}: {value}"));
        }

        if (!Headers.ContainsKey("User-Agent") && isHls)
        {
            args.Append(" --add-header ").Append(Quote(
                "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"));
        }

        var printJson = Path.Combine(Path.GetTempPath(), $"mdm_{Guid.NewGuid():N}.json");
        args.Append(" --print-json");
        args.Append(' ').Append(Quote(url));

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var tcs = new TaskCompletionSource<int>();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            if (e.Data.TrimStart().StartsWith('{')) return;
            ReportProgressLine(e.Data, progress);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            ReportProgressLine(e.Data, progress);
        };

        if (!proc.Start())
            throw new InvalidOperationException("Failed to start yt-dlp");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        });

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        if (proc.ExitCode != 0)
        {
            var err = TrimErr(stderr.ToString());
            ErrorLog.Write($"yt-dlp exit {proc.ExitCode} {url}\n{err}");
            throw new InvalidOperationException($"yt-dlp failed ({proc.ExitCode}): {err}");
        }

        var jsonLine = FindLastJson(stdout.ToString());
        string? title = null, id = null, filepath = null;
        double? duration = null;
        string? uploader = null;

        if (jsonLine is not null)
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
            duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDouble() : null;
            uploader = root.TryGetProperty("uploader", out var u) ? u.GetString()
                : root.TryGetProperty("channel", out var c) ? c.GetString() : null;
            filepath = root.TryGetProperty("filename", out var f) ? f.GetString()
                : root.TryGetProperty("_filename", out var f2) ? f2.GetString() : null;
            if (root.TryGetProperty("requested_downloads", out var rd) &&
                rd.ValueKind == JsonValueKind.Array && rd.GetArrayLength() > 0 &&
                rd[0].TryGetProperty("filepath", out var fp))
                filepath = fp.GetString() ?? filepath;
        }

        filepath ??= FindNewestFile(OutputDir, id);
        long? size = null;
        if (filepath is not null && File.Exists(filepath))
            size = new FileInfo(filepath).Length;

        progress?.Report(new DownloadProgress { Status = "finished", Progress = 100, Filename = filepath });

        try { if (File.Exists(printJson)) File.Delete(printJson); } catch { /* ignore */ }

        return new DownloadResult
        {
            Source = source.ToString().ToLowerInvariant(),
            Url = url,
            Title = title,
            MediaId = id,
            Filepath = filepath,
            Files = filepath is null ? [] : [filepath],
            Filesize = size,
            Duration = duration,
            Uploader = uploader
        };
    }

    private static bool IsFakePng(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<DownloadResult> DownloadFakePngAsync(
        string url,
        SourceType source,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var name = "clip.png";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var leaf = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(leaf))
                name = string.Join("_", leaf.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        }
        if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            name += ".png";

        var dest = Path.Combine(OutputDir, name);
        if (File.Exists(dest))
            dest = Path.Combine(OutputDir, Path.GetFileNameWithoutExtension(name) + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".png");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        foreach (var (key, value) in Headers)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value)) continue;
            http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(dest);
        var buffer = new byte[1024 * 128];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            var pct = total is > 0 ? Math.Min(99, read * 100.0 / total.Value) : 0;
            progress?.Report(new DownloadProgress { Status = "downloading", Progress = pct, Filename = dest });
        }

        progress?.Report(new DownloadProgress { Status = "finished", Progress = 100, Filename = dest });
        return new DownloadResult
        {
            Source = source.ToString().ToLowerInvariant(),
            Url = url,
            Title = Path.GetFileNameWithoutExtension(dest),
            Filepath = dest,
            Files = [dest],
            Filesize = new FileInfo(dest).Length
        };
    }

    private string ResolveYtDlp()
    {
        if (!string.IsNullOrWhiteSpace(YtDlpPath) && File.Exists(YtDlpPath))
            return YtDlpPath;

        var local = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (File.Exists(local)) return local;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), "yt-dlp.exe");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.Trim('"'), "yt-dlp");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "yt-dlp not found. Place yt-dlp.exe next to the app or on PATH.");
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";

    private static string TrimErr(string s)
    {
        s = string.Join('\n', s.Split('\n')
            .Where(l => !l.Contains("Deprecated Feature", StringComparison.OrdinalIgnoreCase)));
        s = s.Trim();
        return s.Length > 400 ? s[^400..] : s;
    }

    private static string? FindLastJson(string stdout)
    {
        string? last = null;
        using var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } line)
        {
            var t = line.Trim();
            if (t.StartsWith('{') && t.EndsWith('}'))
                last = t;
        }
        return last;
    }

    private static string? FindNewestFile(string dir, string? id)
    {
        if (!Directory.Exists(dir)) return null;
        var files = Directory.EnumerateFiles(dir)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        if (id is not null)
        {
            var match = files.FirstOrDefault(f => f.Name.Contains(id, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.FullName;
        }
        return files.FirstOrDefault()?.FullName;
    }

    private static void ReportProgressLine(string line, IProgress<DownloadProgress>? progress)
    {
        if (progress is null) return;
        double? pct = null;
        double speed = 0;
        string? eta = null;

        var m = ProgressRe.Match(line);
        if (m.Success)
        {
            if (double.TryParse(m.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                pct = p;
            speed = ParseSpeedToBytes(m.Groups["speed"].Value);
            if (m.Groups["eta"].Success)
                eta = m.Groups["eta"].Value;
        }
        else
        {
            m = ProgressPctOnlyRe.Match(line);
            if (m.Success &&
                double.TryParse(m.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p2))
                pct = p2;
        }
        if (pct is null) return;

        progress.Report(new DownloadProgress
        {
            Status = "downloading",
            Progress = pct.Value,
            Speed = speed,
            Eta = eta,
            Filename = line
        });
    }

    /// <summary>Parse yt-dlp speed like 1.23MiB / 450KiB / 12.5MB into bytes/sec.</summary>
    public static double ParseSpeedToBytes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        raw = raw.Trim();
        var numMatch = Regex.Match(raw, @"^([\d.]+)");
        if (!numMatch.Success ||
            !double.TryParse(numMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return 0;

        var unit = raw[numMatch.Length..].Trim().ToUpperInvariant();
        // yt-dlp uses binary prefixes: KiB/MiB/GiB (1024) and sometimes KB/MB
        return unit switch
        {
            "B" => n,
            "KB" or "KIB" or "K" => n * 1024,
            "MB" or "MIB" or "M" => n * 1024 * 1024,
            "GB" or "GIB" or "G" => n * 1024 * 1024 * 1024,
            _ => n
        };
    }
}

public sealed class DownloadProgress
{
    public string Status { get; set; } = "";
    public double Progress { get; set; }
    public double Speed { get; set; }
    public string? Eta { get; set; }
    public string? Filename { get; set; }
    public string? Name { get; set; }
}

public sealed class DownloadResult
{
    public string Source { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Filepath { get; set; }
    public List<string> Files { get; set; } = [];
    public long? Filesize { get; set; }
    public double? Duration { get; set; }
    public string? Uploader { get; set; }
    public string? MediaId { get; set; }
    public bool Ok => !string.IsNullOrEmpty(Filepath) || Files.Count > 0;
}
