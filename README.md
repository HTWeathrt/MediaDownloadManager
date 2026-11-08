# Media Download Manager

Windows app (C# WPF, .NET 8) to download video and audio from YouTube, TikTok, Instagram, Facebook, HLS/m3u8, and generic URLs.

## Download

Prebuilt Windows x64 build (needs [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)):

[MediaDownloadManager-win-x64.zip](https://github.com/HTWeathrt/MediaDownloadManager/releases/latest)

Unzip and run `MediaDownloadManager.exe`.

## Requirements

- [yt-dlp](https://github.com/yt-dlp/yt-dlp) on PATH, or `yt-dlp.exe` next to the app
- [ffmpeg](https://ffmpeg.org/) on PATH (HLS merge, mp3, HEVC to H.264)

## Features

- Queue with Start / Pause / Delete, priority (High / Normal / Low)
- Parallel downloads: 1 (single) up to 8 videos at once
- Speed and ETA
- Clipboard auto-add, import URL list, paste a `curl` command (URL + headers)
- HLS / m3u8, including `.vl` manifests and PNG-wrapped MPEG-TS segments
- Prefer H.264; optional auto-convert HEVC to H.264
- In-app YouTube sign-in when cookies are required

## Build

```bash
dotnet publish MediaDownloadManager\MediaDownloadManager.csproj -c Release -r win-x64 --self-contained false -o release-wpf
```

## Layout

```text
MediaDownloadManager/   WPF source
release-wpf/            published binaries
```
