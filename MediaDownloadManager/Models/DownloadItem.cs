using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MediaDownloadManager.Models;

public sealed class DownloadItem : INotifyPropertyChanged
{
    private ItemState _state = ItemState.Queued;
    private DownloadPriority _priority = DownloadPriority.Normal;
    private double _progress;
    private double _speed;
    private string _title = "";
    private string _filepath = "";
    private string _error = "";
    private string _eta = "";

    public int Id { get; init; }
    public string Url { get; init; } = "";
    public SourceType Source { get; init; }
    public bool AudioOnly { get; init; }
    public string OutputDir { get; init; } = "downloads";
    public string? CookiesFromBrowser { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int Generation { get; set; }

    public DownloadPriority Priority
    {
        get => _priority;
        set
        {
            if (_priority == value) return;
            _priority = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PriorityText));
        }
    }

    public ItemState State
    {
        get => _state;
        set { if (_state != value) { _state = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateText)); } }
    }

    public double Progress
    {
        get => _progress;
        set { if (Math.Abs(_progress - value) > 0.01) { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); } }
    }

    public double Speed
    {
        get => _speed;
        set { if (Math.Abs(_speed - value) > 1) { _speed = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedText)); } }
    }

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
    }

    public string Filepath
    {
        get => _filepath;
        set { if (_filepath != value) { _filepath = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
    }

    public string Error
    {
        get => _error;
        set { if (_error != value) { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
    }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Error))
                return $"{(string.IsNullOrEmpty(Title) ? Url : Title)} — {Error}";
            if (State == ItemState.Done && !string.IsNullOrEmpty(Filepath))
                return string.IsNullOrEmpty(Title) ? Path.GetFileName(Filepath) : Title;
            return string.IsNullOrEmpty(Title) ? Url : Title;
        }
    }

    public string Eta
    {
        get => _eta;
        set { if (_eta != value) { _eta = value; OnPropertyChanged(); } }
    }

    public string StateText => State.ToString();
    public string ProgressText => $"{Progress:0.0}%";
    public string SpeedText => FormatSpeed(Speed);
    public string SourceText => Source.ToString();
    public string PriorityText => Priority switch
    {
        DownloadPriority.High => "High",
        DownloadPriority.Low => "Low",
        _ => "Normal"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatSpeed(double bps)
    {
        if (bps <= 0) return "—";
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var v = bps;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.0} {units[i]}";
    }

    public static string FormatSize(long? n)
    {
        if (n is null or <= 0) return "—";
        double v = n.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.0} {units[i]}";
    }
}
