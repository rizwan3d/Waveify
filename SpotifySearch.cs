using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Waveify;

// DDGS is a Python web-search library. Search only public Spotify pages;
// do not request credentials, media streams or protected Spotify APIs.
internal sealed record SpotifySearchHit(string Title, string Detail, string Url);

internal static class SpotifySearch
{
    private const int MaximumHits = 10;
    private const string PythonCode = """
import json
import sys

try:
    from ddgs import DDGS
except ImportError:
    print("Python package 'ddgs' is missing. Install it with: py -3 -m pip install -U ddgs", file=sys.stderr)
    sys.exit(2)

query = sys.argv[1].strip()[:160]
try:
    # Search public Spotify pages indexed by third-party web search engines.
    # Avoid Spotify API authentication, cookies, and protected content.
    results = DDGS(timeout=10).text(
        "site:open.spotify.com " + query,
        max_results=40,
        safesearch="moderate"
    )
    print(json.dumps(results, ensure_ascii=False))
except Exception as ex:
    print("ddgs search failed: " + str(ex), file=sys.stderr)
    sys.exit(3)
""";

    public static async Task<IReadOnlyList<SpotifySearchHit>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0) return Array.Empty<SpotifySearchHit>();
        if (query.Length > 160) query = query[..160];

        string output;
        try
        {
            // 'py -3' selects an installed Python 3 on Windows.
            output = await SearchWithPythonAsync("py", useWindowsLauncher: true, query, cancellationToken);
        }
        catch (Win32Exception)
        {
            try
            {
                output = await SearchWithPythonAsync("python", useWindowsLauncher: false, query, cancellationToken);
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "Python 3.10+ is required. Install Python, then run: py -3 -m pip install -U ddgs", ex);
            }
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("ddgs returned an unexpected search response.");

            var hits = new List<SpotifySearchHit>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string url = ValidSpotifyPage(ReadText(item, "href"));
                if (url.Length == 0 || !seen.Add(url)) continue;
                string title = ReadText(item, "title").Trim();
                if (title.Length == 0) continue;
                string detail = ReadText(item, "body").Trim();
                if (detail.Length > 140) detail = detail[..140] + "…";
                if (detail.Length == 0) detail = "Spotify web result";
                hits.Add(new SpotifySearchHit(title, detail, url));
                if (hits.Count >= MaximumHits) break;
            }
            return hits;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ddgs returned an unreadable search response.", ex);
        }
    }

    private static async Task<string> SearchWithPythonAsync(
        string executable, bool useWindowsLauncher, string query, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (useWindowsLauncher) start.ArgumentList.Add("-3");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(PythonCode);
        start.ArgumentList.Add(query); // ArgumentList prevents shell/flag injection.

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output = await outputTask;
            string error = (await errorTask).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error.Length > 0
                    ? error[..Math.Min(error.Length, 450)]
                    : "Python Spotify search failed. Ensure Python 3.10+ and ddgs are installed.");
            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Spotify search timed out. Try again.");
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

    private static string ValidSpotifyPage(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase))
            return "";

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        // Search engines often index /intl-en/track/... links. Normalize those
        // to the canonical form understood by the existing Spotify importer.
        if (segments.Length == 3 && segments[0].StartsWith("intl-", StringComparison.OrdinalIgnoreCase))
            segments = segments[1..];
        if (segments.Length != 2 ||
            !new[] { "track", "album", "playlist" }.Contains(segments[0], StringComparer.OrdinalIgnoreCase) ||
            segments[1].Length != 22 ||
            !segments[1].All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
            return "";

        // Strip tracking parameters and localize-free paths.
        return $"https://open.spotify.com/{segments[0].ToLowerInvariant()}/{segments[1]}";
    }

    private static string ReadText(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
