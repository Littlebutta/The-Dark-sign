using DarkSign.Porter;

// DarkSign.Porter: reads Dark Souls III and Elden Ring straight from their packed archives and builds
// the Elden Ring content mod. It only reads the game folders; everything it writes goes to TheDarkSign\Work.
const string Usage = """
    Usage: DarkSignPorter [--root <project root>] <command>
      scan                         index every file in both games' archives
      extract <DS3|ER> <path>...   copy files out (decompressed); a path ending in * matches a prefix
      inventory                    list DS3 weapons, armor, rings, spells and NPCs, and Elden Ring's
      dump-param <DS3|ER> <param> [field...]   print a param's rows as TSV
      fmg-list <DS3|ER> <msgbnd path>          list the text files in a msgbnd
      flver-info <DS3|ER> <binder path>        summarize the models and textures in a binder
      shops <DS3|ER>                           list talk-script commands that open shops
      explore-movesets                         report both games' player animations, TAE events and skeletons
      matbin-info <name filter>                show Elden Ring material definitions
      weapons-plan                 decide which DS3 weapons to port and as which Elden Ring class
      port-weapons                 build the weapon rows into TheDarkSign\Mods\ER\regulation.bin
      item-list                    write the test menu's item list (Elden Ring items only)
    """;

var argList = args.ToList();
string? root = null;
var rootAt = argList.IndexOf("--root");
if (rootAt >= 0 && rootAt + 1 < argList.Count)
{
    root = argList[rootAt + 1];
    argList.RemoveRange(rootAt, 2);
}
if (argList.Count == 0)
{
    Console.WriteLine(Usage);
    return 1;
}

try
{
    var ctx = new PorterContext(PorterContext.FindRoot(root));
    return argList[0].ToLowerInvariant() switch
    {
        "scan" => Commands.Scan(ctx),
        "extract" when argList.Count >= 3 => Commands.Extract(ctx, PorterGame.Parse(argList[1]), argList[2..]),
        "inventory" => Commands.Inventory(ctx),
        "dump-param" when argList.Count >= 3 =>
            Commands.DumpParam(ctx, PorterGame.Parse(argList[1]), argList[2], argList[3..]),
        "fmg-list" when argList.Count == 3 => Explore.FmgList(ctx, PorterGame.Parse(argList[1]), argList[2]),
        "flver-info" when argList.Count == 3 => Explore.FlverInfo(ctx, PorterGame.Parse(argList[1]), argList[2]),
        "port-weapons" => Commands.PortWeapons(ctx),
        "item-list" => Commands.ItemListOnly(ctx),
        "weapons-plan" => Commands.WeaponsPlan(ctx).Count > 0 ? 0 : 1,
        "matbin-info" when argList.Count == 2 => Explore.MatbinInfo(ctx, argList[1]),
        "shops" when argList.Count == 2 => Explore.Shops(ctx, PorterGame.Parse(argList[1])),
        "explore-movesets" => MovesetExplore.Run(ctx),
        _ => Fail(Usage),
    };
}
catch (Exception ex)
{
    // Inner exceptions carry the real cause (e.g. why each Oodle DLL candidate failed to load).
    IEnumerable<Exception> inner = ex.InnerException is AggregateException nested
        ? nested.Flatten().InnerExceptions
        : ex.InnerException is { } one ? [one] : [];
    return Fail(string.Join(Environment.NewLine,
        new[] { $"error: {ex.Message}" }.Concat(inner.Select(e => $"  {e.GetType().Name}: {e.Message}"))));
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
