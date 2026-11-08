using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MediaDownloadManager.Core;
using MediaDownloadManager.Models;

namespace MediaDownloadManager;

public sealed class PreviewRow
{
    public string Source { get; init; } = "";
    public string Url { get; init; } = "";
    public string Status { get; init; } = "";
}

public partial class AddUrlsWindow : Window
{
    private readonly HashSet<string> _existing;
    private readonly ObservableCollection<PreviewRow> _rows = [];
    private List<ParsedDownloadRequest> _newRequests = [];

    public IReadOnlyList<string> Urls { get; private set; } = [];
    public IReadOnlyList<ParsedDownloadRequest> Requests { get; private set; } = [];
    public bool AutoStart { get; private set; } = true;

    public AddUrlsWindow() : this([]) { }

    public AddUrlsWindow(IEnumerable<string> existingUrls)
    {
        _existing = new HashSet<string>(existingUrls, StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        PreviewGrid.ItemsSource = _rows;
    }

    private static string ReadClipboard()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() ?? "" : ""; }
        catch { return ""; }
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        var text = UrlBox.Text;
        var all = UrlListParser.ExtractRequests(text);

        _rows.Clear();
        _newRequests = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var already = 0;

        foreach (var r in all)
        {
            var url = r.Url.Trim();
            string status;
            if (_existing.Contains(url)) { status = "Already in list"; already++; }
            else if (!seen.Add(url)) { status = "Duplicate in input"; }
            else { status = "New"; _newRequests.Add(r); }

            var src = r.IsHls ? SourceType.Hls : SourceDetector.Detect(url);
            _rows.Add(new PreviewRow { Source = SourceLabel(src), Url = url, Status = status });
        }

        var ignored = 0;
        if (!string.IsNullOrWhiteSpace(text) && !CurlCommandParser.LooksLikeCurl(text))
        {
            ignored = text.Split('\n')
                .Select(l => l.Trim())
                .Count(l => l.Length > 0
                            && !all.Any(r => l.Contains(r.Url, StringComparison.OrdinalIgnoreCase)));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            CountText.Text = "No links";
        }
        else
        {
            var parts = new List<string> { $"{_newRequests.Count} new" };
            if (already > 0) parts.Add($"{already} already in list");
            if (ignored > 0) parts.Add($"{ignored} line(s) not recognized");
            CountText.Text = string.Join(", ", parts);
        }

        EmptyText.Text = string.IsNullOrWhiteSpace(text)
            ? "No links yet.\nSupported: YouTube, TikTok, Instagram, Facebook, torrent / magnet, HLS (.m3u8), curl commands, direct http(s) links."
            : "No supported link found in the text above.";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var canAdd = _newRequests.Count > 0;
        AddStartBtn.IsEnabled = canAdd;
        AddPausedBtn.IsEnabled = canAdd;
    }

    private static string SourceLabel(SourceType s) => s switch
    {
        SourceType.Youtube => "YouTube",
        SourceType.Tiktok => "TikTok",
        SourceType.Hls => "HLS",
        SourceType.Generic => "Direct link",
        _ => s.ToString()
    };

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        var t = ReadClipboard();
        if (UrlListParser.ExtractRequests(t).Count == 0)
        {
            CountText.Text = "Clipboard has no supported link";
            return;
        }
        if (UrlBox.Text.Length > 0 && !UrlBox.Text.EndsWith('\n')) UrlBox.AppendText("\n");
        UrlBox.AppendText(t.Trim());
        UrlBox.CaretIndex = UrlBox.Text.Length;
        UrlBox.Focus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        UrlBox.Clear();
        UrlBox.Focus();
    }

    private void AddStart_Click(object sender, RoutedEventArgs e) => Finish(true);

    private void AddPaused_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool autoStart)
    {
        if (_newRequests.Count == 0) return;
        Requests = _newRequests;
        Urls = Requests.Select(r => r.Url).ToList();
        AutoStart = autoStart;
        DialogResult = true;
        Close();
    }
}
