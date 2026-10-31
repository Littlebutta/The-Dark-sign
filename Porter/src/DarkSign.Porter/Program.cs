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
      anim-test <DS3 anim> [ER anim] | undo    try one DS3 animation in place of an Elden Ring one (mod folder only)
      explore-behavior [section...]            report how Elden Ring's player behaviour selects weapon animations
      behavior-roundtrip                       write Elden Ring's player behaviour back unchanged, to test the round trip
      tae-dump <DS3|ER> <section>              list every event of a section's TAE with raw parameters
      anim-id-map                              pair DS3 and Elden Ring animation IDs by behaviour selector name
      check-sound-bank                         check Elden Ring's main sound bank round-trips and how it stores media
      explore-fx-sound                         report both games' effect and sound files and the TAE events that use them
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
        "anim-test" => AnimTest.Run(ctx, argList[1..]),
        "explore-behavior" => BehaviorExplore.Run(ctx, argList[1..]),
        "anim-id-map" => AnimIdMap.Run(ctx),
        "behavior-roundtrip" => BehaviorRoundTrip.Run(ctx),
        "explore-fx-sound" => FxSoundExplore.Run(ctx),
        "check-sound-bank" => SoundBankCheck.Run(ctx),
        "tae-dump" when argList.Count == 3 => TaeDump.Run(ctx, PorterGame.Parse(argList[1]), int.Parse(argList[2])),
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
