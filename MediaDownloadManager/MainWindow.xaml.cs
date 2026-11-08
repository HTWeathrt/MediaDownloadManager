using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MediaDownloadManager.Core;
using MediaDownloadManager.Models;
using MediaDownloadManager.Services;
using Microsoft.Win32;

namespace MediaDownloadManager;

public partial class MainWindow : Window
{
    private readonly QueueWorker _worker;
    private readonly ClipboardWatcher _clipWatcher;
    private bool _ready;
    private string _filter = "All";
    private bool _syncingFilters;
    private bool _loginPrompted;
    private ICollectionView? _view;

    public MainWindow()
    {
        _worker = new QueueWorker(Dispatcher);
        _clipWatcher = new ClipboardWatcher(Dispatcher);

        InitializeComponent();

        var defaultOut = Path.Combine(Environment.CurrentDirectory, "downloads");
        Directory.CreateDirectory(defaultOut);
        OutputBox.Text = defaultOut;

        _worker.Configure(defaultOut, false, null);
        _worker.Start();

        _view = CollectionViewSource.GetDefaultView(_worker.Items);
        _view.Filter = FilterItem;
        QueueList.ItemsSource = _view;

        ((INotifyCollectionChanged)_worker.Items).CollectionChanged += (_, _) => RefreshStatus();
        RebuildFilters();
        _worker.LoginRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (_loginPrompted) return;
            _loginPrompted = true;
            var ask = MessageBox.Show(this,
                "YouTube requires you to sign in and no browser cookies could be read.\n\n" +
                "Open the in-app sign-in window now?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes) Login_Click(this, new RoutedEventArgs());
            _loginPrompted = false;
        });
        LogBox.Text = ErrorLog.Text;
        ErrorLog.Appended += line => Dispatcher.BeginInvoke(() =>
        {
            if (string.IsNullOrEmpty(line)) { LogBox.Clear(); LogTab.Header = "Error log"; return; }
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
            LogTab.Header = "Error log ●";
            BottomTabs.SelectedItem = LogTab;
        });
        CheckDeps();

        _clipWatcher.RequestsDetected += OnAutoRequests;
        _clipWatcher.Mode = "All";
        if (AutoClipCheck.IsChecked == true)
            _clipWatcher.Start();

