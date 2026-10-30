using DarkSign.Porter;

// DarkSign.Porter: reads Dark Souls III and Elden Ring straight from their packed archives and builds
// the Elden Ring content mod. It only reads the game folders; everything it writes goes to TheDarkSign\Work.
const string Usage = """
    Usage: DarkSignPorter [--root <project root>] <command>
      scan                         index every file in both games' archives
      extract <DS3|ER> <path>...   copy files out (decompressed); a path ending in * matches a prefix
      inventory                    list DS3 weapons, armor, rings, spells and NPCs, and Elden Ring's
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
