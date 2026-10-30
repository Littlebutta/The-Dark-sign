using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// In-game item names from a game's English text (msg/engus), falling back to Paramdex row names when the text
/// archives can't be read (e.g. when only the archive headers are present).
/// </summary>
public static class ItemText
{
    // DS3 names its FMGs in Japanese inside the binder ("武器名" = weapon name); Elden Ring uses English names.
    private static readonly Dictionary<string, string[]> FmgNames = new()
    {
        ["WeaponName"] = ["武器名", "WeaponName"],
        ["WeaponInfo"] = ["武器説明", "WeaponInfo"],
        ["WeaponCaption"] = ["武器うんちく", "WeaponCaption"],
    };

    private static readonly Dictionary<PorterGame, string[]> ItemBinders = new()
    {
        [PorterGame.DarkSouls3] = ["/msg/engus/item_dlc2.msgbnd.dcx"],
        [PorterGame.EldenRing] = ["/msg/engus/item.msgbnd.dcx", "/msg/engus/item_dlc01.msgbnd.dcx", "/msg/engus/item_dlc02.msgbnd.dcx"],
    };

    /// <summary>ID to text for one kind of text (e.g. "WeaponName"), merged across a game's base and DLC files.</summary>
    public static Dictionary<int, string> Load(GameArchives archives, string kind)
    {
        var result = new Dictionary<int, string>();
        foreach (var path in ItemBinders[archives.Game])
        {
            if (!archives.Contains(path))
                continue;
            var bnd = BND4.Read(archives.Read(path));
            foreach (var file in bnd.Files.Where(f => FmgNames[kind].Any(n => Leaf(f.Name).StartsWith(n, StringComparison.Ordinal))))
                foreach (var e in FMG.Read(file.Bytes).Entries)
                    if (!string.IsNullOrWhiteSpace(e.Text))
                        result[e.ID] = e.Text;
        }
        return result;
    }

    /// <summary>Like <see cref="Load"/>, but returns Paramdex row names if the text can't be read.</summary>
    public static Dictionary<int, string> LoadNamesOrRowNames(GameArchives archives, PARAM param)
    {
        try
        {
            return Load(archives, "WeaponName");
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"warning: {archives.Game.ShortName} text unreadable ({ex.Message}); using Paramdex row names");
        }
        return param.Rows.Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToDictionary(r => r.ID, r => r.Name);
    }

    /// <summary>Lower-case letters and digits only, so "Long Sword" and "Longsword" compare equal.</summary>
    public static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static string Leaf(string path) => path.Replace('\\', '/').Split('/')[^1];
}
