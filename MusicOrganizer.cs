using System.IO;
using System.Text;

namespace Waveify;

/// <summary>Copies authorized music into a managed, human-readable offline library.</summary>
public static class MusicOrganizer
{
    // Some Windows installations redirect the Music known folder to a missing
    // drive / OneDrive path. A writable local fallback keeps imports working.
    private static readonly string PreferredRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Waveify");
    private static readonly string FallbackRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waveify", "Music");
    private static string? customRoot;
    private static string? selectedRoot;

    public static string Root => selectedRoot ?? customRoot ?? PreferredRoot;
    public static string DefaultRoot => PreferredRoot;

    public static void SetCustomRoot(string? root)
    {
        customRoot = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
        selectedRoot = null;
    }

    private static string PrepareDirectory(string artist, string album)
    {
        foreach (string root in new[] { customRoot, PreferredRoot, FallbackRoot }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Ensure the root is actually accessible, not just syntactically valid.
                string directory = Path.Combine(root, artist, album);
                Directory.CreateDirectory(directory);
                if (!Directory.Exists(directory))
                    throw new DirectoryNotFoundException($"Created folder cannot be accessed: {directory}");
                selectedRoot = root;
                return directory;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (string.Equals(root, FallbackRoot, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Cannot create music folder at '{root}': {ex.Message}", ex);
            }
        }
        throw new IOException("No writable Waveify music directory is available.");
    }

    public static string Organize(string sourcePath, Song song)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Downloaded audio file not found", sourcePath);
        string artist = Clean(song.Artist, "Unknown Artist");
        string album = Clean(song.Album, "Unknown Album");
        string title = Clean(song.Title, "Unknown Song");
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext)) ext = ".audio";

        string directory = PrepareDirectory(artist, album);
        string desired = Path.Combine(directory, title + ext);
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(desired), StringComparison.OrdinalIgnoreCase))
            return desired;

        string destination = desired;
        for (int i = 2; File.Exists(destination); i++)
        {
            if (i > 9999) throw new IOException("Too many tracks share this filename.");
            destination = Path.Combine(directory, $"{title} ({i}){ext}");
            if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                return destination;
        }
        try
        {
            // Double-check the destination parent immediately before copying.
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination, overwrite: false);
            if (!File.Exists(destination))
                throw new IOException("File copy completed but destination is missing.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Audio copy failed. Source='{sourcePath}', " +
                $"Destination='{destination}', ParentExists={Directory.Exists(directory)}. " +
                $"{ex.GetType().Name}: {ex.Message}", ex);
        }
        return destination;
    }

    public static void SaveAlbumArtwork(Song song)
    {
        if (!File.Exists(song.FilePath) || !File.Exists(song.ArtworkPath)) return;
        string directory = Path.GetDirectoryName(song.FilePath)!;
        string cover = Path.Combine(directory, "cover" + Path.GetExtension(song.ArtworkPath).ToLowerInvariant());
        if (!File.Exists(cover)) File.Copy(song.ArtworkPath, cover);
    }

    static string Clean(string? input, string fallback)
    {
        string value = string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
        // Replace invalid filenames and Windows path separators; preserve Unicode artist names.
        var forbidden = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var buffer = new StringBuilder();
        foreach (char ch in value)
            buffer.Append(forbidden.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        value = buffer.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(value)) value = fallback;
        // Windows reserved device names cannot be used for folder or file basenames.
        string stem = value.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem))
            value = "_" + value;
        return value.Length <= 100 ? value : value[..100].TrimEnd(' ', '.');
    }
}
