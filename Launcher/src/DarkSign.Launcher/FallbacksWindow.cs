using System.Windows;
using System.Windows.Controls;
using DarkSign.Core;

namespace DarkSign.Launcher;

/// <summary>
/// One drop-down per feature (<see cref="Fallbacks.All"/>): A is the default, B and C are other ways of doing the same
/// thing, "off" turns the feature back to plain Elden Ring. The help under each drop-down says what the chosen method
/// does and when to try the next one. Build methods rebuild the content on the next PLAY.
/// </summary>
internal static class FallbacksWindow
{
    public static bool Show(Window owner, IReadOnlyDictionary<string, string> current, out Dictionary<string, string> methods)
    {
        var boxes = new List<(Fallbacks.Feature Feature, ComboBox Box)>();
        var list = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        string? group = null;
        foreach (var feature in Fallbacks.All)
        {
            if (feature.Group != group)
            {
                group = feature.Group;
                list.Children.Add(new TextBlock
                {
                    Text = group.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, Opacity = 0.7,
                    Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 14, 0, 2),
                });
            }
            var box = new ComboBox { MinWidth = 300, Margin = new Thickness(0, 2, 0, 0) };
            foreach (var option in feature.Options)
                box.Items.Add($"{option.Letter}: {option.Title}");
            var chosen = Fallbacks.Chosen(current, feature);
            box.SelectedIndex = Math.Max(0, feature.Options.ToList().IndexOf(chosen));
            var help = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Opacity = 0.8, FontSize = 12 };
            void RefreshHelp() => help.Text = feature.Options[Math.Max(0, box.SelectedIndex)].Help;
            box.SelectionChanged += (_, _) => RefreshHelp();
            RefreshHelp();

            var title = new TextBlock
            {
                Text = feature.Title + (feature.Stage == Fallbacks.Stage.Build ? "" : "  (at launch, no rebuild)"),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0),
            };
            list.Children.Add(title);
            list.Children.Add(new TextBlock
            {
                Text = "Try the next one if: " + feature.Symptom, TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
                FontSize = 12, Opacity = 0.65,
            });
            list.Children.Add(box);
            list.Children.Add(help);
            boxes.Add((feature, box));
        }

        var save = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 4, 16, 4) };
        var reset = new Button { Content = "All back to A", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        reset.Click += (_, _) => { foreach (var (_, box) in boxes) box.SelectedIndex = 0; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);

        var intro = new TextBlock
        {
            Text = "Each feature can be done more than one way. A is the default. If a feature doesn't work in game, " +
                   "switch it to B, then C, and play again; \"off\" turns that feature back to plain Elden Ring, to tell whether " +
                   "it is what breaks the game. Change one feature at a time. Build methods rebuild the content on the " +
                   "next PLAY (a few minutes). Co-op partners need the same build methods (the mod code shows it).",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 580, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 8),
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(intro);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var window = new Window
        {
            Style = Theme.WindowStyle,
            Title = "Fallback methods",
            Owner = owner,
            Content = root,
            Width = 640,
            Height = Math.Min(760, SystemParameters.WorkArea.Height - 40),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        save.Click += (_, _) => window.DialogResult = true;
        var ok = window.ShowDialog() == true;
        methods = new Dictionary<string, string>();
        foreach (var (feature, box) in boxes)
            if (box.SelectedIndex > 0)
                methods[feature.Key] = feature.Options[box.SelectedIndex].Letter;
        return ok;
    }
}
