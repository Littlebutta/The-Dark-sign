using System.Text.RegularExpressions;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The text DS3's maps show by number: event messages ("Door is locked", "Does not open from this side", "Lever
/// does not move", the yes/no dialogs, the lines that blink mid-screen) and area names ("High Wall of Lothric").
/// <para>
/// Up to 1.0.50x the numbers were carried over as they were, and Elden Ring looked them up in its own text: DS3's
/// event text numbers (10010160...) and area name numbers mean nothing or something else there, so a locked door
/// or a map event showed no words, a placeholder, or an unrelated Elden Ring line. Three places name such text,
/// and each now names a copy of DS3's own words:
/// </para>
/// <list type="bullet">
/// <item>the scripts: every "Message ID" of DS3's dialog, status and message instructions, written in the line or
/// passed in where the event is started (<see cref="EventPort.Kind.Message"/>), and the area name of "Display Area
/// Welcome Message" (<see cref="EventPort.Kind.PlaceName"/>);</item>
/// <item>the ObjActParam rows of doors, levers and chests: the message shown when the action fails
/// (<see cref="ObjectPort.AddObjActRows"/>);</item>
/// <item>the map files: each collision's area name, which DS3 shows as a banner when the player steps onto it
/// (<see cref="MsbPort"/>; Elden Ring's collisions have the same field, LocationTextID).</item>
/// </list>
/// A DS3 text number N is Elden Ring's <see cref="Base"/> + N: the same rule the people port uses for dialogue
/// lines (<see cref="TalkPort.TextBase"/>, which shows in game), so every step finds a text's new number without a
/// shared table. The step that follows the events writes all of DS3's event text into EventTextForMap and all of
/// its area names into PlaceName, in the DLC02 binders, which ship nearly empty: no Elden Ring text changes.
/// <para>
/// DARKSIGN_MAP_TEXT (launcher fallback "map-text"): "noplaces" leaves the area names out (collisions name none,
/// as before); "0" keeps DS3's numbers everywhere, as before.
/// </para>
/// </summary>
public static class MapText
{
    public const int Base = TalkPort.TextBase;

    public enum Method { All, NoPlaces, Off }

    public static Method Mode => ModeOf(Environment.GetEnvironmentVariable("DARKSIGN_MAP_TEXT"));

    public static Method ModeOf(string? value) =>
        value is "0" || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ? Method.Off
        : string.Equals(value, "noplaces", StringComparison.OrdinalIgnoreCase) ? Method.NoPlaces
        : Method.All;

    /// <summary>The DS3 text numbers the converted scripts and rows name, for the report and for text that isn't in DS3's event text files.</summary>
    public sealed class Used
    {
        public SortedSet<int> Messages { get; } = [];
        public SortedSet<int> Places { get; } = [];
    }

    private static bool Movable(long ds3Id) => ds3Id > 0 && ds3Id < Base;

    /// <summary>Elden Ring's EventTextForMap number of a DS3 event message (0 and less stay: "none").</summary>
    public static long Message(long ds3Id, Method mode) => mode != Method.Off && Movable(ds3Id) ? Base + ds3Id : ds3Id;

    public static long Message(long ds3Id) => Message(ds3Id, Mode);

    /// <summary>
    /// Elden Ring's PlaceName number of a DS3 area name. A negative number other than -1 keeps its sign (the older
    /// games' collisions use a negative name for "this area, without the banner"; not checked in DS3's maps).
    /// </summary>
    public static long Place(long ds3Id, Method mode) =>
        mode != Method.All ? ds3Id
        : Movable(ds3Id) ? Base + ds3Id
        : ds3Id < -1 && Movable(-ds3Id) ? -(Base + -ds3Id)
        : ds3Id;

    public static long Place(long ds3Id) => Place(ds3Id, Mode);

    /// <summary>A DS3 collision's area name as the Elden Ring collision's (-1, none, when area names are off).</summary>
    public static int CollisionPlace(short ds3MapNameId, Method mode) => mode == Method.All ? (int)Place(ds3MapNameId, mode) : -1;

