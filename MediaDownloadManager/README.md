# Media Download Manager (C# WPF · .NET 8)

Port của lõi Python sang **WPF net8.0-windows**.

## Yêu cầu

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [`yt-dlp`](https://github.com/yt-dlp/yt-dlp) trên PATH hoặc cạnh file `.exe`
- [`ffmpeg`](https://ffmpeg.org/) trên PATH (merge / mp3)

## Chạy

```bash
cd MediaDownloadManager
dotnet run -c Release
```

## Publish single-file

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\release-wpf
```

## Tính năng

- UI kiểu Free Download Manager (toolbar / sidebar filter / queue / details)
- Multi URL, clipboard, list file
- YouTube, TikTok, Instagram, Facebook, generic HTTP (via yt-dlp process)
- Cookies browser chỉ cho nền tảng đã biết (không gửi GENERIC)
- Torrent: chưa port libtorrent (item sẽ báo NotSupported)

Python `download_core/` vẫn giữ làm reference / CLI cũ.
