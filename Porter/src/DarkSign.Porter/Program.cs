using DarkSign.Porter;

// DarkSign.Porter: reads Dark Souls III and Elden Ring straight from their packed archives and builds
// the Elden Ring content mod. It only reads the game folders; everything it writes goes to TheDarkSign\Work.
const string Usage = """
    Usage: DarkSignPorter [--root <data folder>] [--game <DSR|DS3|BB|ER>=<exe>]... <command>
      --root    the folder that holds TheDarkSign (mod, tools, work files); found from the porter's own folder if left out
      --game    where a game is, e.g. --game ER="C:\Steam\steamapps\common\ELDEN RING\Game\eldenring.exe"; the
                game folder is only read. Games not given are searched for in the root's subfolders.
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
      chr-roundtrip                            write Elden Ring's player binders unchanged (raw, and re-written) to try in game
      binder-diff <file a> <file b>            compare two binder files on disk (compression, files, TAE events)
      check-sound-bank                         check Elden Ring's main sound bank round-trips and how it stores media
      sound-probe [all|undo]                   point the ported sounds at an Elden Ring swing in the built bank (one-launch test)
      explore-fx-sound                         report both games' effect and sound files and the TAE events that use them
      matbin-info <name filter>                show Elden Ring material definitions
      weapons-plan                 decide which DS3 weapons to port and as which Elden Ring class
      port-weapons                 build the weapon rows into TheDarkSign\Mods\ER\regulation.bin
      validate-mod                 check the built mod offline: params, TAE, animations, clips, sounds, effects
      item-list                    write the test menu's item list (Elden Ring items only)
      chr-survey <DS3|ER> <chr id>...  report an enemy's files, binders, AI scripts and params (enemy port step 1)
      map-selftest                 offline checks of the map porter (no game files needed)
      sound-selftest               offline checks of how ported sounds are added to a Wwise bank (no game files needed)
      timing-selftest              offline checks that ported moves' combo/cancel windows don't cut DS3 swings short
      map-survey [map ids]         report both games' map files for the ported slots (layouts, binders, textures, events)
      event-coverage <ds3.emedf.json> <er.emedf.json>  which DS3 event instructions convert to Elden Ring, and how
      map-validate                 read back the ported maps in the mod folder and check them (port-maps runs this too)
      port-maps [map ids] [--ds3-groups] [--binary-collision] [--simd] [--after-pid <port-weapons pid>]
                                   port the DS3 maps that have an Elden Ring slot, and the F1 menu's DS3 run events
      randomize [--profile <name>] [--seed <n>]
                                   make a profile's cross-game randomized world (checked finishable) and its spoiler log
      rando-survey                 report Elden Ring's graces, regions and map item lots for the randomizer
    """;

var argList = args.ToList();
string? root = null;
var rootAt = argList.IndexOf("--root");
if (rootAt >= 0 && rootAt + 1 < argList.Count)
{
    root = argList[rootAt + 1];
    argList.RemoveRange(rootAt, 2);
}
var gameExes = new Dictionary<DarkSign.Core.GameId, string>();
for (int at; (at = argList.IndexOf("--game")) >= 0;)
{
    if (at + 1 >= argList.Count || argList[at + 1].Split('=', 2) is not [var name, var exe]
        || DarkSign.Core.GameDefinition.All.FirstOrDefault(g => g.ShortName.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } game)
    {
        Console.Error.WriteLine("--game needs <DSR|DS3|BB|ER>=<path to the game's exe>");
        return 1;
    }
    gameExes[game.Id] = exe.Trim('"');
    argList.RemoveRange(at, 2);
}
if (argList.Count == 0)
{
    Console.WriteLine(Usage);
    return 1;
}

try
{
    // Checks that need no game folder run before the project root is looked up.
    if (argList[0].Equals("map-selftest", StringComparison.OrdinalIgnoreCase))
        return DarkSign.Porter.Maps.MapSelfTest.Run();
    if (argList[0].Equals("sound-selftest", StringComparison.OrdinalIgnoreCase))
        return SoundSelfTest.Run();
    if (argList[0].Equals("timing-selftest", StringComparison.OrdinalIgnoreCase))
        return MoveTiming.SelfTest();
    if (argList[0].Equals("event-coverage", StringComparison.OrdinalIgnoreCase) && argList.Count == 3)
        return DarkSign.Porter.Maps.EventPort.CoverageReport(argList[1], argList[2]);
    // Build methods chosen in the launcher's Fallbacks window (DarkSign.Core.Fallbacks); "" = every default.
    if (Environment.GetEnvironmentVariable("DARKSIGN_METHODS") is { Length: > 0 } methods)
        Console.WriteLine($"fallback methods: {methods}");
    var ctx = new PorterContext(PorterContext.FindRoot(root), gameExes);
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
        "validate-mod" => ModValidate.Run(ctx),
        "port-maps" => DarkSign.Porter.Maps.MapPorter.Run(ctx, argList[1..]),
        "map-survey" => DarkSign.Porter.Maps.MapSurvey.Run(ctx, argList[1..]),
        "map-validate" => DarkSign.Porter.Maps.MapValidate.Run(ctx),
        "randomize" => DarkSign.Porter.Rando.RandomizeCommand.Run(ctx, argList[1..]),
        "rando-survey" => DarkSign.Porter.Rando.RandomizeCommand.Survey(ctx),
        "item-list" => Commands.ItemListOnly(ctx),
        "weapons-plan" => Commands.WeaponsPlan(ctx).Count > 0 ? 0 : 1,
        "matbin-info" when argList.Count == 2 => Explore.MatbinInfo(ctx, argList[1]),
        "shops" when argList.Count == 2 => Explore.Shops(ctx, PorterGame.Parse(argList[1])),
        "explore-movesets" => MovesetExplore.Run(ctx),
        "anim-test" => AnimTest.Run(ctx, argList[1..]),
        "anim-spline-check" => AnimSplineCheck.Run(ctx, argList[1..]),
        "mod-check" when argList.Count == 2 => ModCheck.Run(ctx, int.Parse(argList[1])),
        "explore-behavior" => BehaviorExplore.Run(ctx, argList[1..]),
        "anim-id-map" => AnimIdMap.Run(ctx),
        "behavior-roundtrip" => BehaviorRoundTrip.Run(ctx),
        "explore-fx-sound" => FxSoundExplore.Run(ctx),
        "check-sound-bank" => SoundBankCheck.Run(ctx),
        "sound-probe" => SoundProbe.Run(ctx, argList[1..]),
        "chr-roundtrip" => ChrRoundTrip.Run(ctx),
        "binder-diff" when argList.Count == 3 => BinderDiff.Run(argList[1], argList[2]),
        "chr-survey" when argList.Count >= 3 => ChrSurvey.Run(ctx, PorterGame.Parse(argList[1]), argList[2..]),
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
