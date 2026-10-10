using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Waveify;

public static class BatchImportDialog
{
    public static string[]? Ask(Window owner)
    {
        var window = new Window
        {
            Owner = owner, Title = "Batch music import", Width = 660, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        Theme.ApplyWindow(window);
        var panel = new DockPanel { Margin = new Thickness(22) };
        var info = new TextBlock
        {
            Text = "Paste one HTTPS URL per line. YouTube and SoundCloud tracks/playlists require yt-dlp + FFmpeg and permission to download. Spotify tracks/albums/playlists use the optional Sunnify CLI.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,15), Foreground = Theme.Muted
        };
        DockPanel.SetDock(info, Dock.Top); panel.Children.Add(info);
        var button = new Button { Content = "Import URLs", Height = 40, Margin = new Thickness(0,12,0,0) };
        Theme.StylePrimaryButton(button);
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        var entry = new TextBox
        {
            AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap, FontSize = 14
        };
        Theme.StyleTextBox(entry);
        panel.Children.Add(entry);
        string[]? result = null;
        button.Click += (_, _) =>
        {
            result = entry.Text.Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !s.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            window.DialogResult = true;
        };
        window.Content = panel;
        return window.ShowDialog() == true ? result : null;
    }
}
