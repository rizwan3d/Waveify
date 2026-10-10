using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Waveify;

// Only fetches public YouTube search metadata; does not download media.
internal sealed record YouTubeSearchHit(string Title, string Artist, string Url, string ArtworkUrl, int DurationSeconds);

internal static class YouTubeSearch
{
    private static readonly Regex VideoIdPattern =
        new("^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<IReadOnlyList<YouTubeSearchHit>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0) return Array.Empty<YouTubeSearchHit>();
        if (query.Length > 160) query = query[..160];

        // ytsearch10: is a yt-dlp search pseudo-URL. ArgumentList keeps the
        // query as a single argument, so it cannot inject command-line flags.
        var start = new ProcessStartInfo(DependencyInstaller.Resolve("yt-dlp"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[]
        {
            "--ignore-config", "--skip-download", "--flat-playlist",
            "--dump-single-json", "--no-warnings", "--socket-timeout", "10",
            "--", "ytsearch10:" + query
        })
            start.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorsTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output = await outputTask;
            string errors = await errorsTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("yt-dlp search failed: " +
                    errors.Trim()[..Math.Min(errors.Trim().Length, 350)]);

            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                return Array.Empty<YouTubeSearchHit>();

            var hits = new List<YouTubeSearchHit>();
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string id = ReadText(item, "id");
                if (!VideoIdPattern.IsMatch(id)) continue;
                string title = ReadText(item, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;
                string artist = ReadText(item, "channel");
                if (artist.Length == 0) artist = ReadText(item, "uploader");
                if (artist.Length == 0) artist = "YouTube";
                var thumbnail = ReadText(item, "thumbnail");
                if (!Uri.TryCreate(thumbnail, UriKind.Absolute, out var thumbnailUri) ||
                    thumbnailUri.Scheme != Uri.UriSchemeHttps)
                    thumbnail = "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg";
                int seconds = 0;
                if (item.TryGetProperty("duration", out var duration) &&
                    duration.ValueKind == JsonValueKind.Number &&
                    duration.TryGetDouble(out var value) && value > 0 && value <= int.MaxValue)
                    seconds = (int)value;
                hits.Add(new YouTubeSearchHit(title, artist,
                    "https://www.youtube.com/watch?v=" + id, thumbnail, seconds));
            }
            return hits;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "yt-dlp is missing. Install it using Waveify's Setup tools button.", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("YouTube search timed out. Try again.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("yt-dlp returned an unreadable search response.", ex);
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    private static string ReadText(JsonElement item, string property) =>
        item.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
