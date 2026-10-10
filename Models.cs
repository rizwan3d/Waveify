using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
namespace Waveify;
public class Song
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Unknown title";
    public string Artist { get; set; } = "Unknown artist";
    public string Album { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string ArtworkUrl { get; set; } = "";
    public string ArtworkPath { get; set; } = "";
    public string LyricsPath { get; set; } = "";
    public uint Year { get; set; }
    public string Genre { get; set; } = "";
    public DateTime? DateAddedUtc { get; set; }
    public string MusicBrainzRecordingId { get; set; } = "";
    public string MusicBrainzReleaseId { get; set; } = "";
    public bool Liked { get; set; }
    public int DurationSeconds { get; set; }
    public string Duration => DurationSeconds > 0 ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"mm\:ss") : "—";
    public string Source => string.IsNullOrEmpty(FilePath) ? "Link" : "Local";
}
public class Playlist
{
    public string Name { get; set; } = "New Playlist";
    public List<Guid> SongIds { get; set; } = new();
    public override string ToString() => Name;
}
public class LibraryData
{
    public List<Song> Songs { get; set; } = new();
    public List<Playlist> Playlists { get; set; } = new();
    public List<Guid> PlaybackHistory { get; set; } = new();
}
public static class LibraryStore
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waveify");
    public static readonly string JsonPath = Path.Combine(Folder, "library.json");
    public static LibraryData Load()
    {
        Directory.CreateDirectory(Folder);
        try { return System.Text.Json.JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(JsonPath)) ?? new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }
    public static void Save(LibraryData data)
    {
        Directory.CreateDirectory(Folder);
        string temporary = JsonPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, JsonPath, true);
    }
}
