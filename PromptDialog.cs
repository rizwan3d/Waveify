using System.Windows;
using System.Windows.Controls;
namespace Waveify;
public static class PromptDialog
{
    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var window = new Window { Title = title, Owner = owner, Width = 500, Height = 190, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        Theme.ApplyWindow(window);
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = label, Foreground = Theme.Muted, Margin = new Thickness(0, 0, 0, 10) });
        var input = new TextBox { Text = initial };
        Theme.StyleTextBox(input);
        stack.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 86, Margin = new Thickness(0, 0, 8, 0) };
        Theme.StyleSecondaryButton(cancel);
        cancel.Click += (_, _) => window.DialogResult = false;
        var ok = new Button { Content = "OK", MinWidth = 86, IsDefault = true };
        Theme.StylePrimaryButton(ok);
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok); stack.Children.Add(buttons);
        window.Content = stack;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text : null;
    }
}
