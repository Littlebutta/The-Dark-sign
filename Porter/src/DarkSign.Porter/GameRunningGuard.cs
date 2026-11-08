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
    public static string? Problem(string command)
    {
        if (!WritingCommands.Contains(command))
            return null;
        int running;
        try
        {
            running = Process.GetProcessesByName("eldenring").Length;
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
}
