using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Waveify;

public sealed class PlayerSettings
{
    public double Volume { get; set; } = 0.75;
    public string? OutputDeviceId { get; set; }
    public Guid? LastTrackId { get; set; }
    public double LastPositionSeconds { get; set; }
    public int RepeatMode { get; set; }
    public string ArtistFilter { get; set; } = "";
    public string AlbumFilter { get; set; } = "";
    public string GenreFilter { get; set; } = "";
    public string YearFilter { get; set; } = "";
    public bool LikedFilter { get; set; }
    public bool LocalFilter { get; set; }
    public string? MusicLibraryPath { get; set; }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Waveify", "player-settings.json");

    public static PlayerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var result = JsonSerializer.Deserialize<PlayerSettings>(
                    File.ReadAllText(SettingsPath));
                if (result != null)
                {
                    result.Volume = double.IsFinite(result.Volume)
                        ? Math.Clamp(result.Volume, 0, 1) : 0.75;
                    result.LastPositionSeconds = double.IsFinite(result.LastPositionSeconds)
                        ? Math.Max(0, result.LastPositionSeconds) : 0;
                    result.RepeatMode = Math.Clamp(result.RepeatMode, 0, 2);
                    if (!string.IsNullOrWhiteSpace(result.MusicLibraryPath))
                    {
                        try { result.MusicLibraryPath = Path.GetFullPath(result.MusicLibraryPath); }
                        catch { result.MusicLibraryPath = null; }
                    }
                    return result;
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine("Player settings load: " + ex); }
        return new PlayerSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch (Exception ex) { Debug.WriteLine("Player settings save: " + ex); }
    }
}
