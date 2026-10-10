using System.Diagnostics;
using System.IO;

namespace Waveify;

/// <summary>
/// Runs an independently installed yt-dlp for media that the user is authorized to download.
/// Never supplies cookies, logins, tokens, DRM workarounds, or platform-specific bypasses.
/// </summary>
internal static class AuthorizedDownloader
{
    public static async Task<IReadOnlyList<string>> DownloadAsync(string url, Action<string>? report = null, Action<SongDownloadProgress>? progress = null)
    {
        string staging = Path.Combine(LibraryStore.Folder, "yt-dlp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        report?.Invoke("Importing authorized media: " + url);
        var start = new ProcessStartInfo(DependencyInstaller.Resolve("yt-dlp"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        // yt-dlp treats everything after "--" as a URL. All options must precede it.
        foreach (var part in new[]
        {
            "--ignore-config", "--no-playlist-reverse", "--yes-playlist", "--no-overwrites",
            "--newline", "--progress", "--max-downloads", "100", "--extract-audio",
            "--audio-format", "mp3", "--audio-quality", "0",
            "--embed-metadata", "--embed-thumbnail", "--convert-thumbnails", "jpg",
            "--paths", staging, "--output", "%(id)s.%(ext)s"
        }) start.ArgumentList.Add(part);
        string ffmpegExecutable = DependencyInstaller.Resolve("ffmpeg");
        if (Path.IsPathFullyQualified(ffmpegExecutable) && File.Exists(ffmpegExecutable))
        {
            start.ArgumentList.Add("--ffmpeg-location");
            start.ArgumentList.Add(Path.GetDirectoryName(ffmpegExecutable)!);
        }
        // yt-dlp may require a JavaScript runtime for some extractors.
        if (DependencyInstaller.IsAvailable("deno"))
        {
            start.ArgumentList.Add("--js-runtimes");
            start.ArgumentList.Add("deno:" + DependencyInstaller.Resolve("deno"));
        }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(url);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch yt-dlp.");
            var stdout = DownloadProgressReader.ReadAsync(process.StandardOutput, "yt-dlp", progress);
            var stderr = DownloadProgressReader.ReadAsync(process.StandardError, "yt-dlp", progress);
            await process.WaitForExitAsync();
            var error = await stderr;
            _ = await stdout;
            var files = Directory.GetFiles(staging, "*.mp3", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
                throw new InvalidOperationException("No audio was downloaded. Check permissions, URL, yt-dlp, and FFmpeg. " + error[..Math.Min(1800, error.Length)]);
            // A partially successful playlist can still import the downloaded tracks.
            report?.Invoke($"Downloaded {files.Length} authorized audio files. Organizing…");
            return files;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("yt-dlp was not found. Install yt-dlp and FFmpeg and add them to PATH.", ex);
        }
    }
}
