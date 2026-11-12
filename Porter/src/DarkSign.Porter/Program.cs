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
      attack-events <DS3|ER> [bullets] [aNNN:id ...]  survey the attack / bullet events that look up behaviour rows
      anim-id-map                              pair DS3 and Elden Ring animation IDs by behaviour selector name
      chr-roundtrip                            write Elden Ring's player binders unchanged (raw, and re-written) to try in game
      binder-diff <file a> <file b>            compare two binder files on disk (compression, files, TAE events)
      check-sound-bank                         check Elden Ring's main sound bank round-trips and how it stores media
      sound-probe [all|undo]                   point the ported sounds at an Elden Ring swing in the built bank (one-launch test)
      explore-fx-sound                         report both games' effect and sound files and the TAE events that use them
      matbin-info <name filter>                show Elden Ring material definitions
      effect-models [DS3 fxr id...]            report the DS3 effects that use models (and their materials) next to Elden Ring's effect models
      effect-refs [DS3 weapon id...]           report the effects DS3's params tie to each weapon (SpEffect visuals, bullets) and dry-run their port; params only
      weapons-plan                 decide which DS3 weapons to port and as which Elden Ring class
      port-weapons                 build the weapon rows into TheDarkSign\Mods\ER\regulation.bin
      validate-mod                 check the built mod offline: params, TAE, animations, clips, sounds, effects
      texture-check [weapon...]    check every ported weapon model's materials find their textures (grey models)
      item-list                    write the test menu's item list (Elden Ring items only)
      chr-survey <DS3|ER> <chr id>...  report an enemy's files, binders, AI scripts and params (enemy port step 1)
      map-selftest                 offline checks of the map porter (no game files needed)
      sound-selftest               offline checks of how ported sounds are added to a Wwise bank (no game files needed)
      texture-selftest             offline checks of the DS3 reflectance -> Elden Ring metallic texture conversion and hidden weapon parts
      timing-selftest              offline checks that ported moves' combo/cancel windows don't cut DS3 swings short
      enemy-diag survey|unpack <file>...  enemy port diagnostics: every character's skeleton and animation IDs; decompress files
      enemy-diag tae <anibnd> [from-to...]  a decompressed animation binder's TAE entries and events, and each file's length and blend hint
      enemy-selftest               offline checks of the enemy porter (templates, IDs, skin indices, chr map, TAE merge, effects, SpEffects, sound events)
      moveset-selftest             offline checks of how behaviour animation IDs are shared out among the ported movesets
      arts-selftest                offline checks of the weapon art port (hks patch, skill names)
      anim-selftest                offline checks of the bone retargeting (rest-pose corrections) and spline rotation rewrite
      weapon-diag bones            how each shared player bone rests in both games and how its DS3 rotation is carried over
      effects-selftest             offline checks of the effect visual, bullet and resident effect ports
      map-survey [map ids]         report both games' map files for the ported slots (layouts, binders, textures, events)
      cutscene-survey [names] [er:names]  report DS3 remo and Elden Ring cutscene binders (layout, MQB timeline, Havok)
      cutscene-sound-survey [names] [er:names]  report both games' cutscene sound (timeline sound events, banks, index)
      cutscene-test [names]        convert DS3 cutscenes into Work\\scratch\\cutscenes (not the mod) and check every result
      event-coverage <ds3.emedf.json> <er.emedf.json>  which DS3 event instructions convert to Elden Ring, and how
      port-enemies                 port DS3's enemies only (port-maps does this too) and check what the game will load
      drops-plan                   dry run of the DS3 enemy drops on the two regulations: every lot, mapping and unported item
      map-validate                 read back the ported maps in the mod folder and check them (port-maps runs this too)
      port-maps [map ids] [--ds3-groups|--no-groups] [--plain-materials] [--vanilla-draw-param] [--host-lighting] [--host-sky] [--helper-pieces] [--ds3-hit-filters] [--stand-ins] [--host-atmosphere] [--host-env-light] [--flat-env-light] [--baked-light-flip x|y|z] [--half-textures|--full-low-textures] [--no-piece-test] [--piece-test <er map id>=<vanilla-model|vanilla-material|ported-material|none>] [--xml-collision] [--simd] [--after-pid <port-weapons pid>]
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
    if (argList[0].Equals("texture-selftest", StringComparison.OrdinalIgnoreCase))
        return WeaponTextures.SelfTest() | WeaponHiddenParts.SelfTest() | WeaponNodes.SelfTest();
    if (argList[0].Equals("timing-selftest", StringComparison.OrdinalIgnoreCase))
        return MoveTiming.SelfTest();
    if (argList[0].Equals("enemy-selftest", StringComparison.OrdinalIgnoreCase))
    {
        int enemy = DarkSign.Porter.Enemies.EnemySelfTest.Run();
        int effects = DarkSign.Porter.Enemies.ChrEffectsSelfTest.Run();
        Console.WriteLine(effects == 0 ? "enemy-selftest (effects, sounds, abilities): all checks passed" : $"enemy-selftest (effects, sounds, abilities): {effects} FAILED");
        return enemy != 0 || effects != 0 ? 1 : 0;
    }
    // Reads decompressed files only, so it needs neither game nor root (a cloud thread can run it on staged files).
    if (argList[0].Equals("enemy-diag", StringComparison.OrdinalIgnoreCase) && argList.Count >= 3 && argList[1].Equals("tae", StringComparison.OrdinalIgnoreCase))
        return DarkSign.Porter.Enemies.EnemyDiag.Tae(argList[2], argList.Skip(3).ToList());
    if (argList[0].Equals("moveset-selftest", StringComparison.OrdinalIgnoreCase))
        return ClipBudget.SelfTest();
    if (argList[0].Equals("arts-selftest", StringComparison.OrdinalIgnoreCase))
        return ArtSelfTest.Run();
    if (argList[0].Equals("anim-selftest", StringComparison.OrdinalIgnoreCase))
        return AnimSelfTest.Run();
    if (argList[0].Equals("effects-selftest", StringComparison.OrdinalIgnoreCase))
        return EffectSelfTest.Run();
    if (argList[0].Equals("event-coverage", StringComparison.OrdinalIgnoreCase) && argList.Count == 3)
        return DarkSign.Porter.Maps.EventPort.CoverageReport(argList[1], argList[2]);
    // Build methods chosen in the launcher's Fallbacks window (DarkSign.Core.Fallbacks); "" = every default.
    if (Environment.GetEnvironmentVariable("DARKSIGN_METHODS") is { Length: > 0 } methods)
        Console.WriteLine($"fallback methods: {methods}");
    if (GameRunningGuard.Problem(argList[0]) is { } running)
        return Fail(running);
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
        "texture-check" => TextureCheck.Run(ctx, argList[1..]),
        "port-maps" => DarkSign.Porter.Maps.MapPorter.Run(ctx, argList[1..]),
        "port-enemies" => DarkSign.Porter.Enemies.ChrPort.Run(ctx),
        "drops-plan" => DarkSign.Porter.Enemies.DropPort.Plan(ctx),
        "enemy-diag" => DarkSign.Porter.Enemies.EnemyDiag.Run(ctx, argList[1..]),
        "map-survey" => DarkSign.Porter.Maps.MapSurvey.Run(ctx, argList[1..]),
        "cutscene-survey" => DarkSign.Porter.Maps.CutsceneSurvey.Run(ctx, argList[1..]),
        "cutscene-sound-survey" => DarkSign.Porter.Maps.CutsceneSoundSurvey.Run(ctx, argList[1..]),
        "cutscene-test" => DarkSign.Porter.Maps.CutscenePort.Test(ctx, argList[1..]),
        "map-validate" => DarkSign.Porter.Maps.MapValidate.Run(ctx),
        "map-diag" => DarkSign.Porter.Maps.MapDiag.Run(ctx, argList[1..]),
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
        "weapon-diag" => WeaponDiag.Run(ctx, argList[1..]),
        "explore-behavior" => BehaviorExplore.Run(ctx, argList[1..]),
        "anim-id-map" => AnimIdMap.Run(ctx),
        "behavior-roundtrip" => BehaviorRoundTrip.Run(ctx),
        "explore-fx-sound" => FxSoundExplore.Run(ctx),
        "effect-models" => EffectModelSurvey.Run(ctx, argList[1..]),
        "effect-refs" => EffectRefsReport.Run(ctx, argList[1..]),
        "check-sound-bank" => SoundBankCheck.Run(ctx),
        "sound-probe" => SoundProbe.Run(ctx, argList[1..]),
        "chr-roundtrip" => ChrRoundTrip.Run(ctx),
        "binder-diff" when argList.Count == 3 => BinderDiff.Run(argList[1], argList[2]),
        "chr-survey" when argList.Count >= 3 => ChrSurvey.Run(ctx, PorterGame.Parse(argList[1]), argList[2..]),
        "attack-events" when argList.Count >= 2 => AttackEventSurvey.Run(ctx, PorterGame.Parse(argList[1]), argList[2..]),
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
