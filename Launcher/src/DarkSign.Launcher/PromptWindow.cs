using System.Windows;
using System.Windows.Controls;

namespace DarkSign.Launcher;

/// <summary>A minimal single-line text prompt (WPF has no built-in one).</summary>
internal static class PromptWindow
{
    public static string? Ask(Window owner, string title, string label)
    {
        var box = new TextBox { Margin = new Thickness(0, 6, 0, 10), MinWidth = 260 };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4, 16, 4) };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(ok);

        var window = new Window
        {
            Title = title,
            Owner = owner,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Text : null;
    }
}
