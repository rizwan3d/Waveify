using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Waveify;

/// <summary>Conservative MusicBrainz + Cover Art Archive lookups for local, user-owned files.</summary>
public sealed class MetadataEnricher : IDisposable
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim musicBrainzGate = new(1, 1);
    private static DateTime nextMusicBrainzRequest = DateTime.MinValue;

    public MetadataEnricher()
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Waveify/1.0 (music library metadata lookup; https://github.com/metabrainz/musicbrainz-server)");
    }

    public async Task<bool> EnrichAsync(Song song, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(song.FilePath) || !File.Exists(song.FilePath) ||
            string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist) ||
            Normalize(song.Artist) is "unknown artist" or "online link") return false;

        // Compare title AND artist. Don't automatically assign arbitrary search results to audio.
        string query = $"recording:\"{EscapeQuery(song.Title)}\" AND artist:\"{EscapeQuery(song.Artist)}\"";
        string url = "https://musicbrainz.org/ws/2/recording/?fmt=json&limit=5&query=" + Uri.EscapeDataString(query);
        using var json = await GetMusicBrainzAsync(url, cancellationToken);
        if (json is null || !json.RootElement.TryGetProperty("recordings", out var recordings)) return false;
        foreach (var recording in recordings.EnumerateArray())
        {
            var foundTitle = Str(recording, "title");
            var artist = ArtistCredit(recording);
            if (Normalize(foundTitle) != Normalize(song.Title) || Normalize(artist) != Normalize(song.Artist)) continue;
            if (recording.TryGetProperty("length", out var length) && length.ValueKind == JsonValueKind.Number &&
                song.DurationSeconds > 0 && Math.Abs(length.GetDouble() / 1000d - song.DurationSeconds) > 10) continue;
            if (recording.TryGetProperty("score", out var score) && score.TryGetInt32(out var scoreValue) && scoreValue < 90) continue;

            if (!recording.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) continue;
            JsonElement release = default;
            var options = releases.EnumerateArray().ToList();
            if (options.Count == 0) continue;
            // Preserve an existing album by requiring an exact match if supplied.
            if (!string.IsNullOrWhiteSpace(song.Album) && Normalize(song.Album) != "unknown album")
            {
                var match = options.FirstOrDefault(r => Normalize(Str(r, "title")) == Normalize(song.Album));
                if (match.ValueKind != JsonValueKind.Object) continue;
                release = match;
            }
            else release = options[0];

            var album = Str(release, "title");
            var releaseId = Str(release, "id");
            var date = Str(release, "date");
            uint year = date.Length >= 4 && uint.TryParse(date[..4], out var parsedYear) ? parsedYear : 0;
            var newAlbum = string.IsNullOrWhiteSpace(song.Album) || Normalize(song.Album) == "unknown album" ? album : song.Album;

            byte[]? cover = null;
            if (string.IsNullOrWhiteSpace(song.ArtworkPath) || !File.Exists(song.ArtworkPath))
                cover = await FetchCoverAsync(releaseId, cancellationToken);

            // Backup original metadata where possible and only fill missing fields.
            using (var file = TagLib.File.Create(song.FilePath))
            {
                bool changed = false;
                if (string.IsNullOrWhiteSpace(file.Tag.Title)) { file.Tag.Title = song.Title; changed = true; }
                if (file.Tag.Performers.Length == 0) { file.Tag.Performers = new[] { song.Artist }; changed = true; }
                if (string.IsNullOrWhiteSpace(file.Tag.Album) && !string.IsNullOrWhiteSpace(newAlbum)) { file.Tag.Album = newAlbum; changed = true; }
                if (file.Tag.Year == 0 && year > 0) { file.Tag.Year = year; changed = true; }
                if (file.Tag.Pictures.Length == 0 && cover != null)
                {
                    file.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(cover))
                        { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg", Description = "Album cover" } };
                    changed = true;
                }
                if (changed) file.Save();
            }
            song.Album = newAlbum;
            if (cover != null)
            {
                var directory = Path.Combine(LibraryStore.Folder, "artwork");
                Directory.CreateDirectory(directory);
                song.ArtworkPath = Path.Combine(directory, song.Id + ".jpg");
                await File.WriteAllBytesAsync(song.ArtworkPath, cover, cancellationToken);
            }
            if (year > 0) song.Year = year;
            song.MusicBrainzRecordingId = Str(recording, "id");
            song.MusicBrainzReleaseId = releaseId;
            MusicOrganizer.SaveAlbumArtwork(song);
            return true;
        }
        return false;
    }

    private async Task<JsonDocument?> GetMusicBrainzAsync(string url, CancellationToken token)
    {
        await musicBrainzGate.WaitAsync(token);
        try
        {
            var delay = nextMusicBrainzRequest - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            nextMusicBrainzRequest = DateTime.UtcNow.AddMilliseconds(1200);
            using var response = await client.GetAsync(url, token);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: token);
        }
        finally { musicBrainzGate.Release(); }
    }

    private async Task<byte[]?> FetchCoverAsync(string releaseId, CancellationToken token)
    {
        if (!Guid.TryParse(releaseId, out _)) return null;
        try
        {
            using var response = await client.GetAsync($"https://coverartarchive.org/release/{releaseId}/front-500", HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength is long len && len > 8_000_000)) return null;
            if (response.Content.Headers.ContentType?.MediaType is not string mime || !mime.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var memory = new MemoryStream();
            var buffer = new byte[81920]; int bytes;
            while ((bytes = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (memory.Length + bytes > 8_000_000) return null;
                memory.Write(buffer, 0, bytes);
            }
            return memory.ToArray();
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private static string EscapeQuery(string value) => value.Replace("\\", " ").Replace("\"", " ").Trim();
    private static string Normalize(string? value) => Regex.Replace(value?.Trim().ToLowerInvariant() ?? "", @"[^\p{L}\p{N}]+", " ").Trim();
    private static string Str(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    private static string ArtistCredit(JsonElement recording)
    {
        if (!recording.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(credit.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object ? Str(x, "name") + Str(x, "joinphrase") : x.ValueKind == JsonValueKind.String ? x.GetString() : ""));
    }
    public void Dispose() => client.Dispose();
}
