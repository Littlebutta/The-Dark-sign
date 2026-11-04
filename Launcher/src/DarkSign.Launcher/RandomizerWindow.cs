using System.Windows;
using System.Windows.Controls;
using DarkSign.Core;

namespace DarkSign.Launcher;

/// <summary>
/// The profile's randomizer settings. Each profile (and so each character's save) has its own seed and choices.
/// "New randomized character" makes a new profile with a fresh seed, so starting another randomized run never
/// touches an existing one. The co-op part shares this world's code with friends, or joins a friend's world by
/// pasting theirs (this profile then loads the friend's world until the code is cleared; its own seed is kept).
/// </summary>
internal static class RandomizerWindow
{
    public enum Result { Cancelled, Saved, NewCharacter }

    public static Result Show(Window owner, string profileName, RandomizerSettings settings, string? joinCodeIn, out string? joinCode)
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

        // Co-op: this world's code (follows the choices above as they change), and a friend's code to join.
        var myCode = new TextBox { IsReadOnly = true, MinWidth = 260, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        var copy = new Button { Content = "Copy", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        copy.Click += (_, _) => { try { Clipboard.SetText(myCode.Text); } catch (System.Runtime.InteropServices.ExternalException) { } };
        var joinBox = new TextBox { Text = joinCodeIn ?? "", MinWidth = 260, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        var leave = new Button { Content = "Back to my world", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        leave.Click += (_, _) => joinBox.Text = "";
        var joinState = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 0) };
        void RefreshJoin() => joinState.Text = joinBox.Text.Trim().Length == 0
            ? "Playing your own world. To play in a friend's world, paste their co-op code here."
            : RandomizerCoop.Decode(joinBox.Text) is { } friend
                ? $"Joining a friend's world (seed {friend.Seed}). Your own seed is kept for when you go back."
                : "That isn't a valid co-op code; check it for typos.";
        joinBox.TextChanged += (_, _) => RefreshJoin();
        RefreshJoin();
        StackPanel Row(params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var i in items) row.Children.Add(i);
            return row;
        }

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
        var coopTitle = new TextBlock { Text = "Playing with friends (Seamless Co-op)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) };
        var coopHelp = new TextBlock
        {
            Text = "Everyone keeps their own seed. To play together, everyone loads the host's world: the host sends " +
                "their co-op code, friends paste it below, and the launcher makes that world on their PC. The mod code " +
                "on the main window then matches for everyone in the same world.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 4),
        };
        foreach (var c in new UIElement[] { enabled, seedRow, fogs, items, crossItems, bosses, enemies, shards, dlc, new TextBlock { Text = "Goal", Margin = new Thickness(0, 8, 0, 0) }, goal,
                     coopTitle, coopHelp, new TextBlock { Text = "Your co-op code" }, Row(myCode, copy),
                     new TextBlock { Text = "Friend's co-op code", Margin = new Thickness(0, 6, 0, 0) }, Row(joinBox, leave), joinState, buttons })
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
        void Store() => StoreInto(settings);
        void RefreshMyCode()
        {
            var preview = new RandomizerSettings { CrossGameFogs = settings.CrossGameFogs, CrossGameBosses = settings.CrossGameBosses };
            StoreInto(preview);
            preview.Seed = long.TryParse(seedBox.Text.Trim(), out var s) && s > 0 ? s : settings.Seed;
            myCode.Text = preview.Seed > 0 ? RandomizerCoop.Encode(preview) : "";
        }
        void StoreInto(RandomizerSettings target)
        {
            target.Enabled = enabled.IsChecked == true;
            target.Seed = long.TryParse(seedBox.Text.Trim(), out var s) && s > 0 ? s : RandomizerSettings.NewSeed();
            target.Entrances = fogs.IsChecked == true;
            target.Items = items.IsChecked == true;
            target.CrossGameItems = crossItems.IsChecked == true;
            target.Bosses = bosses.IsChecked == true;
            target.Enemies = enemies.IsChecked == true;
            target.SharedShards = shards.IsChecked == true;
            target.Dlc = dlc.IsChecked == true;
            target.Goal = goals[Math.Max(0, goal.SelectedIndex)].Item1;
        }
        seedBox.TextChanged += (_, _) => RefreshMyCode();
        foreach (var box in new[] { fogs, items, crossItems, bosses, enemies, shards, dlc })
            box.Click += (_, _) => RefreshMyCode();
        goal.SelectionChanged += (_, _) => RefreshMyCode();
        RefreshMyCode();
        save.Click += (_, _) =>
        {
            if (joinBox.Text.Trim().Length > 0 && RandomizerCoop.Decode(joinBox.Text) is null)
            {
                MessageBox.Show(window, "The friend's co-op code isn't valid. Fix it, or clear it to play your own world.",
                    "Co-op code", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Store(); result = Result.Saved; window.DialogResult = true;
        };
        newChar.Click += (_, _) => { Store(); result = Result.NewCharacter; window.DialogResult = true; };
        window.ShowDialog();
        joinCode = RandomizerCoop.Decode(joinBox.Text) is not null ? RandomizerCoop.Normalize(joinBox.Text) : null;
        return result;
    }

    private static CheckBox Check(string text, bool value) =>
        new() { Content = text, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
}
