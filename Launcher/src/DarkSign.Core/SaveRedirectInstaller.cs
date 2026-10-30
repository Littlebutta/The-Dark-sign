using System.Text;

namespace DarkSign.Core;

public enum RedirectState
{
    /// <summary>The current DLL is in place, or the game needs none.</summary>
    Installed,
    /// <summary>An older build of our DLL is in place.</summary>
    Outdated,
    Missing,
    /// <summary>Another mod already uses dinput8.dll in this game folder.</summary>
    Conflict,
}

/// <summary>
/// Puts dksign_saveredirect.dll next to a game exe as dinput8.dll, which the three Souls games
/// load at start. Only game folders inside the project root are ever written.
/// </summary>
public static class SaveRedirectInstaller
{
    public const string ProxyName = "dinput8.dll";

    // Exported by the DLL, so its name is in every build's export table.
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("DkSignSaveRedirectVersion");

    public static string ProxyPath(string exePath) => Path.Combine(Path.GetDirectoryName(exePath)!, ProxyName);

    public static RedirectState Check(GameId game, string exePath, byte[]? dll)
    {
        if (GameDefinition.Get(game).SaveRedirect != SaveRedirectMethod.ProxyDll)
            return RedirectState.Installed;

        var path = ProxyPath(exePath);
        if (!File.Exists(path))
            return RedirectState.Missing;

        var existing = File.ReadAllBytes(path);
        if (!IsOurs(existing))
            return RedirectState.Conflict;
        return dll is null || existing.AsSpan().SequenceEqual(dll) ? RedirectState.Installed : RedirectState.Outdated;
    }

    /// <summary>Installs or updates the DLL. Never replaces another mod's dinput8.dll.</summary>
    public static RedirectState Install(GameId game, string exePath, byte[] dll, string root)
    {
        var state = Check(game, exePath, dll);
        if (state is RedirectState.Missing or RedirectState.Outdated)
        {
            var path = ProxyPath(exePath);
            PathGuard.EnsureInsideRoot(path, root);
            File.WriteAllBytes(path, dll);
            state = RedirectState.Installed;
        }
        return state;
    }

    internal static bool IsOurs(ReadOnlySpan<byte> file) => file.IndexOf(Marker) >= 0;
}
