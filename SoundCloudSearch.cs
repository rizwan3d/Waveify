using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Waveify;

// Searches public SoundCloud track metadata; no audio is downloaded by this helper.
internal sealed record SoundCloudSearchHit(string Title, string Artist, string Url, string ArtworkUrl, int DurationSeconds);

internal static class SoundCloudSearch
{
    public static async Task<IReadOnlyList<SoundCloudSearchHit>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0) return Array.Empty<SoundCloudSearchHit>();
        if (query.Length > 160) query = query[..160];

        // scsearch10: is yt-dlp's SoundCloud search pseudo-URL.
        // ArgumentList prevents the query from becoming a command-line option.
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
            "--", "scsearch10:" + query
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
            {
                string message = errors.Trim();
                throw new InvalidOperationException("SoundCloud search failed: " +
                    message[..Math.Min(message.Length, 350)]);
            }

            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                return Array.Empty<SoundCloudSearchHit>();

            var hits = new List<SoundCloudSearchHit>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string title = ReadText(item, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                // yt-dlp's flat SoundCloud results may expose an API URL in "url".
                // Only public soundcloud.com permalinks can be opened/imported.
                string url = FirstPublicTrackUrl(
                    ReadText(item, "webpage_url"),
                    ReadText(item, "permalink_url"),
                    ReadText(item, "url"));
                if (url.Length == 0 || !seen.Add(url)) continue;

                string artist = ReadText(item, "uploader");
                if (artist.Length == 0) artist = ReadText(item, "channel");
                if (artist.Length == 0) artist = ReadText(item, "artist");
                if (artist.Length == 0) artist = "SoundCloud";
                string thumbnail = ReadText(item, "thumbnail");
                if (!Uri.TryCreate(thumbnail, UriKind.Absolute, out var cover) ||
                    cover.Scheme != Uri.UriSchemeHttps)
                    thumbnail = "";
                int seconds = 0;
                if (item.TryGetProperty("duration", out var duration) &&
                    duration.ValueKind == JsonValueKind.Number &&
                    duration.TryGetDouble(out var value) && value > 0 && value <= int.MaxValue)
                    seconds = (int)value;
                hits.Add(new SoundCloudSearchHit(title, artist, url, thumbnail, seconds));
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
            throw new TimeoutException("SoundCloud search timed out. Try again.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("yt-dlp returned an unreadable SoundCloud search response.", ex);
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

    private static string FirstPublicTrackUrl(params string[] candidates)
    {
        foreach (var value in candidates)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps) continue;
            if (uri.Host.Equals("soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".soundcloud.com", StringComparison.OrdinalIgnoreCase))
                return uri.AbsoluteUri;
        }
        return "";
    }

    private static string ReadText(JsonElement item, string property) =>
        item.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
