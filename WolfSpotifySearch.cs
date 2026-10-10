using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Waveify;

/// <summary>
/// Queries the independent wolfXspotify-API for public Spotify track metadata.
/// Does not obtain Spotify tokens, play audio, or download protected content.
/// No additional local search tools are required.
/// </summary>
internal sealed record WolfSpotifyHit(
    string Title, string Artist, string Url, string ArtworkUrl, int DurationSeconds);

internal static class WolfSpotifySearch
{
    private const string SearchEndpoint = "https://spotify.xwolf.space/api/search";
    private const long MaxResponseBytes = 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<IReadOnlyList<WolfSpotifyHit>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0) return Array.Empty<WolfSpotifyHit>();
        if (query.Length > 160) query = query[..160];

        // Use only the search endpoint, never the service's token endpoint.
        string url = SearchEndpoint + "?q=" + Uri.EscapeDataString(query) + "&type=track&limit=10";
        using var response = await Client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // The server returns 404 when there are no matching tracks.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return Array.Empty<WolfSpotifyHit>();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException(
                "Spotify search is temporarily rate-limited. Wait a moment and try again.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Spotify search service returned HTTP {(int)response.StatusCode}.");

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidOperationException("Spotify search returned too much data.");
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var responseBuffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int bytesRead;
        while ((bytesRead = await responseStream.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
        {
            if (responseBuffer.Length + bytesRead > MaxResponseBytes)
                throw new InvalidOperationException("Spotify search returned too much data.");
            await responseBuffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }
        responseBuffer.Position = 0;
        using var json = JsonDocument.Parse(responseBuffer);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Unexpected Spotify search response.");

        if (root.TryGetProperty("success", out var successful) &&
            successful.ValueKind == JsonValueKind.False)
        {
            var error = ReadString(root, "error");
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? "Spotify search service failed." :
                "Spotify search service: " + error[..Math.Min(180, error.Length)]);
        }

        if (!root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Spotify search results were unavailable.");

        var hits = new List<WolfSpotifyHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in results.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            string urlFromApi = ReadString(entry, "url");
            string urlForTrack = CanonicalTrackUrl(ReadString(entry, "id"), urlFromApi);
            if (urlForTrack.Length == 0 || !seen.Add(urlForTrack)) continue;

            string title = ReadString(entry, "title").Trim();
            if (title.Length == 0) continue;
            string artist = ReadString(entry, "artist").Trim();
            if (artist.Length == 0) artist = "Spotify";
            string artwork = ReadString(entry, "thumbnail");
            if (!Uri.TryCreate(artwork, UriKind.Absolute, out var imageUri) ||
                imageUri.Scheme != Uri.UriSchemeHttps)
                artwork = "";

            int seconds = 0;
            if (entry.TryGetProperty("duration_ms", out var duration) &&
                duration.ValueKind == JsonValueKind.Number &&
                duration.TryGetDouble(out double milliseconds) &&
                milliseconds > 0 && milliseconds <= (double)int.MaxValue * 1000)
                seconds = (int)(milliseconds / 1000);

            hits.Add(new WolfSpotifyHit(title, artist, urlForTrack, artwork, seconds));
            if (hits.Count >= 10) break;
        }
        return hits;
    }

    private static string CanonicalTrackUrl(string id, string candidate)
    {
        if (!ValidId(id) &&
            Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 2 && parts[0] == "track") id = parts[1];
            else if (parts.Length == 3 && parts[0].StartsWith("intl-", StringComparison.OrdinalIgnoreCase)
                     && parts[1] == "track") id = parts[2];
        }

        return ValidId(id) ? "https://open.spotify.com/track/" + id : "";
    }

    private static bool ValidId(string id) =>
        id.Length == 22 &&
        id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static string ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
