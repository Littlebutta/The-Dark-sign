using System.Diagnostics;

namespace DarkSign.Porter;

/// <summary>
/// Elden Ring keeps mod files open while it runs (menu/hi/00_solo.tpfbdt above all), so a build started then fails
/// halfway: on 2026-11-07 a port-weapons run made with the game open could rewrite menu/low but not menu/hi, and its
/// clean-up deleted hi's sheet, layouts and header while the locked .tpfbdt stayed. On the next start the game read its
/// own hi menus (no DS3 icon sheet) against our .tpfbdt, and the DS3 weapon icons were gone. The launcher already waits
/// for the game to close; this guard covers every other way the porter is started (by hand, scripts, test runs).
/// </summary>
public static class GameRunningGuard
{
    /// <summary>Commands that write TheDarkSign\Mods\ER.</summary>
    public static readonly HashSet<string> WritingCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "port-weapons", "port-maps", "port-enemies", "randomize",
    };

    /// <summary>The message to stop with when Elden Ring is running and the command writes the mod, else null.</summary>
    /// <param name="root">The --root the command writes under, or null for the usual one. A game running from outside
    /// it (a build into a scratch folder while the real game is open) holds none of the files the command writes;
    /// a game whose path can't be read counts as inside.</param>
    public static string? Problem(string command, string? root = null)
    {
        if (!WritingCommands.Contains(command))
            return null;
        int running;
        try
        {
            var games = Process.GetProcessesByName("eldenring");
            running = root is null ? games.Length : games.Count(g => Inside(g, Path.GetFullPath(root)));
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
        return running == 0 ? null :
            $"error: Elden Ring is running and keeps the mod files open, so {command} would leave TheDarkSign\\Mods\\ER " +
            "half-written (that is what made the DS3 weapon icons vanish). Close the game completely, check Task Manager " +
            "for eldenring.exe, then run it again.";
    }

    private static bool Inside(Process game, string root)
    {
        try
        {
            var exe = game.MainModule?.FileName;
            return exe is null || Path.GetFullPath(exe).StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return true;
        }
    }
}