    public static bool IsOurs(int id) => id >= Base || id <= -Base;

    // ---- Text files ----

    /// <summary>
    /// Adds DS3's text to an Elden Ring text file under <see cref="Base"/> + its number (ours from an earlier run are
    /// replaced): all of <paramref name="own"/> (the DS3 files this kind of text lives in), and each of
    /// <paramref name="used"/> that is only in <paramref name="other"/> (text a script names from another DS3 file).
    /// Returns how many were added and the used numbers DS3 has no words for.
    /// </summary>
    public static (int Added, int FromOther, List<int> Missing) Merge(FMG target, IReadOnlyDictionary<int, string> own,
        IReadOnlyDictionary<int, string> other, IEnumerable<int> used)
    {
        target.Entries.RemoveAll(e => IsOurs(e.ID));
        int added = 0, fromOther = 0;
        var missing = new List<int>();
        foreach (var (id, words) in own.OrderBy(t => t.Key))
            if (Movable(id))
            {
                target.Entries.Add(new FMG.Entry(Base + id, words));
                added++;
            }
        foreach (var id in used.Where(id => Movable(id) && !own.ContainsKey(id)).Distinct().OrderBy(id => id))
            if (other.TryGetValue(id, out var words))
            {
                target.Entries.Add(new FMG.Entry(Base + id, words));
                fromOther++;
            }
            else
                missing.Add(id);
        target.Entries.Sort((a, b) => a.ID.CompareTo(b.ID));
        return (added, fromOther, missing);
    }

    /// <summary>
    /// DS3's event text names items by tag: "Used &lt;?gdsparam@2001?&gt;" is "Used" and the name of DS3's goods 2001
    /// (181 of its 1,374 event texts, the key and item messages). Elden Ring would fill the tag with its own goods
    /// 2001, so the tag gives way to DS3's item name. A tag whose item DS3 has no name for stays.
    /// </summary>
    public static string GoodsTags(string text, IReadOnlyDictionary<int, string> ds3Goods) =>
        text.Contains("<?gdsparam@", StringComparison.Ordinal)
            ? Regex.Replace(text, @"<\?gdsparam@(\d+)\?>", m => int.TryParse(m.Groups[1].Value, out var id) && ds3Goods.TryGetValue(id, out var name) ? name.Trim() : m.Value)
            : text;

    private static readonly string[] Ds3Menus = ["/msg/engus/menu_dlc2.msgbnd.dcx", "/msg/engus/menu_dlc1.msgbnd.dcx", "/msg/engus/menu.msgbnd.dcx"];
    private static readonly string[] Ds3Items = ["/msg/engus/item_dlc2.msgbnd.dcx", "/msg/engus/item_dlc1.msgbnd.dcx", "/msg/engus/item.msgbnd.dcx"];
    // DS3 names its text files in Japanese: イベントテキスト = event text, 地名 = place name.
    private static readonly string[] EventNames = ["イベントテキスト", "EventText"], PlaceNames = ["地名", "PlaceName"];
    public const string ErItems = "/msg/engus/item_dlc02.msgbnd.dcx";

