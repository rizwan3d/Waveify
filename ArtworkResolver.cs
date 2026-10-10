using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace Waveify;

/// <summary>Resolve artwork from saved images, album folders, then embedded audio tags.</summary>
internal static class ArtworkResolver
{
    private static readonly string[] CoverNames = { "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png" };

    public static string FindLocalCover(Song song)
    {
        if (!string.IsNullOrWhiteSpace(song.ArtworkPath) && File.Exists(song.ArtworkPath))
            return song.ArtworkPath;
        if (string.IsNullOrWhiteSpace(song.FilePath) || !File.Exists(song.FilePath)) return "";
        var folder = Path.GetDirectoryName(song.FilePath);
        if (folder == null) return "";
        foreach (var name in CoverNames)
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }

    public static BitmapImage? Load(Song song)
    {
        var cover = FindLocalCover(song);
        if (cover.Length > 0)
        {
            var image = LoadFile(cover);
            if (image != null) return image;
        }
        if (!string.IsNullOrWhiteSpace(song.FilePath) && File.Exists(song.FilePath))
        {
            try
            {
                using var audio = TagLib.File.Create(song.FilePath);
                var picture = audio.Tag.Pictures.FirstOrDefault(p => p?.Data?.Count > 0);
                if (picture != null)
                {
                    using var data = new MemoryStream(picture.Data.Data, writable: false);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = data;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("Embedded artwork: " + ex.Message); }
        }
        if (Uri.TryCreate(song.ArtworkUrl, UriKind.Absolute, out var remote) &&
            (remote.Scheme == Uri.UriSchemeHttps || remote.Scheme == Uri.UriSchemeHttp))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = remote;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { }
        }
        return null;
    }

    private static BitmapImage? LoadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}
