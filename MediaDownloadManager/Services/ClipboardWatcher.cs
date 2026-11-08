using System.Windows;
using System.Windows.Threading;
using MediaDownloadManager.Core;

namespace MediaDownloadManager.Services;

/// <summary>
/// Polls clipboard for new media URLs and raises when found.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private string _lastText = "";
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public event Action<IReadOnlyList<string>>? UrlsDetected;

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value) _timer.Start();
            else _timer.Stop();
        }
    }

    /// <summary>All / Youtube / Tiktok / Instagram / Facebook / Torrent / Generic</summary>
    public string Mode { get; set; } = "All";

    public ClipboardWatcher(Dispatcher dispatcher, TimeSpan? interval = null)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = interval ?? TimeSpan.FromMilliseconds(800)
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void ClearSeen() => _seen.Clear();

    /// <summary>Mark the current clipboard text as handled so it is not auto-captured later.</summary>
    public void SkipCurrent()
    {
        try
        {
            if (Clipboard.ContainsText())
                _lastText = Clipboard.GetText() ?? "";
        }
        catch { /* clipboard busy */ }
    }

    private void Poll()
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText() ?? "";
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text) || text == _lastText) return;
        _lastText = text;

        var reqs = UrlListParser.ExtractRequests(text)
            .Where(r => MatchesMode(r.Url))
            .Where(r => _seen.Add(r.Url))
            .ToList();
        if (reqs.Count == 0) return;
        RequestsDetected?.Invoke(reqs);

        var urls = reqs.Select(r => r.Url).ToList();
        if (urls.Count > 0) UrlsDetected?.Invoke(urls);
    }

    public event Action<IReadOnlyList<ParsedDownloadRequest>>? RequestsDetected;

    private bool MatchesMode(string url)
    {
        var src = SourceDetector.Detect(url);
        return Mode switch
        {
            "Youtube" => src == Models.SourceType.Youtube,
            "Tiktok" => src == Models.SourceType.Tiktok,
            "Instagram" => src == Models.SourceType.Instagram,
            "Facebook" => src == Models.SourceType.Facebook,
            "Torrent" => src == Models.SourceType.Torrent,
            "Hls" => src == Models.SourceType.Hls,
            "Generic" => src == Models.SourceType.Generic || src == Models.SourceType.Hls,
            _ => src != Models.SourceType.Unknown
        };
    }

    public void Dispose() => _timer.Stop();
}
