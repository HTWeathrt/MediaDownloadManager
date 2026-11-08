using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaDownloadManager.Core;

public sealed class ConvertProgress
{
    public double Percent { get; init; }
    public string? Message { get; init; }
}

public sealed class ConvertResult
{
    public required string OutputPath { get; init; }
    public string? VideoCodec { get; init; }
    public bool Converted { get; init; }
    public bool AlreadyCompatible { get; init; }
}

public static class FfmpegConverter
{
    private static readonly Regex TimeRe = new(
        @"time=(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    public static string? FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            var c = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
            if (File.Exists(c)) return c;
            c = Path.Combine(dir.Trim('"'), "ffmpeg");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static string? FindFfprobe()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return null;
        var dir = Path.GetDirectoryName(ffmpeg) ?? "";
        var probe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (File.Exists(probe)) return probe;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator))
        {
            var c = Path.Combine(d.Trim('"'), OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static async Task<string?> DetectVideoCodecAsync(string file, CancellationToken ct = default)
    {
        var probe = FindFfprobe();
        if (probe is null) return null;

        var psi = new ProcessStartInfo
        {
            FileName = probe,
            Arguments = $"-v quiet -select_streams v:0 -show_entries stream=codec_name -of json {Quote(file)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var json = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.GetArrayLength() > 0
                && streams[0].TryGetProperty("codec_name", out var name))
                return name.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    public static bool IsHevc(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return false;
        var c = codec.Trim().ToLowerInvariant();
        return c.Contains("hevc") || c.Contains("h265") || c.Contains("hev1") || c.Contains("hvc1");
    }

    public static bool IsH264Compatible(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return false;
        var c = codec.Trim().ToLowerInvariant();
        return c.Contains("h264") || c.Contains("avc1") || c is "avc";
    }

    public static async Task<double?> GetDurationSecondsAsync(string file, CancellationToken ct = default)
    {
        var probe = FindFfprobe();
        if (probe is null) return null;
        var psi = new ProcessStartInfo
        {
            FileName = probe,
            Arguments = $"-v quiet -show_entries format=duration -of csv=p=0 {Quote(file)}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var text = (await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>
    /// Convert video to H.264/AAC MP4 for broad Windows playback.
    /// Skips if already H.264 unless force=true.
    /// </summary>
    public static async Task<ConvertResult> ConvertToH264Async(
        string inputPath,
        string? outputPath = null,
        bool force = false,
        bool replaceOriginal = false,
        IProgress<ConvertProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Input video not found", inputPath);

        var ffmpeg = FindFfmpeg()
            ?? throw new FileNotFoundException("ffmpeg not found on PATH");

        if (inputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return new ConvertResult
            {
                OutputPath = await RemuxPngToMp4Async(inputPath, ct).ConfigureAwait(false),
                VideoCodec = "h264",
                Converted = true
            };

        var codec = await DetectVideoCodecAsync(inputPath, ct).ConfigureAwait(false);
        if (IsH264Compatible(codec) && !IsHevc(codec))
        {
            progress?.Report(new ConvertProgress { Percent = 100, Message = "Already H.264" });
            return new ConvertResult
            {
                OutputPath = inputPath,
                VideoCodec = codec,
                Converted = false,
                AlreadyCompatible = true
            };
        }

        outputPath ??= BuildOutputPath(inputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var duration = await GetDurationSecondsAsync(inputPath, ct).ConfigureAwait(false);

        // No video stream (audio-only / empty HLS): remux, don't open libx264 on 0 frames.
        var input = Quote(inputPath);
        var output = Quote(outputPath);
        var args = codec is null
            ? $"-y -i {input} -map 0 -c copy -movflags +faststart {output}"
            : $"-y -i {input} -map 0:v:0 -map 0:a? -c:v libx264 -preset veryfast -crf 23 " +
              $"-pix_fmt yuv420p -profile:v high -level 4.1 " +
              $"-c:a aac -b:a 192k -ac 2 -movflags +faststart {output}";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var err = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            err.AppendLine(e.Data);
            var m = TimeRe.Match(e.Data);
            if (!m.Success || duration is null or <= 0) return;
            var h = double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            var min = double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            var s = double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
            var t = h * 3600 + min * 60 + s;
            var pct = Math.Min(99.0, t / duration.Value * 100.0);
            progress?.Report(new ConvertProgress { Percent = pct, Message = e.Data });
        };

        if (!proc.Start())
            throw new InvalidOperationException("Failed to start ffmpeg");

        proc.BeginErrorReadLine();
        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        });

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        if (proc.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException($"ffmpeg convert failed: {Trim(err.ToString())}");

        if (replaceOriginal)
        {
            try
            {
                File.Delete(inputPath);
                File.Move(outputPath, inputPath);
                outputPath = inputPath;
            }
            catch
            {
                // keep sidecar output if replace fails
            }
        }

        progress?.Report(new ConvertProgress { Percent = 100, Message = "Done" });
        return new ConvertResult
        {
            OutputPath = outputPath,
            VideoCodec = "h264",
            Converted = true,
            AlreadyCompatible = false
        };
    }

    /// <summary>
    /// PNG files from this CDN wrap an MPEG-TS payload after the IEND chunk.
    /// Returns a sibling .ts path, or null when the file is a real image.
    /// </summary>
    public static string? ExtractTsFromPng(string pngPath)
    {
        var data = File.ReadAllBytes(pngPath);
        if (data.Length < 16 || data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47)
            return null;

        var iend = new byte[] { 0x49, 0x45, 0x4E, 0x44 };
        var at = -1;
        for (var i = 8; i <= data.Length - 4; i++)
        {
            if (data[i] == iend[0] && data[i + 1] == iend[1] && data[i + 2] == iend[2] && data[i + 3] == iend[3])
            {
                at = i;
                break;
            }
        }
        if (at < 0) return null;
        var start = at + 8; // IEND + CRC
        if (start >= data.Length - 188) return null;

        var tsPath = Path.Combine(Path.GetDirectoryName(pngPath) ?? ".", Path.GetFileNameWithoutExtension(pngPath) + ".ts");
        File.WriteAllBytes(tsPath, data.AsSpan(start).ToArray());
        return tsPath;
    }

    /// <summary>
    /// Remux a mislabeled MPEG-TS file (often saved as .png) to .mp4 without re-encoding.
    /// ffmpeg -f mpegts -i input -c copy -map 0 output.mp4
    /// </summary>
    public static async Task<string> RemuxPngToMp4Async(string inputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Input file not found", inputPath);

        var ffmpeg = FindFfmpeg()
            ?? throw new FileNotFoundException("ffmpeg not found on PATH");

        var extracted = ExtractTsFromPng(inputPath);
        var source = extracted ?? inputPath;

        var dir = Path.GetDirectoryName(inputPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var outputPath = Path.Combine(dir, name + ".mp4");
        if (File.Exists(outputPath))
            outputPath = Path.Combine(dir, name + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".mp4");

        var args = $"-y -f mpegts -i {Quote(source)} -c copy -map 0 -movflags +faststart {Quote(outputPath)}";
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new InvalidOperationException("Failed to start ffmpeg");

        var err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        if (proc.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException($"png→mp4 remux failed: {Trim(err)}");

        try { File.Delete(inputPath); } catch { /* keep source if locked */ }
        if (extracted is not null) { try { File.Delete(extracted); } catch { /* ignore */ } }
        return outputPath;
    }

    private static string Quote(string path)
    {
        var full = Path.GetFullPath(path);
        return "\"" + full.Replace("\"", "\\\"") + "\"";
    }

    private static string BuildOutputPath(string input)
    {
        var dir = Path.GetDirectoryName(input) ?? ".";
        var name = Path.GetFileNameWithoutExtension(input);
        if (name.EndsWith(".h264", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_h264", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(dir, name + ".mp4");
        return Path.Combine(dir, name + "_h264.mp4");
    }

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length > 500 ? s[^500..] : s;
    }
}
