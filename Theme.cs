using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Waveify;

internal static class Theme
{
    public static Brush AppBg => Brush("WaveAppBg", Color.FromRgb(9, 13, 20));
    public static Brush Panel => Brush("WavePanel", Color.FromRgb(18, 25, 35));
    public static Brush Stroke => Brush("WaveStroke", Color.FromRgb(34, 42, 55));
    public static Brush Muted => Brush("WaveMuted", Color.FromRgb(168, 176, 192));
    public static Brush Accent => Brush("WaveAccent", Color.FromRgb(255, 49, 95));
    public static Brush Text => new SolidColorBrush(Color.FromRgb(242, 245, 251));
    public static Brush ControlBg => new SolidColorBrush(Color.FromRgb(27, 32, 43));

    public static void ApplyWindow(Window window)
    {
        window.Background = AppBg;
        window.Foreground = Text;
        window.FontFamily = new FontFamily("Segoe UI");
    }

    public static void StyleTextBox(TextBox textBox)
    {
        textBox.Background = ControlBg;
        textBox.Foreground = Text;
        textBox.BorderBrush = Stroke;
        textBox.BorderThickness = new Thickness(1);
        textBox.Padding = new Thickness(12, 8, 12, 8);
    }

    public static void StylePrimaryButton(Button button)
    {
        button.Background = Accent;
        button.Foreground = Brushes.White;
        button.BorderBrush = Accent;
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(14, 7, 14, 7);
    }

    public static void StyleSecondaryButton(Button button)
    {
        button.Background = ControlBg;
        button.Foreground = Text;
        button.BorderBrush = Stroke;
        button.BorderThickness = new Thickness(1);
        button.Padding = new Thickness(14, 7, 14, 7);
    }

    private static Brush Brush(string key, Color fallback)
    {
        return Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(fallback);
    }
}
