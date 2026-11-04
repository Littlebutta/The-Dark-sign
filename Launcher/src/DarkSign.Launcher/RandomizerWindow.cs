using System.Windows;
using System.Windows.Controls;
using DarkSign.Core;

namespace DarkSign.Launcher;

/// <summary>
/// The profile's randomizer settings. Each profile (and so each character's save) has its own seed and choices.
/// "New randomized character" makes a new profile with a fresh seed, so starting another randomized run never
/// touches an existing one.
/// </summary>
internal static class RandomizerWindow
{
    public enum Result { Cancelled, Saved, NewCharacter }

    public static Result Show(Window owner, string profileName, RandomizerSettings settings)
    {
        var enabled = Check("Randomize this profile's world", settings.Enabled);
        var seedBox = new TextBox { Text = settings.Seed == 0 ? RandomizerSettings.NewSeed().ToString() : settings.Seed.ToString(), MinWidth = 160 };
        var reroll = new Button { Content = "New seed", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        reroll.Click += (_, _) => seedBox.Text = RandomizerSettings.NewSeed().ToString();
        var fogs = Check("Fog walls lead somewhere else, across both games", settings.Entrances);
        var items = Check("Shuffle items", settings.Items);
        var crossItems = Check("…mixing Dark Souls III and Elden Ring items", settings.CrossGameItems);
        var bosses = Check("Shuffle bosses across both games", settings.Bosses);
        var enemies = Check("Shuffle enemies across both games", settings.Enemies);
        var shards = Check("Great Runes and Lords of Cinder count for both games' gates", settings.SharedShards);
        var dlc = Check("Include DLC areas", settings.Dlc);
        var goal = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 4, 0, 0) };
        var goals = new[] { ("Both", "Beat the Elden Beast and the Soul of Cinder"), ("EldenBeast", "Beat the Elden Beast"), ("SoulOfCinder", "Beat the Soul of Cinder") };
        foreach (var (_, text) in goals) goal.Items.Add(text);
        goal.SelectedIndex = Math.Max(0, Array.FindIndex(goals, g => g.Item1 == settings.Goal));

        var seedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        seedRow.Children.Add(new TextBlock { Text = "Seed ", VerticalAlignment = VerticalAlignment.Center });
        seedRow.Children.Add(seedBox);
        seedRow.Children.Add(reroll);

        var save = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 4, 16, 4) };
        var newChar = new Button { Content = "New randomized character…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(newChar);

        var panel = new StackPanel { Margin = new Thickness(16), MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = $"Profile \"{profileName}\"", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Every seed is checked before it is used: the game can always be finished. Each profile keeps its own " +
                "world, so switching profiles never rebuilds anything. Stage 1: the plan and spoiler log are made, " +
                "but the game itself isn't changed yet.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Opacity = 0.8, Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (var c in new UIElement[] { enabled, seedRow, fogs, items, crossItems, bosses, enemies, shards, dlc, new TextBlock { Text = "Goal", Margin = new Thickness(0, 8, 0, 0) }, goal, buttons })
            panel.Children.Add(c);

        var window = new Window
        {
            Title = "Cross-game randomizer",
            Owner = owner,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var result = Result.Cancelled;
        void Store()
        {
            settings.Enabled = enabled.IsChecked == true;
            settings.Seed = long.TryParse(seedBox.Text.Trim(), out var s) && s > 0 ? s : RandomizerSettings.NewSeed();
            settings.Entrances = fogs.IsChecked == true;
            settings.Items = items.IsChecked == true;
            settings.CrossGameItems = crossItems.IsChecked == true;
            settings.Bosses = bosses.IsChecked == true;
            settings.Enemies = enemies.IsChecked == true;
            settings.SharedShards = shards.IsChecked == true;
            settings.Dlc = dlc.IsChecked == true;
            settings.Goal = goals[Math.Max(0, goal.SelectedIndex)].Item1;
        }
        save.Click += (_, _) => { Store(); result = Result.Saved; window.DialogResult = true; };
        newChar.Click += (_, _) => { Store(); result = Result.NewCharacter; window.DialogResult = true; };
        window.ShowDialog();
        return result;
    }

    private static CheckBox Check(string text, bool value) =>
        new() { Content = text, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
}
