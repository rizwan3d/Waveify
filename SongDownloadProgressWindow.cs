using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Waveify;

internal sealed class SongDownloadProgressWindow : Window
{
    private readonly TextBlock source = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock detail = new() { Margin = new Thickness(0, 8, 0, 12), Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar track = new() { Minimum = 0, Maximum = 100, Height = 18, IsIndeterminate = true };
    private readonly ProgressBar batch = new() { Minimum = 0, Maximum = 100, Height = 8 };
    private readonly TextBlock batchLabel = new() { Margin = new Thickness(0, 12, 0, 6) };
    public SongDownloadProgressWindow(Window owner, int count)
    {
        Owner = owner; Title = "Waveify — Importing songs";
        Width = 500; Height = 265; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize; Theme.ApplyWindow(this);
        track.Foreground = Theme.Accent;
        batch.Foreground = track.Foreground;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Downloading songs", FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Theme.Text, Margin = new Thickness(0,0,0,16) });
        panel.Children.Add(source); panel.Children.Add(track); panel.Children.Add(detail);
        panel.Children.Add(batchLabel); panel.Children.Add(batch);
        Content = panel;
        SetBatch(0, count, "Preparing downloads");
        // Prevent accidental closure while a subprocess is running.
        Closing += (_, args) => { if (IsVisible && !completed) args.Cancel = true; };
    }
    private bool completed;
    public void SetBatch(int index, int count, string url)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetBatch(index, count, url)); return; }
        source.Text = $"URL {Math.Min(index + 1, count)} of {count}: {url}";
        batchLabel.Text = $"Batch progress: {index} / {count} URLs";
        batch.Value = count == 0 ? 0 : 100.0 * index / count;
        track.IsIndeterminate = true; detail.Text = "Connecting…";
    }
    public void Report(SongDownloadProgress info)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Report(info)); return; }
        source.Text = $"{info.Provider}: {info.Track}";
        track.IsIndeterminate = info.Percent is null;
        if (info.Percent is double value) track.Value = Math.Clamp(value, 0, 100);
        detail.Text = (info.Percent is double percent ? $"{percent:F1}% · " : "") + info.Status;
    }
    public void Finish(int count)
    {
        completed = true; batch.Value = 100; batchLabel.Text = $"Finished {count} URL(s)";
        Close();
    }
}

internal sealed record SongDownloadProgress(string Provider, string Track, double? Percent, string Status);
