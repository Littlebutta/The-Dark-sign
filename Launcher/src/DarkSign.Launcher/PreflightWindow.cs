using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DarkSign.Core;

namespace DarkSign.Launcher;

/// <summary>
/// The "Pre-flight check" window: the fast checks (<see cref="Preflight.Run"/>) as soon as it opens, a button for the
/// deep ones (the porter reads the built mod back: params, animations, sounds and the game's sound index, maps and
/// flag lists), and "Save test report" to zip every log and report after a test (<see cref="Diagnostics"/>).
/// </summary>
internal static class PreflightWindow
{
    /// <param name="fast">Runs the fast checks.</param>
    /// <param name="deep">Runs the porter's checks on the built mod (validate-mod, map-validate); false if it couldn't.</param>
    /// <param name="saveReport">Writes the test report zip with the given pre-flight text and returns its path.</param>
    public static void Show(Window owner, Func<List<Preflight.Item>> fast, Func<Action<string>, Task<bool>> deep,
        Func<string?, string> saveReport)
    {
        var list = new StackPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 8, 0, 0) };
        List<Preflight.Item> items = [];

        void Fill()
        {
            items = fast();
            list.Children.Clear();
            string? area = null;
            foreach (var item in items.OrderBy(i => AreaOrder(items, i.Area)))
            {
                if (item.Area != area)
                {
                    area = item.Area;
                    list.Children.Add(new TextBlock
                    {
                        Text = area.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, Opacity = 0.7,
                        Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 10, 0, 2),
                    });
                }
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var tag = new TextBlock
                {
                    Text = Preflight.Tag(item.Level), Width = 44, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(item.Level switch
                    {
                        Preflight.Level.Ok => Color.FromRgb(0x6F, 0xB5, 0x6F),
                        Preflight.Level.Info => Color.FromRgb(0x8A, 0x9B, 0xB8),
                        Preflight.Level.Warn => Color.FromRgb(0xD9, 0xA4, 0x41),
                        _ => Color.FromRgb(0xE0, 0x5A, 0x4F),
                    }),
                };
                DockPanel.SetDock(tag, Dock.Left);
                row.Children.Add(tag);
                row.Children.Add(new TextBox
                {
                    Text = item.Text, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent, Foreground = (Brush)Application.Current.FindResource("TextBrush"),
                });
                list.Children.Add(row);
            }
            int fails = items.Count(i => i.Level == Preflight.Level.Fail), warns = items.Count(i => i.Level == Preflight.Level.Warn);
            status.Text = fails == 0 && warns == 0 ? "Nothing wrong found. Ready to test."
                : $"{fails} problem{(fails == 1 ? "" : "s")} and {warns} warning{(warns == 1 ? "" : "s")}. " +
                  "FAIL lines will break the test; fix them (or switch the feature in Fallbacks…) before playing.";
        }

        var deepButton = new Button { Content = "Check the built mod too", Padding = new Thickness(12, 4, 12, 4) };
        var report = new Button { Content = "Save test report (zip)", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        deepButton.ToolTip = "The porter reads the built mod back (params, animations, sounds and the game's sound index, the DS3 maps and " +
                             "event flag lists). Takes a minute or two.";
        report.ToolTip = "After a test: puts every log and report (launcher, porter, me3, save redirect, F1 menu, moveset timing…) " +
                         "into one zip in TheDarkSign\\Diagnostics. No saves or game files.";
        deepButton.Click += async (_, _) =>
        {
            deepButton.IsEnabled = false;
            status.Text = "Checking the built mod…";
            var ok = await deep(line => owner.Dispatcher.Invoke(() => status.Text = "Checking the built mod… " + line));
            Fill();
            if (!ok)
                status.Text = "The porter's checks didn't finish (see TheDarkSign\\Work\\logs). " + status.Text;
            deepButton.IsEnabled = true;
        };
        report.Click += (_, _) =>
        {
            try
            {
                var path = saveReport(Preflight.Format(items));
                status.Text = $"Saved {path}. Send this file to Claude.";
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IsolationViolationException)
            {
                status.Text = "Couldn't save the report: " + ex.Message;
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(deepButton);
        buttons.Children.Add(report);
        buttons.Children.Add(close);
        var intro = new TextBlock
        {
            Text = "Checks everything that can be checked before the game starts. After a test, press \"Save test report\" " +
                   "and send the zip: it has every log and report, so nothing needs describing.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 8),
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(intro);
        root.Children.Add(buttons);
        root.Children.Add(status);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var window = new Window
        {
            Style = Theme.WindowStyle,
            Title = "Pre-flight check",
            Owner = owner,
            Content = root,
            Width = 760,
            Height = Math.Min(720, SystemParameters.WorkArea.Height - 40),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Fill();
        window.ShowDialog();
    }

    private static int AreaOrder(List<Preflight.Item> items, string area) => items.FindIndex(i => i.Area == area);
}