    /// <summary>
    /// DS3's text by number from a set of binders: the files whose name has one of <paramref name="names"/>, and
    /// every other file (first found wins in each; the DLC2 binder, which carries the patched base text, comes first).
    /// </summary>
    public static (Dictionary<int, string> Own, Dictionary<int, string> Other, List<string> OwnFiles) LoadDs3(GameArchives ds3, string[] binders, string[] names)
    {
        var own = new Dictionary<int, string>();
        var other = new Dictionary<int, string>();
        var files = new List<string>();
        foreach (var path in binders.Where(ds3.Contains))
            foreach (var f in BND4.Read(ds3.Read(path)).Files.Where(f => f.Name.EndsWith(".fmg", StringComparison.OrdinalIgnoreCase)))
            {
                var leaf = ItemText.Leaf(f.Name);
                bool mine = names.Any(n => leaf.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (mine && !files.Contains(leaf))
                    files.Add(leaf);
                foreach (var e in FMG.Read(f.Bytes).Entries)
                    if (!string.IsNullOrWhiteSpace(e.Text))
                        (mine ? own : other).TryAdd(e.ID, e.Text);
            }
        return (own, other, files);
    }

    /// <summary>The text file a binder keeps for our additions: the DLC02 one, else the DLC01 one, else the base one.</summary>
    public static BinderFile? Target(BND4 binder, string stem) =>
        new[] { stem + "_dlc02", stem + "_dlc01", stem }
            .Select(n => binder.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).StartsWith(n + ".", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(f => f is not null);

    /// <summary>
    /// The port-maps step, after the events step: DS3's event text into the mod's menu binder and its area names into
    /// the mod's item binder (each built on the mod's copy when another step already wrote one).
    /// </summary>
    public static string Write(PorterContext ctx, GameArchives ds3, GameArchives er, Used used)
    {
        var mode = Mode;
        if (mode == Method.Off)
            return "map text: off (DARKSIGN_MAP_TEXT=0); scripts, doors and collisions name DS3's text numbers, which Elden Ring doesn't have";
        Dictionary<int, string>? goods = null;
        try { goods = ItemText.Load(ds3, "GoodsName"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; } // the item tags stay as DS3 wrote them
        var lines = new List<string>
        {
            WriteOne(ctx, ds3, er, ActionButtons.ModMenu, "EventTextForMap", Ds3Menus, EventNames, used.Messages, "event messages", goods),
            mode == Method.NoPlaces
                ? "map text: area names off (DARKSIGN_MAP_TEXT=noplaces); no area name shows on entering a DS3 area"
                : WriteOne(ctx, ds3, er, ErItems, "PlaceName", Ds3Items, PlaceNames, used.Places, "area names"),
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string WriteOne(PorterContext ctx, GameArchives ds3, GameArchives er, string erBinder, string stem,
        string[] ds3Binders, string[] ds3Names, IReadOnlyCollection<int> used, string what, IReadOnlyDictionary<int, string>? goods = null)
    {
        try
        {
            var (own, other, ownFiles) = LoadDs3(ds3, ds3Binders, ds3Names);
            int tags = 0;
            if (goods is not null)
                foreach (var texts in new[] { own, other })
                    foreach (var id in texts.Keys.ToList())
                        if (GoodsTags(texts[id], goods) is var named && !ReferenceEquals(named, texts[id]) && named != texts[id])
                        {
                            texts[id] = named;
                            if (texts == own)
                                tags++;
                        }
            var modPath = ctx.ModPath(MapPorter.ModParts(erBinder));
            var (vanilla, compression) = er.ReadWithCompression(erBinder);
            var binder = BND4.Read(File.Exists(modPath) ? DCX.Decompress(File.ReadAllBytes(modPath)) : vanilla);
            var file = Target(binder, stem) ?? throw new InvalidDataException($"{ItemText.Leaf(erBinder)} has no {stem} file");
            var fmg = FMG.Read(file.Bytes);
            var (added, fromOther, missing) = Merge(fmg, own, other, used);
            file.Bytes = fmg.Write();
            Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
            File.WriteAllBytes(modPath, DCX.Compress(binder.Write(), compression));
            return $"map text: {what}: {added} of DS3's ({(ownFiles.Count == 0 ? "NO DS3 FILE FOUND BY NAME" : string.Join(", ", ownFiles))}) in {ItemText.Leaf(file.Name)} at {Base:N0} + the DS3 number; "
                   + (tags > 0 ? $"{tags} item tags filled with DS3's item names; " : "")
                   + $"the maps name {used.Count}"
                   + (fromOther > 0 ? $", {fromOther} of them from DS3's other text files" : "")
                   + (missing.Count > 0 ? $", {missing.Count} DS3 has no words for ({string.Join(" ", missing.Take(20))}{(missing.Count > 20 ? " ..." : "")})" : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"map text: {what}: FAILED ({ex.Message}); they show no words";
        }
    }
}
