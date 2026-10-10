using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Waveify;

/// <summary>Finds previously imported tracks without changing or moving their files.</summary>
internal static class LibraryRecovery
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".flac", ".wav", ".aac", ".ogg", ".wma", ".opus" };

    public static List<Song> FindMissing(IEnumerable<string> existingPaths)
    {
        var known = new HashSet<string>(existingPaths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => { try { return Path.GetFullPath(p); } catch { return p; } }),
            StringComparer.OrdinalIgnoreCase);
        var found = new List<Song>();
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Waveify"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waveify", "Music")
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && found.Count < 20000)
            {
                var folder = pending.Pop();
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(folder))
                    {
                        // Ignore directory links/reparse points to prevent recursive loops.
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                            pending.Push(directory);
                    }
                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        if (!Extensions.Contains(Path.GetExtension(file))) continue;
                        var full = Path.GetFullPath(file);
                        if (!known.Add(full)) continue;
                        var song = new Song { FilePath = full, Title = Path.GetFileNameWithoutExtension(full) };
                        try
                        {
                            using var tagged = TagLib.File.Create(full);
                            if (!string.IsNullOrWhiteSpace(tagged.Tag.Title)) song.Title = tagged.Tag.Title;
                            song.Artist = tagged.Tag.FirstPerformer ?? "Unknown artist";
                            song.Album = tagged.Tag.Album ?? "";
                            song.Genre = tagged.Tag.FirstGenre ?? "";
                            song.DurationSeconds = (int)tagged.Properties.Duration.TotalSeconds;
                        }
                        catch { /* Keep playable file even if tags cannot be decoded. */ }
                        song.ArtworkPath = ArtworkResolver.FindLocalCover(song);
                        found.Add(song);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return found;
    }
}
