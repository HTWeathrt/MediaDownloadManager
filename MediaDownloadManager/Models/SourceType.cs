namespace MediaDownloadManager.Models;

public enum SourceType
{
    Unknown,
    Youtube,
    Tiktok,
    Instagram,
    Facebook,
    Torrent,
    Hls,
    Generic
}

public enum ItemState
{
    Queued,
    Running,
    Paused,
    Done,
    Failed,
    Cancelled
}

public enum DownloadPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}
