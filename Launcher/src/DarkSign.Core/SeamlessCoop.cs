using System.Security.Cryptography;

namespace DarkSign.Core;

/// <summary>
/// Optional Seamless Co-op (LukeYui, nexusmods.com/eldenring/mods/510) for Elden Ring. The launcher never ships it:
/// the player points it at the ersc.dll from their own download, and me3 loads it as a native before our DLLs.
/// Seamless reads ersc_settings.ini from its own folder, so the password and scaling stay the player's own.
/// See docs/seamless-coop-compatibility.md.
/// </summary>
public static class SeamlessCoop
{
    public const string DllName = "ersc.dll";
    public const string SettingsName = "ersc_settings.ini";

    /// <summary>Set to "1" for a co-op launch: the save redirect then leaves sockets open for Seamless.</summary>
    public const string CoopVariable = "DKSIGN_COOP";

    /// <summary>Why <paramref name="dllPath"/> can't be used, or null when it can.</summary>
    public static string? Problem(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            return "Choose the ersc.dll from your Seamless Co-op download.";
        if (!string.Equals(Path.GetFileName(dllPath), DllName, StringComparison.OrdinalIgnoreCase))
            return $"Seamless Co-op's DLL is called {DllName}.";
        if (!File.Exists(dllPath))
            return $"{dllPath} was not found.";
        return null;
    }

    /// <summary>The co-op password from ersc_settings.ini next to the DLL ("" when none is set or the file is missing).</summary>
    public static string Password(string dllPath)
    {
        var ini = Path.Combine(Path.GetDirectoryName(dllPath) ?? "", SettingsName);
        if (!File.Exists(ini))
            return "";
        var section = "";
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                section = line[1..^1].Trim();
            else if (section.Equals("PASSWORD", StringComparison.OrdinalIgnoreCase) && line.Split('=', 2) is [var key, var value]
                     && key.Trim().Equals("cooppassword", StringComparison.OrdinalIgnoreCase))
                return value.Trim();
        }
        return "";
    }

    /// <summary>
    /// A short code for the built regulation.bin. Seamless needs every player's regulation.bin to be identical,
    /// so friends compare this code before playing together (null when the content isn't built).
    /// </summary>
    public static string? Fingerprint(string modDir)
    {
        var regulation = Path.Combine(modDir, "regulation.bin");
        if (!File.Exists(regulation))
            return null;
        using var stream = File.OpenRead(regulation);
        return Convert.ToHexString(SHA256.HashData(stream))[..8];
    }

    /// <summary>The me3 natives in load order: Seamless first (as its own docs list it), then ours.</summary>
    public static List<string> Natives(string? seamlessDll, IEnumerable<string> ours)
    {
        var list = new List<string>();
        if (seamlessDll is not null)
            list.Add(seamlessDll);
        list.AddRange(ours);
        return list;
    }
}
