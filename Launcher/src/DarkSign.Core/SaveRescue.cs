namespace DarkSign.Core;

/// <summary>
/// Gets a save out of a Dark Souls III map that crashes on load. Elden Ring keeps the player's last map in the save
/// and loads it before any event script runs, so a save left in a ported map (the colosseum slots m45_00-02, or the
/// m50-m52 new-ID slots) crashes on every load until that map loads again. "Rescue save" starts the game once with
/// the ported maps hidden (moved out of the mod folder): the save then loads the game's own colosseum arena under
/// the same map ID, the player falls or rests back to an Elden Ring grace, and Ctrl+Shift+F2 (Leave DS3 run) warps
/// them to Roundtable Hold. The next normal PLAY or rebuild puts the files back first (<see cref="Restore"/>).
///
/// Only files port-maps writes are moved: the slot layouts (msb), the map folders (models, textures, collision,
/// navmesh) and the slots' event scripts. common.emevd stays, so the run's leave warp still works, and so do the
/// weapons and enemies. A map ID the game doesn't ship (m50-m52) has no arena to fall back to, so a save left there
/// can't be rescued this way; the launcher says so.
/// </summary>
public static class SaveRescue
{
    public const string HiddenFolderName = "rescue-hidden-maps";

    /// <summary>The ported map IDs: colosseum slots and the new-ID slots (Porter MapSlots).</summary>
    public static readonly string[] MapIds = ["m45_00_00_00", "m45_01_00_00", "m45_02_00_00", "m50_00_00_00", "m51_00_00_00", "m52_00_00_00"];

    /// <summary>Where the hidden files wait: TheDarkSign\Work\rescue-hidden-maps, mirroring the mod folder.</summary>
    public static string HiddenDir(string root) => Path.Combine(root, "TheDarkSign", "Work", HiddenFolderName);

    /// <summary>Mod-relative paths (files or folders) of everything a rescue hides.</summary>
    public static IEnumerable<string> RelativePaths()
    {
        foreach (var id in MapIds)
        {
            yield return Path.Combine("map", "mapstudio", $"{id}.msb.dcx");
            yield return Path.Combine("event", $"{id}.emevd.dcx");
        }
        foreach (var area in MapIds.Select(id => id[..3]).Distinct())
            yield return Path.Combine("map", area);
    }

    /// <summary>Moves the ported maps out of the mod folder; returns what was moved (mod-relative).</summary>
    public static List<string> Hide(string modDir, string hiddenDir)
    {
        var moved = new List<string>();
        foreach (var rel in RelativePaths())
        {
            var from = Path.Combine(modDir, rel);
            var to = Path.Combine(hiddenDir, rel);
            if (Directory.Exists(from))
            {
                if (Directory.Exists(to))
                    MergeInto(to, from); // an older rescue's copy: bring it back first so nothing is lost
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
                moved.Add(rel);
            }
            else if (File.Exists(from))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to, overwrite: true);
                moved.Add(rel);
            }
        }
        return moved;
    }

    /// <summary>Puts hidden maps back; returns what came back (mod-relative). Nothing hidden: nothing happens.</summary>
    public static List<string> Restore(string modDir, string hiddenDir)
    {
        var restored = new List<string>();
        if (!Directory.Exists(hiddenDir))
            return restored;
        foreach (var rel in RelativePaths())
        {
            var hidden = Path.Combine(hiddenDir, rel);
            var to = Path.Combine(modDir, rel);
            if (Directory.Exists(hidden))
            {
                if (Directory.Exists(to))
                    MergeInto(hidden, to); // a rebuild wrote new files meanwhile: the new ones win
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    Directory.Move(hidden, to);
                }
                restored.Add(rel);
            }
            else if (File.Exists(hidden))
            {
                if (File.Exists(to))
                    File.Delete(hidden);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(hidden, to);
                }
                restored.Add(rel);
            }
        }
        if (Directory.Exists(hiddenDir) && !Directory.EnumerateFiles(hiddenDir, "*", SearchOption.AllDirectories).Any())
            Directory.Delete(hiddenDir, recursive: true);
        return restored;
    }

    /// <summary>True while a rescue has the maps hidden (the next PLAY restores them).</summary>
    public static bool IsHidden(string hiddenDir) =>
        Directory.Exists(hiddenDir) && Directory.EnumerateFiles(hiddenDir, "*", SearchOption.AllDirectories).Any();

    /// <summary>
    /// Moves every file under <paramref name="source"/> into <paramref name="target"/>, keeping the target's own
    /// copy of a file both hold, then removes the source.
    /// </summary>
    private static void MergeInto(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList())
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            if (File.Exists(dest))
                File.Delete(file);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest);
            }
        }
        Directory.Delete(source, recursive: true);
    }
}
