using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MediaDownloadManager.Core;
using MediaDownloadManager.Models;

namespace MediaDownloadManager.Services;

public sealed class QueueWorker
{
    private readonly ObservableCollection<DownloadItem> _items = [];
    private readonly List<int> _pending = [];
    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource _cts = new();
    private readonly HashSet<int> _runningIds = [];
    private readonly List<CancellationTokenSource> _jobTokens = [];
    private int _nextId = 1;
    private int _generation;
    private int _parallel = 1;
    private bool _paused;
    private Task? _loop;

    public ObservableCollection<DownloadItem> Items => _items;

    /// <summary>Raised when a download failed because the site needs a signed-in session.</summary>
    public event Action? LoginRequested;
    public string OutputDir { get; private set; } = Path.Combine(Environment.CurrentDirectory, "downloads");
    public bool AudioOnly { get; private set; }
    public bool AutoConvertHevc { get; private set; }
    public string? CookiesFromBrowser { get; private set; }
    public string? YtDlpPath { get; set; }

    /// <summary>How many videos download at once. 1 = single, up to 8 = multi.</summary>
    public int Parallel
    {
        get { lock (_gate) return _parallel; }
        set { lock (_gate) _parallel = Math.Clamp(value, 1, 8); }
    }

    public QueueWorker(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Configure(string outputDir, bool audioOnly, string? cookiesFromBrowser, bool autoConvertHevc = false)
    {
        lock (_gate)
        {
            OutputDir = outputDir;
            AudioOnly = audioOnly;
            AutoConvertHevc = autoConvertHevc;
            CookiesFromBrowser = string.IsNullOrWhiteSpace(cookiesFromBrowser) ? null : cookiesFromBrowser;
        }
    }

    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Shutdown()
    {
        _generation++;
        _cts.Cancel();
        CancelJobs();
    }

    public IReadOnlyList<DownloadItem> AddUrls(IEnumerable<string> urls, bool autoStart = true)
    {
        var reqs = urls.Select(u => new ParsedDownloadRequest
        {
            Url = u.Trim(),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            IsHls = CurlCommandParser.IsHlsUrl(u)
        });
        return AddRequests(reqs, autoStart);
    }

    public IReadOnlyList<DownloadItem> AddRequests(
        IEnumerable<ParsedDownloadRequest> requests,
        bool autoStart = true)
    {
        var added = new List<DownloadItem>();
        lock (_gate)
        {
            var existing = _items.Select(i => i.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var req in requests)
            {
                var url = (req.Url ?? "").Trim();
                if (string.IsNullOrEmpty(url) || !existing.Add(url)) continue;
                var source = SourceDetector.Detect(url);
                if (req.IsHls) source = SourceType.Hls;
                if (source == SourceType.Unknown) continue;

                var item = new DownloadItem
                {
                    Id = _nextId++,
                    Url = url,
                    Source = source,
                    AudioOnly = AudioOnly,
                    OutputDir = OutputDir,
                    CookiesFromBrowser = CookiesFromBrowser,
                    Headers = new Dictionary<string, string>(req.Headers, StringComparer.OrdinalIgnoreCase),
                    Generation = _generation,
                    Priority = DownloadPriority.Normal,
                    State = autoStart ? ItemState.Queued : ItemState.Paused
                };
                added.Add(item);
                _dispatcher.Invoke(() => _items.Add(item));
                if (autoStart) EnqueueJob(item.Id);
            }
        }
        return added;
    }

    public void SetPriority(IEnumerable<int> ids, DownloadPriority priority)
    {
        var set = ids.ToHashSet();
        lock (_gate)
        {
            foreach (var item in _items.Where(i => set.Contains(i.Id)))
                item.Priority = priority;
        }
    }

    public void StartAll()
    {
        _paused = false;
        lock (_gate)
        {
            foreach (var item in _items.Where(i => i.State == ItemState.Paused))
            {
                item.State = ItemState.Queued;
                EnqueueJob(item.Id);
            }
        }
    }

    private void EnqueueJob(int id)
    {
        lock (_gate)
        {
            if (!_pending.Contains(id))
                _pending.Add(id);
        }
    }

    private bool TryDequeueNext(out int id)
    {
        lock (_gate)
        {
            id = 0;
            if (_pending.Count == 0) return false;

            // Highest priority first; among equals keep FIFO (lowest list index).
            var bestIndex = -1;
            var bestPri = DownloadPriority.Low;
            for (var i = 0; i < _pending.Count; i++)
            {
                var item = _items.FirstOrDefault(x => x.Id == _pending[i]);
                if (item is null || item.State != ItemState.Queued)
                {
                    _pending.RemoveAt(i);
                    i--;
                    continue;
                }
                if (bestIndex < 0 || item.Priority > bestPri)
                {
                    bestIndex = i;
                    bestPri = item.Priority;
                }
            }
            if (bestIndex < 0) return false;
            id = _pending[bestIndex];
            _pending.RemoveAt(bestIndex);
            return true;
        }
    }

    public void Requeue(IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        _paused = false;
        lock (_gate)
        {
            foreach (var item in _items.Where(i => set.Contains(i.Id)
                         && i.State is ItemState.Paused or ItemState.Failed or ItemState.Cancelled))
            {
                item.Error = "";
                item.State = ItemState.Queued;
                EnqueueJob(item.Id);
            }
        }
    }

    public void PauseAll()
    {
        _paused = true;
        CancelJobs();
        lock (_gate)
        {
            foreach (var item in _items.Where(i => i.State == ItemState.Queued))
                item.State = ItemState.Paused;
            _pending.Clear();
        }
    }

    public void ClearFinished()
    {
        _dispatcher.Invoke(() =>
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                var s = _items[i].State;
                if (s is ItemState.Done or ItemState.Failed or ItemState.Cancelled)
                    _items.RemoveAt(i);
            }
        });
    }

    public void ClearAll()
    {
        _generation++;
        CancelJobs();
        lock (_gate) _pending.Clear();
        _dispatcher.Invoke(() => _items.Clear());
    }

    public void DeleteIds(IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        lock (_gate)
        {
            if (_runningIds.Overlaps(set))
                CancelJobs();
        }
        lock (_gate)
            _pending.RemoveAll(set.Contains);
        _dispatcher.Invoke(() =>
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                if (set.Contains(_items[i].Id))
                    _items.RemoveAt(i);
            }
        });
    }

    public bool HasItem(int id)
    {
        lock (_gate) return _items.Any(i => i.Id == id);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_paused)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            var slots = Parallel - RunningCount();
            if (slots <= 0 || !TryDequeueNext(out var id))
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            DownloadItem? item = null;
            _dispatcher.Invoke(() => item = _items.FirstOrDefault(i => i.Id == id));
            if (item is null || item.State != ItemState.Queued) continue;
            _ = Task.Run(() => DownloadOneAsync(item, ct), ct);
        }
    }

    private int RunningCount()
    {
        lock (_gate) return _runningIds.Count;
    }

    private void CancelJobs()
    {
        List<CancellationTokenSource> copy;
        lock (_gate) copy = _jobTokens.ToList();
        foreach (var cts in copy)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
        }
    }

    private async Task DownloadOneAsync(DownloadItem item, CancellationToken ct)
    {
        var gen = _generation;
        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var jobCt = jobCts.Token;
        lock (_gate)
        {
            _runningIds.Add(item.Id);
            _jobTokens.Add(jobCts);
        }

        Update(item, i =>
        {
            i.State = ItemState.Running;
            i.Progress = 0;
            i.Error = "";
        });

        try
        {
            if (item.Source == SourceType.Torrent)
                throw new NotSupportedException(
                    "Torrent: install Python libtorrent path or use magnet via external client. yt-dlp path only for URL platforms.");

            var dl = new YtDlpDownloader
            {
                OutputDir = item.OutputDir,
                AudioOnly = item.AudioOnly,
                CookiesFromBrowser = item.CookiesFromBrowser,
                YtDlpPath = YtDlpPath,
                Headers = item.Headers
            };

            var progress = new Progress<DownloadProgress>(p =>
            {
                if (gen != _generation || !HasItem(item.Id)) return;
                Update(item, i =>
                {
                    i.Progress = p.Progress;
                    if (p.Speed > 0) i.Speed = p.Speed;
                    if (!string.IsNullOrEmpty(p.Eta)) i.Eta = p.Eta!;
                    if (!string.IsNullOrEmpty(p.Name)) i.Title = p.Name!;
                });
            });

            var result = await dl.DownloadAsync(item.Url, progress, jobCt).ConfigureAwait(false);
            if (gen != _generation || !HasItem(item.Id)) return;

            var filepath = result.Filepath ?? "";
            if (filepath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(filepath))
            {
                Update(item, i =>
                {
                    i.Title = result.Title ?? i.Title;
                    i.Filepath = filepath;
                    i.Error = "Remux PNG (MPEG-TS) → MP4…";
                });
                filepath = await FfmpegConverter.RemuxPngToMp4Async(filepath, jobCt).ConfigureAwait(false);
            }

            if (!item.AudioOnly && !string.IsNullOrEmpty(filepath) && File.Exists(filepath))
            {
                Update(item, i =>
                {
                    i.Title = result.Title ?? i.Title;
                    i.Filepath = filepath;
                    i.Progress = 0;
                    i.Error = "Checking video codec…";
                });

                try
                {
                    var codec = await FfmpegConverter.DetectVideoCodecAsync(filepath, jobCt).ConfigureAwait(false);
                    if (FfmpegConverter.IsHevc(codec))
                    {
                        Update(item, i => i.Error = "Converting to H.264…");
                        var convProgress = new Progress<ConvertProgress>(p =>
                        {
                            if (gen != _generation || !HasItem(item.Id)) return;
                            Update(item, i =>
                            {
                                i.Progress = p.Percent;
                                i.Error = $"Converting… {p.Percent:0.0}%";
                            });
                        });
                        var conv = await FfmpegConverter.ConvertToH264Async(
                            filepath,
                            replaceOriginal: true,
                            progress: convProgress,
                            ct: jobCt).ConfigureAwait(false);
                        if (conv.Converted)
                            filepath = conv.OutputPath;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception cex)
                {
                    Update(item, i =>
                    {
                        i.Title = result.Title ?? i.Title;
                        i.Filepath = filepath;
                        i.Progress = 100;
                        i.Speed = 0;
                        i.Eta = "";
                        i.State = ItemState.Done;
                        i.Error = $"Downloaded OK; convert failed: {cex.Message}";
                        i.State = ItemState.Failed;
                        ErrorLog.Write($"#{item.Id} FAIL convert {item.Url}\n{cex.Message}");
                    });
                    return;
                }
            }

            if (gen != _generation || !HasItem(item.Id)) return;
            Update(item, i =>
            {
                i.Title = result.Title ?? i.Title;
                i.Filepath = filepath;
                i.Progress = 100;
                i.Speed = 0;
                i.Eta = "";
                i.Error = "";
                i.State = ItemState.Done;
            });
        }
        catch (OperationCanceledException)
        {
            if (gen != _generation || !HasItem(item.Id)) return;
            Update(item, i =>
            {
                if (_paused)
                {
                    i.State = ItemState.Paused;
                    i.Error = "";
                }
                else
                {
                    i.State = ItemState.Cancelled;
                    i.Error = "Cancelled";
                }
                i.Speed = 0;
                i.Eta = "";
            });
        }
        catch (Exception ex)
        {
            if (gen != _generation || !HasItem(item.Id)) return;
            Update(item, i =>
            {
                i.State = ItemState.Failed;
                i.Error = ex.Message;
                ErrorLog.Write($"#{item.Id} {item.Url}\n{ex.Message}");
                i.Speed = 0;
                i.Eta = "";
            });
            if (ex.Message.Contains("Sign in to YouTube", StringComparison.OrdinalIgnoreCase))
                LoginRequested?.Invoke();
        }
        finally
        {
            lock (_gate)
            {
                _runningIds.Remove(item.Id);
                _jobTokens.Remove(jobCts);
            }
            jobCts.Dispose();
        }
    }

    private void Update(DownloadItem item, Action<DownloadItem> action)
    {
        if (_dispatcher.CheckAccess())
            action(item);
        else
            _dispatcher.Invoke(() => action(item));
    }
}
