using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;

namespace Waveify;

/// <summary>Installs optional tools after user approval. Uses official GitHub release assets and SHA-256 digests.</summary>
internal sealed record InstallProgress(string Tool, string Stage, long DownloadedBytes = 0, long? TotalBytes = null);

internal static class DependencyInstaller
{
    public static string ToolsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waveify", "tools");
    public static string YtDlpPath => Path.Combine(ToolsFolder, "yt-dlp.exe");
    public static string SunnifyPath => Path.Combine(ToolsFolder, "Sunnify-Windows-CLI.exe");
    public static string FfmpegPath => Path.Combine(ToolsFolder, "ffmpeg.exe");
    public static string DenoPath => Path.Combine(ToolsFolder, "deno.exe");
    private const long MaxDownloadBytes = 700L * 1024 * 1024; // Sunnify and FFmpeg releases can exceed 150 MB.
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };

    private static string ExecutableName(string name) => name switch
    {
        "yt-dlp" => "yt-dlp.exe",
        "sunnify" => "Sunnify-Windows-CLI.exe",
        "ffmpeg" => "ffmpeg.exe",
        "deno" => "deno.exe",
        _ => throw new ArgumentException("Unsupported tool", nameof(name))
    };

    public static bool IsAvailable(string name)
    {
        var filename = ExecutableName(name);
        if (File.Exists(Path.Combine(ToolsFolder, filename)) || File.Exists(Path.Combine(AppContext.BaseDirectory, filename))) return true;
        // Older installers may have left the GUI binary; don't count that as the CLI.
        try
        {
            using var p = Process.Start(new ProcessStartInfo("where.exe", name == "ffmpeg" ? "ffmpeg" : name == "deno" ? "deno" : name == "sunnify" ? "sunnify" : "yt-dlp")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            p?.WaitForExit(1500);
            return p is { HasExited: true, ExitCode: 0 };
        }
        catch { return false; }
    }

    public static string Resolve(string name)
    {
        string filename = ExecutableName(name);
        string installed = Path.Combine(ToolsFolder, filename);
        string adjacent = Path.Combine(AppContext.BaseDirectory, filename);
        if (File.Exists(installed)) return installed;
        if (File.Exists(adjacent)) return adjacent;
        return name;
    }

    public static async Task<string> InstallAsync(string name, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string repo = name switch
        {
            "yt-dlp" => "yt-dlp/yt-dlp",
            "sunnify" => "sunnypatell/sunnify-spotify-downloader",
            "ffmpeg" => "BtbN/FFmpeg-Builds",
            "deno" => "denoland/deno",
            _ => throw new ArgumentException("Unsupported tool", nameof(name))
        };
        string assetName = name switch
        {
            "ffmpeg" => "ffmpeg-master-latest-win64-gpl.zip",
            "deno" => "deno-x86_64-pc-windows-msvc.zip",
            _ => ExecutableName(name)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        request.Headers.UserAgent.ParseAdd("Waveify-DependencySetup/1.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub release lookup failed ({(int)response.StatusCode}). See https://github.com/{repo}/releases");
        using var info = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var assets = info.RootElement.GetProperty("assets");
        JsonElement? selected = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (string.Equals(asset.GetProperty("name").GetString(), assetName, StringComparison.OrdinalIgnoreCase))
            { selected = asset; break; }
        }
        if (selected is null) throw new InvalidOperationException($"Latest release has no {assetName}. See https://github.com/{repo}/releases");
        var item = selected.Value;
        string url = item.GetProperty("browser_download_url").GetString() ?? throw new InvalidOperationException("Missing asset URL.");
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith($"/{repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Release download URL is not an official GitHub release path.");
        string? digest = item.TryGetProperty("digest", out var hashItem) ? hashItem.GetString() : null;
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
            throw new InvalidOperationException($"No published SHA-256 digest for {assetName}; install and verify it manually.");
        if (item.TryGetProperty("size", out var size) && size.GetInt64() > MaxDownloadBytes)
            throw new InvalidOperationException($"Official asset exceeds the {MaxDownloadBytes / 1024 / 1024} MiB limit; install manually.");
        Directory.CreateDirectory(ToolsFolder);
        string temp = Path.Combine(ToolsFolder, Guid.NewGuid().ToString("N") + (name is "ffmpeg" or "deno" ? ".zip" : ".download"));
        try
        {
            progress?.Report(new(name, "Downloading", 0, item.TryGetProperty("size", out var expectedSize) ? expectedSize.GetInt64() : null));
            using var download = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            download.EnsureSuccessStatusCode();
            if (download.Content.Headers.ContentLength > MaxDownloadBytes) throw new InvalidOperationException("Release asset too large.");
            await using (var source = await download.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(temp))
            {
                var buffer = new byte[128 * 1024];
                long total = 0; int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > MaxDownloadBytes) throw new InvalidOperationException("Release asset too large.");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report(new(name, "Downloading", total, download.Content.Headers.ContentLength ?? (item.TryGetProperty("size", out var declaredSize) ? declaredSize.GetInt64() : null)));
                }
            }
            progress?.Report(new(name, "Verifying SHA-256"));
            await using (var input = File.OpenRead(temp))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SHA-256 mismatch. The downloaded file has been rejected.");
            }
            if (name == "ffmpeg")
            {
                progress?.Report(new(name, "Extracting FFmpeg"));
                using var archive = ZipFile.OpenRead(temp);
                foreach (string binary in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = archive.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').EndsWith("/bin/" + binary, StringComparison.OrdinalIgnoreCase));
                    if (entry is null) throw new InvalidOperationException($"FFmpeg archive doesn't contain {binary}.");
                    string destination = Path.Combine(ToolsFolder, binary);
                    string stage = destination + ".new";
                    try { entry.ExtractToFile(stage, true); File.Move(stage, destination, true); }
                    finally { if (File.Exists(stage)) File.Delete(stage); }
                }
                progress?.Report(new(name, "Installed"));
                return FfmpegPath;
            }
            if (name == "deno")
            {
                progress?.Report(new(name, "Extracting Deno"));
                using var archive = ZipFile.OpenRead(temp);
                var entry = archive.Entries.FirstOrDefault(x =>
                    string.Equals(x.FullName.Replace('\\', '/'), "deno.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null) throw new InvalidOperationException("Official Deno archive does not contain deno.exe.");
                string stage = DenoPath + ".new";
                try
                {
                    entry.ExtractToFile(stage, true);
                    File.Move(stage, DenoPath, true);
                }
                finally { if (File.Exists(stage)) File.Delete(stage); }
                progress?.Report(new(name, "Installed"));
                return DenoPath;
            }
            string dest = Path.Combine(ToolsFolder, ExecutableName(name));
            File.Move(temp, dest, true);
            progress?.Report(new(name, "Installed"));
            return dest;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
