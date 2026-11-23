namespace DarkSign.Core;

/// <summary>
/// Environment variables the launcher passes to our game DLLs, and the files they share with the content build.
/// The launcher used to offer "fallback methods" (other ways of doing each feature) here; they were removed on
/// 2026-11-23 at Devin's request: a content build takes about an hour, so every build uses the current best method.
/// </summary>
public static class ModRuntime
{
    /// <summary>Game variable: the test menu DLL doesn't hook DirectX 12 (no overlay), hotkeys only.</summary>
    public const string NoOverlayVariable = "DKSIGN_NO_OVERLAY";

    /// <summary>Game variable: no hotkeys (the DLL is loaded only for another job and the test menu is off).</summary>
    public const string NoHotkeysVariable = "DKSIGN_NO_HOTKEYS";

    /// <summary>Game variable: the mod folder, for the DLL's files (DS3 music).</summary>
    public const string ModDirVariable = "DKSIGN_MOD_DIR";

    /// <summary>The music list port-maps writes into the mod folder (Porter Maps/MapMusic.cs ListFile).</summary>
    public const string MusicListFile = "darksign_music.tsv";

    /// <summary>True when the test menu DLL must be loaded to play DS3's music: the content has music (port-maps wrote its list).</summary>
    public static bool NeedsMusicDll(string modDir) => File.Exists(Path.Combine(modDir, MusicListFile));
}
