using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Waveify;

/// <summary>
/// Optional Sunnify CLI bridge. Never installs a downloader, provides credentials, or bypasses access controls.
/// Users must have permission to make local copies of the requested audio.
/// </summary>
internal static class SunnifyDownloader
{
    private static readonly string[] AudioExtensions = { ".mp3", ".m4a", ".flac", ".wav", ".opus" };

    public static async Task<IReadOnlyList<string>> DownloadAsync(string url, Action<string>? report = null, Action<SongDownloadProgress>? progress = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("spotify.com", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Only HTTPS open.spotify.com track, album, or playlist URLs are accepted.");
        if (!new[] { "track", "album", "playlist" }.Contains(uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault(), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Sunnify requires a Spotify track, album, or playlist URL.");

        string staging = Path.Combine(LibraryStore.Folder, "sunnify-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string program = DependencyInstaller.Resolve("sunnify");
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "download", url, "--out", staging, "--format", "mp3", "--json" })
            start.ArgumentList.Add(argument);
        report?.Invoke("Sunnify: importing authorized Spotify-linked audio…");
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Sunnify.");
            var outputTask = DownloadProgressReader.ReadAsync(process.StandardOutput, "Sunnify", progress);
            var errorsTask = DownloadProgressReader.ReadAsync(process.StandardError, "Sunnify", progress);
            await process.WaitForExitAsync();
            _ = await outputTask;
            string errors = await errorsTask;
            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (files.Count == 0)
                throw new InvalidOperationException($"Sunnify returned no audio (exit {process.ExitCode}). " + errors[..Math.Min(errors.Length, 400)]);
            report?.Invoke($"Sunnify: organizing {files.Count} downloaded tracks…");
            return files;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Sunnify is not installed. Put Sunnify-Windows.exe next to Waveify.exe, or install its CLI as 'sunnify' on PATH.", ex);
        }
    }
}
