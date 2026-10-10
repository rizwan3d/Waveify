using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Waveify;

public sealed class ToolSetupProgressWindow : Window
{
    private readonly ProgressBar downloadBar;
    private readonly ProgressBar overallBar;
    private readonly TextBlock stage;
    private readonly TextBlock transfer;
    private readonly Button cancelButton;
    private int currentIndex = 1;
    private int totalTools = 1;
    private bool finished;
    public CancellationTokenSource Cancellation { get; } = new();

    public ToolSetupProgressWindow()
    {
        Title = "Waveify — Installing download tools";
        Width = 485; Height = 265;
        MinWidth = 440; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.ApplyWindow(this);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Setting up Waveify tools", FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Theme.Text, Margin = new Thickness(0,0,0,14) });
        stage = new TextBlock { Text = "Preparing…", Margin = new Thickness(0,0,0,8) };
        panel.Children.Add(stage);
        downloadBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 19, Foreground = Theme.Accent };
        panel.Children.Add(downloadBar);
        transfer = new TextBlock { Text = "", Foreground = Theme.Muted, Margin = new Thickness(0,6,0,15) };
        panel.Children.Add(transfer);
        overallBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Foreground = Theme.Accent, Margin = new Thickness(0,0,0,12) };
        panel.Children.Add(overallBar);
        cancelButton = new Button { Content = "Cancel", Width = 110, Height = 30, HorizontalAlignment = HorizontalAlignment.Right };
        Theme.StyleSecondaryButton(cancelButton);
        cancelButton.Click += (_, _) => { if (finished) Close(); else { Cancellation.Cancel(); cancelButton.IsEnabled = false; stage.Text = "Cancelling…"; } };
        panel.Children.Add(cancelButton);
        Content = panel;
        Closing += (_, e) => { if (!finished) { e.Cancel = true; Cancellation.Cancel(); cancelButton.IsEnabled = false; stage.Text = "Cancelling…"; } };
    }

    public void SetCurrentTool(int index, int count, string name)
    {
        currentIndex = index;
        totalTools = Math.Max(1, count);
        stage.Text = $"{index}/{count} · {name} — preparing";
        downloadBar.Value = 0;
        downloadBar.IsIndeterminate = true;
        overallBar.Value = 100.0 * (index - 1) / totalTools;
        transfer.Text = "Connecting to release server…";
    }

    internal void Report(InstallProgress info)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Report(info)); return; }
        stage.Text = $"{currentIndex}/{totalTools} · {info.Tool} — {info.Stage}";
        if (info.Stage == "Downloading" && info.TotalBytes is > 0)
        {
            downloadBar.IsIndeterminate = false;
            downloadBar.Value = Math.Clamp(100.0 * info.DownloadedBytes / info.TotalBytes.Value, 0, 100);
            transfer.Text = $"{info.DownloadedBytes / 1048576.0:F1} / {info.TotalBytes.Value / 1048576.0:F1} MiB ({downloadBar.Value:F0}%)";
            overallBar.Value = 100.0 * (currentIndex - 1 + downloadBar.Value / 100.0) / totalTools;
        }
        else if (info.Stage == "Downloading")
        {
            downloadBar.IsIndeterminate = true;
            transfer.Text = $"{info.DownloadedBytes / 1048576.0:F1} MiB downloaded";
        }
        else
        {
            downloadBar.IsIndeterminate = info.Stage != "Installed";
            if (info.Stage == "Installed")
            {
                downloadBar.IsIndeterminate = false;
                downloadBar.Value = 100;
                overallBar.Value = 100.0 * currentIndex / totalTools;
                transfer.Text = "Installation complete";
            }
            else transfer.Text = info.Stage;
        }
    }

    public void Finish()
    {
        finished = true;
        cancelButton.IsEnabled = true;
        cancelButton.Content = "Close";
        stage.Text = Cancellation.IsCancellationRequested ? "Setup cancelled" : "Setup finished";
        downloadBar.IsIndeterminate = false;
        Cancellation.Dispose();
    }
}
