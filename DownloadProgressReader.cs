using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Waveify;

internal static class DownloadProgressReader
{
    private static readonly Regex PercentPattern = new(@"(?<!\d)(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    public static async Task<string> ReadAsync(StreamReader reader, string provider, Action<SongDownloadProgress>? onProgress)
    {
        var errors = new System.Text.StringBuilder();
        string currentTrack = "Preparing audio";
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            if (errors.Length < 5000) errors.AppendLine(line.Length > 500 ? line[..500] : line);
            if (line.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
                currentTrack = Path.GetFileName(line["[download] Destination:".Length..].Trim());
            else if (line.StartsWith("[download] ") && line.Contains(" has already been downloaded"))
                currentTrack = line[11..].Split(" has already been downloaded")[0].Trim();
            else if (line.StartsWith("[ExtractAudio] Destination:", StringComparison.OrdinalIgnoreCase))
                currentTrack = Path.GetFileName(line["[ExtractAudio] Destination:".Length..].Trim());
            var match = PercentPattern.Match(line);
            double? percent = match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number <= 100 ? number : null;
            // Sunnify may not report per-track percentages; show an indeterminate bar in that case.
            if (percent is not null || line.Contains("Destination:") || line.Contains("download", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase))
                onProgress?.Invoke(new SongDownloadProgress(provider, currentTrack, percent, line.Length > 180 ? line[..180] : line));
        }
        return errors.ToString();
    }
}