        _ready = true;
        ApplySettings();
        Closed += (_, _) =>
        {
            _clipWatcher.Dispose();
            _worker.Shutdown();
        };
    }

    private void ShowLog_Click(object sender, RoutedEventArgs e) => LogBox.Focus();

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var win = new LoginWindow { Owner = this };
        if (win.ShowDialog() == true)
            StatusText.Text = "Login saved - select failed items and press Start / retry";
    }

    private void ForgetLogin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(CookieStore.FilePath)) File.Delete(CookieStore.FilePath);
            if (Directory.Exists(CookieStore.ProfileDir)) Directory.Delete(CookieStore.ProfileDir, true);
            StatusText.Text = "Saved login removed";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(LogBox.Text))
            Clipboard.SetText(LogBox.Text);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ErrorLog.Clear();

    private void OnAutoRequests(IReadOnlyList<ParsedDownloadRequest> reqs)
    {
        if (!_ready || AutoClipCheck.IsChecked != true) return;
        ApplySettings();
        var added = _worker.AddRequests(reqs, autoStart: true);
        if (added.Count > 0)
        {
            var hls = added.Count(i => i.Source == SourceType.Hls);
            StatusText.Text = hls > 0
                ? $"Auto-captured {added.Count} ({hls} HLS) · {_clipWatcher.Mode}"
                : $"Auto-captured {added.Count} · {_clipWatcher.Mode}";
            RefreshStatus();
        }
    }

    private void AutoClip_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var on = AutoClipCheck.IsChecked == true;
        // Ignore whatever is already in the clipboard; only react to links copied after enabling.
        if (on) _clipWatcher.SkipCurrent();
        _clipWatcher.Enabled = on;
        StatusText.Text = on ? $"Auto-add links ON · {_clipWatcher.Mode}" : "Auto-add links OFF";
    }

    private void AutoMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (AutoModeBox.SelectedItem is ComboBoxItem item)
            _clipWatcher.Mode = item.Tag?.ToString() ?? "All";
        StatusText.Text = $"Auto mode: {_clipWatcher.Mode}";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingFilters) return;
        if (FilterList.SelectedItem is ListBoxItem item)
            _filter = item.Tag?.ToString() ?? "All";
        _view?.Refresh();
        RefreshStatus();
    }

    private void RebuildFilters()
    {
        var items = _worker.Items.ToList();
        var filters = new List<(string Label, string Tag)>
        {
            ("All", "All"),
            ("Downloading", "Downloading"),
            ("Paused", "Paused"),
            ("Completed", "Completed"),
            ("Errors", "Errors")
        };
        foreach (var src in items.Select(i => i.Source).Distinct())
            filters.Add((SourceLabel(src), src.ToString()));

        var tags = filters.Select(f => f.Tag).ToList();
        var current = FilterList.Items.Cast<ListBoxItem>().Select(i => i.Tag?.ToString()).ToList();
        if (current.SequenceEqual(tags)) return;

        if (!tags.Contains(_filter)) _filter = "All";
        _syncingFilters = true;
        FilterList.Items.Clear();
        foreach (var (label, tag) in filters)
        {
            var item = new ListBoxItem { Content = label, Tag = tag };
            FilterList.Items.Add(item);
            if (tag == _filter) FilterList.SelectedItem = item;
        }
        _syncingFilters = false;
    }

    private static string SourceLabel(SourceType src) => src switch
    {
        SourceType.Youtube => "YouTube",
        SourceType.Tiktok => "TikTok",
        SourceType.Instagram => "Instagram",
        SourceType.Facebook => "Facebook",
        SourceType.Hls => "HLS",
        SourceType.Generic => "Generic",
        _ => src.ToString()
    };

    private void CheckDeps()
    {
        var yt = FindOnPath("yt-dlp.exe") ?? FindOnPath("yt-dlp");
        var ff = FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
        StatusText.Text = "Ready";
        DepsText.Text = $"yt-dlp: {(yt is null ? "missing" : "OK")}   ffmpeg: {(ff is null ? "missing (HLS/convert)" : "OK")}";
        if (yt is not null) _worker.YtDlpPath = yt;
    }

    private static string? FindOnPath(string name)
    {
        var local = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(local)) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            var c = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private bool FilterItem(object obj)
    {
        if (obj is not DownloadItem item) return false;

        var q = SearchBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(q)
            && !item.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            && !item.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
            return false;

        return _filter switch
        {
            "Downloading" => item.State is ItemState.Queued or ItemState.Running,
            "Paused" => item.State == ItemState.Paused,
            "Completed" => item.State == ItemState.Done,
            "Errors" => item.State is ItemState.Failed or ItemState.Cancelled,
            "Youtube" => item.Source == SourceType.Youtube,
            "Tiktok" => item.Source == SourceType.Tiktok,
            "Instagram" => item.Source == SourceType.Instagram,
            "Facebook" => item.Source == SourceType.Facebook,
            "Torrent" => item.Source == SourceType.Torrent,
            "Hls" => item.Source == SourceType.Hls,
            "Generic" => item.Source == SourceType.Generic,
            _ => true
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _view?.Refresh();
        RefreshStatus();
    }

    private List<DownloadItem> Selected()
        => QueueList?.SelectedItems?.OfType<DownloadItem>().ToList() ?? [];

    private void HasSelection_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = QueueList?.SelectedItems is { Count: > 0 };

    private void HasFile_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = QueueList?.SelectedItem is DownloadItem { State: ItemState.Done } i
                          && !string.IsNullOrEmpty(i.Filepath) && File.Exists(i.Filepath);

    private void StartSelected_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        ApplySettings();
        _worker.Requeue(Selected().Select(i => i.Id));
        RefreshStatus();
    }

    private void OpenFile_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not DownloadItem i || string.IsNullOrEmpty(i.Filepath) || !File.Exists(i.Filepath))
            return;
        try { Process.Start(new ProcessStartInfo { FileName = i.Filepath, UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ShowInFolder_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not DownloadItem i) return;
        try
        {
            if (!string.IsNullOrEmpty(i.Filepath) && File.Exists(i.Filepath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{i.Filepath}\"") { UseShellExecute = true });
            else
            {
                Directory.CreateDirectory(i.OutputDir);
                Process.Start(new ProcessStartInfo { FileName = i.OutputDir, UseShellExecute = true });
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void CopyUrl_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var urls = string.Join(Environment.NewLine, Selected().Select(i => i.Url));
        if (urls.Length == 0) return;
        try { Clipboard.SetText(urls); }
        catch { /* clipboard busy */ }
    }

    private async void ConvertHevc_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var items = Selected()
            .Where(i => i.State == ItemState.Done && !string.IsNullOrEmpty(i.Filepath) && File.Exists(i.Filepath))
            .ToList();
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select a completed download with a video file.", Title);
            return;
        }
        if (FfmpegConverter.FindFfmpeg() is null)
        {
            MessageBox.Show(this, "ffmpeg not found on PATH.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var item in items)
            await ConvertItemAsync(item, force: false).ConfigureAwait(true);
    }

    private async void ConvertFile_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (FfmpegConverter.FindFfmpeg() is null)
        {
            MessageBox.Show(this, "ffmpeg not found on PATH.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ofd = new OpenFileDialog
        {
            Title = "Select video to convert to H.264",
            Filter = "Video|*.mp4;*.mkv;*.webm;*.mov;*.ts;*.m4v;*.avi;*.png|All|*.*"
        };
        if (ofd.ShowDialog() != true) return;

        StatusText.Text = "Converting…";
        try
        {
            var progress = new Progress<ConvertProgress>(p =>
            {
                StatusText.Text = $"Converting… {p.Percent:0.0}%";
            });
            var result = await FfmpegConverter.ConvertToH264Async(
                ofd.FileName,
                force: true,
                progress: progress).ConfigureAwait(true);
            StatusText.Text = result.AlreadyCompatible
                ? "Already H.264 — no convert needed"
                : $"Converted → {result.OutputPath}";
            MessageBox.Show(this,
                result.AlreadyCompatible
                    ? "File is already H.264 compatible."
                    : $"Saved:\n{result.OutputPath}",
                Title);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Convert failed";
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ConvertItemAsync(DownloadItem item, bool force)
    {
        var path = item.Filepath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        item.Error = "Converting HEVC→H.264…";
        item.Progress = 0;
        StatusText.Text = $"Converting {Path.GetFileName(path)}…";

        try
        {
            var codec = await FfmpegConverter.DetectVideoCodecAsync(path).ConfigureAwait(true);
            if (!force && !FfmpegConverter.IsHevc(codec) && FfmpegConverter.IsH264Compatible(codec))
            {
                item.Error = "";
                item.Progress = 100;
                StatusText.Text = "Already H.264";
                return;
            }

            var progress = new Progress<ConvertProgress>(p =>
            {
                item.Progress = p.Percent;
                item.Error = $"Converting… {p.Percent:0.0}%";
            });

            var result = await FfmpegConverter.ConvertToH264Async(
                path,
                force: force || FfmpegConverter.IsHevc(codec),
                progress: progress).ConfigureAwait(true);

            if (result.Converted)
            {
                item.Filepath = result.OutputPath;
                item.Error = "";
                item.Progress = 100;
                StatusText.Text = $"Converted → {Path.GetFileName(result.OutputPath)}";
            }
            else
            {
                item.Error = "";
                item.Progress = 100;
                StatusText.Text = "Already compatible";
            }
        }
        catch (Exception ex)
        {
            item.Error = $"Convert failed: {ex.Message}";
            StatusText.Text = "Convert failed";
        }
    }

    private void PriorityHigh_Executed(object sender, ExecutedRoutedEventArgs e)
        => SetSelectedPriority(DownloadPriority.High);

    private void PriorityNormal_Executed(object sender, ExecutedRoutedEventArgs e)
        => SetSelectedPriority(DownloadPriority.Normal);

    private void PriorityLow_Executed(object sender, ExecutedRoutedEventArgs e)
        => SetSelectedPriority(DownloadPriority.Low);

    private void SetSelectedPriority(DownloadPriority priority)
    {
        var ids = Selected().Select(i => i.Id).ToList();
        if (ids.Count == 0) return;
        _worker.SetPriority(ids, priority);
        StatusText.Text = $"Priority → {priority} ({ids.Count} item(s))";
        RefreshStatus();
    }

    private void Exit_Executed(object sender, ExecutedRoutedEventArgs e) => Close();

    private void QueueList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is DownloadItem { State: ItemState.Done })
            OpenFile_Executed(sender, null!);
    }

    private void QueueList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ApplySettings()
    {
        if (!_ready) return;
        var cookies = (CookiesBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (cookies is "(none)" or null) cookies = null;
        var outDir = string.IsNullOrWhiteSpace(OutputBox.Text)
            ? Path.Combine(Environment.CurrentDirectory, "downloads")
            : OutputBox.Text.Trim();
        Directory.CreateDirectory(outDir);
        _worker.Configure(
            outDir,
            AudioOnlyCheck.IsChecked == true,
            cookies,
            AutoConvertHevcCheck.IsChecked == true);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (!_ready) return;
        var items = _worker.Items.ToList();
        var active = items.Count(i => i.State == ItemState.Running);
        var done = items.Count(i => i.State == ItemState.Done);
        var failed = items.Count(i => i.State == ItemState.Failed);
        SummaryText.Text = $"Total {items.Count}   Active {active}   Done {done}   Failed {failed}";
        RebuildFilters();
        _view?.Refresh();
    }

    private void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        // Pause auto-capture while the dialog is open so the clipboard is not consumed behind it.
        var watcherWasOn = _clipWatcher.Enabled;
        _clipWatcher.Enabled = false;
        bool? result;
        AddUrlsWindow dlg;
        try
        {
            dlg = new AddUrlsWindow(_worker.Items.Select(i => i.Url)) { Owner = this };
            result = dlg.ShowDialog();
        }
        finally
        {
            _clipWatcher.SkipCurrent();
            _clipWatcher.Enabled = watcherWasOn;
        }
        if (result != true) return;
        ApplySettings();
        var added = _worker.AddRequests(dlg.Requests, dlg.AutoStart);
        StatusText.Text = added.Count == 1 ? "Added 1 download" : $"Added {added.Count} downloads";
        RefreshStatus();
    }

    private void Clipboard_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.GetText() ?? ""; }
        catch { text = ""; }
        var reqs = UrlListParser.ExtractRequests(text);
        if (reqs.Count == 0)
        {
            MessageBox.Show("No valid URLs / curl / magnets in clipboard.", Title);
            return;
        }
        ApplySettings();
        var added = _worker.AddRequests(reqs, true);
        StatusText.Text = $"Added {added.Count} from clipboard";
        RefreshStatus();
    }

    private void ListFile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "URL / curl list file",
            Filter = "Text|*.txt;*.list;*.csv|All|*.*"
        };
        if (ofd.ShowDialog() != true) return;
        try
        {
            var text = File.ReadAllText(ofd.FileName);
            var reqs = UrlListParser.ExtractRequests(text);
            if (reqs.Count == 0) reqs = UrlListParser.LoadListFile(ofd.FileName)
                .Select(u => new ParsedDownloadRequest { Url = u, IsHls = CurlCommandParser.IsHlsUrl(u) })
                .ToList();
            if (reqs.Count == 0)
            {
                MessageBox.Show("File has no valid URLs.", Title);
                return;
            }
            ApplySettings();
            var added = _worker.AddRequests(reqs, true);
            StatusText.Text = $"Loaded {added.Count} from {Path.GetFileName(ofd.FileName)}";
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        _worker.StartAll();
        StatusText.Text = _worker.Parallel <= 1
            ? "Queue started · single"
            : $"Queue started · {_worker.Parallel} at once";
        RefreshStatus();
    }

    private void Parallel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ParallelBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Content?.ToString(), out var n)) return;
        _worker.Parallel = n;
        StatusText.Text = n <= 1 ? "Single download" : $"Parallel: {n} videos at once";
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _worker.PauseAll();
        StatusText.Text = "Queue paused";
        RefreshStatus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var ids = Selected().Select(i => i.Id).ToList();
        if (ids.Count == 0) return;
        var msg = ids.Count == 1
            ? "Remove the selected download from the list?\nDownloaded files are not deleted."
            : $"Remove {ids.Count} selected downloads from the list?\nDownloaded files are not deleted.";
        if (MessageBox.Show(this, msg, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _worker.DeleteIds(ids);
        DetailBox.Text = "Select a download to view details.";
        RefreshStatus();
    }

    private void ClearDone_Click(object sender, RoutedEventArgs e)
    {
        _worker.ClearFinished();
        RefreshStatus();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var path = string.IsNullOrWhiteSpace(OutputBox.Text)
            ? Path.Combine(Environment.CurrentDirectory, "downloads")
            : OutputBox.Text.Trim();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void BrowseOut_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Output folder" };
        if (dlg.ShowDialog() == true)
        {
            OutputBox.Text = dlg.FolderName;
            ApplySettings();
        }
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplySettings();
    }

    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is DownloadItem prev && e.RemovedItems.Contains(prev))
            prev.PropertyChanged -= ItemOnPropertyChanged;

        if (QueueList.SelectedItem is not DownloadItem item)
        {
            DetailBox.Text = "Select a download to view details.";
            return;
        }
        ShowDetails(item);
        item.PropertyChanged -= ItemOnPropertyChanged;
        item.PropertyChanged += ItemOnPropertyChanged;
    }

    private void ItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DownloadItem item && ReferenceEquals(QueueList.SelectedItem, item))
            ShowDetails(item);
    }

    private void ShowDetails(DownloadItem item)
    {
        var hdr = item.Headers.Count == 0
            ? "—"
            : string.Join(", ", item.Headers.Select(kv => kv.Key));
        DetailBox.Text = string.Join('\n',
        [
            $"ID: {item.Id}",
            $"URL: {item.Url}",
            $"Source: {item.SourceText}",
            $"Status: {item.StateText}",
            $"Progress: {item.Progress:0.0}%",
            $"Speed: {item.SpeedText}",
            $"Priority: {item.PriorityText}",
            $"Title: {(string.IsNullOrEmpty(item.Title) ? "—" : item.Title)}",
            $"File: {(string.IsNullOrEmpty(item.Filepath) ? "—" : item.Filepath)}",
            $"Error: {(string.IsNullOrEmpty(item.Error) ? "—" : item.Error)}",
            $"Headers: {hdr}",
            $"Audio only: {item.AudioOnly}",
            $"Output: {item.OutputDir}",
            "Note: HLS/m3u8 needs ffmpeg; video prefers H.264"
        ]);
    }
}
