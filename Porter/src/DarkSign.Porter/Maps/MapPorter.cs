using System.Numerics;
using System.Text;
using SoulsFormats;
using ChrPort = DarkSign.Porter.Enemies.ChrPort;

namespace DarkSign.Porter.Maps;

/// <summary>
/// `port-maps`: ports every Dark Souls III map that has an Elden Ring slot (<see cref="MapSlots"/>) into the mod
/// folder in one run: layout (MSB), map piece models, their textures and materials, collision, navmesh, and an empty event
/// script for the slot. It then adds the "DS3 run" events to common.emevd and writes darksign_maps.tsv, which the F1
/// menu reads. Everything is read from the games' archives and written under TheDarkSign\Mods\ER.
///
/// Anything that fails to convert is left out of the layout (a part whose model is missing would crash the game),
/// and the report says what and why.
/// </summary>
public static class MapPorter
{
    public sealed class Options
    {
        public HashSet<string> Only { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>--piece-test &lt;Elden Ring map id&gt;=&lt;vanilla-model|vanilla-material|ported-material|none&gt;: a slot's test, instead of the default.</summary>
        public Dictionary<string, PieceTest> PieceTests { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>--tint-materials (diagnostic): DS3 material families that keep something in vertex
        /// alpha are drawn in solid colours (see <see cref="MapModelPort.Tints"/>).</summary>
        public bool TintMaterials { get; set; }

        /// <summary>
        /// --after-pid: the port-weapons process started alongside this one. The events step edits the regulation.bin
        /// port-weapons writes, so it waits for that process to finish; everything before it runs side by side.
        /// </summary>
        public int AfterPid { get; set; }

        /// <summary>
        /// --stop-after-maps (diagnostic): the run ends once the maps, their textures and their materials are written,
        /// before the steps that need port-weapons' regulation, and lists every file in the mod folder with its SHA-256
        /// in Work\reports\map-files.tsv, to compare two runs into two empty --root folders.
        /// </summary>
        public bool StopAfterMaps { get; set; }

        public static Options Parse(IEnumerable<string> args)
        {
            var o = new Options();
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
                switch (list[i].ToLowerInvariant())
                {
                    case "--piece-test" when i + 1 < list.Count && list[i + 1].Split('=') is [var map, var test]:
                        o.PieceTests[MapId.Parse(map).ToString()] = ParsePieceTest(test); i++; break;
                    case "--tint-materials": o.TintMaterials = true; break;
                    case "--stop-after-maps": o.StopAfterMaps = true; break;
                    case "--after-pid" when i + 1 < list.Count && int.TryParse(list[i + 1], out var pid): o.AfterPid = pid; i++; break;
                    default: o.Only.Add(MapId.Parse(list[i]).ToString()); break;
                }
            return o;
        }
    }

    /// <summary>
    /// One line per DS3 map piece, to name what a screenshot shows: the part, where it stands, DS3's draw flags and
    /// groups, whether it was written, and each mesh of its model (material type, the kind it is drawn as, vertex
    /// count, size in metres, albedo texture). Sorted so the largest odd pieces (unlit, left out, few vertices)
    /// are easy to find with a filter.
    /// </summary>
    public static string PieceReport(MSB3 ds3, MapModelPort models, IReadOnlySet<string> missingModels,
        IReadOnlySet<string>? standIns = null, IReadOnlySet<string>? farViews = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string V(Vector3 v) => string.Create(inv, $"{v.X:F0},{v.Y:F0},{v.Z:F0}");
        var sb = new StringBuilder();
        sb.AppendLine("part\tmodel\tstatus\tposition\trotation\tlod\tlayer\tshadow src/dest\treflect by/only\tdraw groups\tsize\tvertices\tmeshes");
        bool layerOk = MsbPort.BaseLayerMeans(ds3);
        foreach (var p in ds3.Parts.MapPieces)
        {
            var number = ModelNumber(p.ModelName);
            models.ModelMeshes.TryGetValue(number, out var meshes);
            var status = MsbPort.HelperKind(p, layerOk) is { } helper ? "left out: " + helper
                : missingModels.Contains(p.ModelName) ? "left out: no mesh the port can draw"
                : farViews?.Contains(p.Name) == true ? "left out: far view (DS3 draws it only where the detail it covers is hidden)"
                : standIns?.Contains(p.Name) == true ? "left out: low detail stand-in"
                : "written";
            var size = meshes is { Count: > 0 } ? meshes.Select(m => m.Size).Aggregate(Vector3.Max) : Vector3.Zero;
            sb.Append(p.Name).Append('\t').Append(p.ModelName).Append('\t').Append(status).Append('\t')
                .Append(V(p.Position)).Append('\t').Append(V(p.Rotation)).Append('\t').Append(p.LodParamID).Append('\t')
                .Append(p.MapStudioLayer.ToString("X")).Append('\t')
                .Append(p.ShadowSource ? 1 : 0).Append('/').Append(p.ShadowDest ? 1 : 0).Append(p.IsShadowOnly ? " only" : "").Append('\t')
                .Append(p.DrawByReflectCam ? 1 : 0).Append('/').Append(p.DrawOnlyReflectCam ? 1 : 0).Append('\t')
                .Append(string.Join("", p.DrawGroups.Take(4).Select(g => g.ToString("X8")))).Append('\t')
                .Append(V(size)).Append('\t').Append(meshes?.Sum(m => m.Vertices) ?? 0).Append('\t')
                .AppendLine(meshes is null ? "" : string.Join("; ", meshes.Select(m => string.Create(inv, $"{m.Mtd} [{m.Kind}] v{m.Vertices} {V(m.Size)} {m.Albedo}"))));
        }
        return sb.ToString();
    }

    // The map materials this port adds go into Elden Ring's main material binder, where JortPob puts the materials
    // of its map pieces (Bind.cs BindMaterials). The weapon and enemy ports use DLC02, so the steps never overwrite
    // each other's work. Builds up to 1.0.263 used the DLC01 binder, which is empty in the game's own files: nothing
    // shows the game looks a map piece's material up there, and those builds never drew a DS3 map piece.
    private const string MaterialBinder = "/material/allmaterial.matbinbnd.dcx";
    private const string OldMaterialBinder = "/material/allmaterial_dlc01.matbinbnd.dcx";

    /// <summary>
    /// What a slot's map pieces are made of. The 1.0.259 build was the first whose converted models the game read
    /// (their file ID inside the binder became 200), and its map load never finished: the game opened every map piece
    /// binder and then waited. Which layer it waits on can't be told offline, so one build holds the three layers
    /// side by side, each a strict subset of the next:
    /// <list type="bullet">
    /// <item><see cref="VanillaModel"/>: every DS3 map piece is the host's own vanilla model (Elden Ring's bytes).
    /// Loads: binders, layout parts and draw settings are fine.</item>
    /// <item><see cref="VanillaMaterial"/>: DS3's geometry with the host model's own vanilla material. Loads: the
    /// converted geometry is fine too.</item>
    /// <item><see cref="PortedMaterial"/>: DS3's geometry with the port's own MATBINs, which keep their template's
    /// vanilla textures. Loads: the MATBINs the port adds are fine too.</item>
    /// <item><see cref="None"/>: the real port (DS3 geometry, ported materials and textures).</item>
    /// </list>
    /// The 1.0.271 playtest: vanilla-model and vanilla-material both loaded and the real port did not, so the wait
    /// was on the ported MATBINs or their textures. 1.0.279 packed the texture binder the way JortPob does and the
    /// real port loaded, textured. Every slot is therefore the real port again; a test is built only when
    /// "--piece-test" names one for a slot.
    /// </summary>
    public enum PieceTest { None, VanillaModel, VanillaMaterial, PortedMaterial }

    public static PieceTest ParsePieceTest(string text) => text.ToLowerInvariant() switch
    {
        "vanilla-model" => PieceTest.VanillaModel,
        "vanilla-material" => PieceTest.VanillaMaterial,
        "ported-material" => PieceTest.PortedMaterial,
        "none" => PieceTest.None,
        _ => throw new ArgumentException($"unknown piece test '{text}' (vanilla-model, vanilla-material, ported-material or none)"),
    };

    /// <summary>The test a slot is built as: what "--piece-test" says, else none (the real port).</summary>
    public static PieceTest PieceTestFor(MapSlots.Slot slot, Options options)
    {
        if (options.PieceTests.TryGetValue(slot.Er.ToString(), out var asked))
            return asked;
        return PieceTest.None;
    }

    public static int Run(PorterContext ctx, IEnumerable<string> args)
    {
        var options = Options.Parse(args);
        var started = DateTime.Now;
        _placedEnemies.Clear();
        _arenas.Clear();
        _partNames.Clear();
        _coverage.Clear();
        _standInWorld.Clear();
        _templates.Clear();
        _converted.Clear();
        _objects = null;
        _objectsFailed = false;
        // A full run starts from Elden Ring's own files, not from what an earlier run left in Mods\ER: every PC that
        // builds from the same game files then gets byte-identical maps, events and flag numbers (co-op), and a slot
        // of the other map-slots method doesn't linger. A --only run is a partial rebuild and keeps the rest.
        if (options.Only.Count == 0)
        {
            CleanOutputs(ctx);
            ObjectPort.CleanOutputs(ctx);
        }
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var report = new StringBuilder();
        var ported = new List<(MapSlots.Slot Slot, int StartEntity)>();
        var textures = new Dictionary<string, MapTextures>(); // by Elden Ring area folder
        var materials = new List<BinderFile>();

        // The enemy port goes first (a full run only): it writes darksign_chr_map.tsv, which the maps read when they
        // place DS3's enemies. Its params wait for port-weapons' regulation.bin (below). A failure leaves stand-ins.
        ChrPort.Result? enemyPort = null;
        if (options.Only.Count == 0)
        {
            var enemyClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                enemyPort = ChrPort.Build(ctx, er, ds3);
                report.AppendLine($"enemy port: {enemyPort.Summary} (Work\\reports\\{ChrPort.ReportFile})");
                if (enemyPort.Problem is { } problem)
                    report.AppendLine($"enemy port: FAIL: {problem}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report.AppendLine($"enemy port: FAILED: {ex.Message}; DS3 maps use Elden Ring stand-ins");
                Console.Error.WriteLine($"enemies: failed: {ex}");
            }
            Time(report, "enemy port", enemyClock);
        }

        // Only map IDs the registry names or can name: an unlisted ID never loads, and an open-world area number past
        // the game's six crashed every boot (1.0.238), so such a slot is reported and left out rather than built.
        var registryLists = MapRegistry.ReadLists(er);
        var slots = MapSlots.All.Where(s => options.Only.Count == 0 || options.Only.Contains(s.Ds3.ToString())).ToList();
        foreach (var slot in MapRegistry.Unregistered(registryLists, slots))
        {
            report.AppendLine(MapRegistry.SkipReason(registryLists, slot));
            Console.Error.WriteLine("  " + MapRegistry.SkipReason(registryLists, slot));
            slots.Remove(slot);
        }
        // Probe slots last: they copy their main slot's converted map pieces.
        var order = slots.OrderBy(s => s.Probe).ToList();
        // Several maps port side by side (MapTurns keeps what they share in this order, so the files are the ones a
        // one-at-a-time run writes). Each map has its own part of the report, joined in this order afterwards.
        foreach (var slot in order)
            if (!textures.ContainsKey(slot.Er.AreaFolder))
                textures[slot.Er.AreaFolder] = new MapTextures(slot.Er);
        var turns = new MapTurns(order.Count);
        var mapReports = order.Select(_ => new StringBuilder()).ToArray();
        var starts = new int[order.Count];
        int atOnce = Math.Min(Parallelism.MapsAtOnce, Math.Max(1, order.Count));
        var mapsClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            turns.Run(atOnce,
                // A slot whose DS3 map an earlier slot converts copies that slot's files, so it waits for it.
                map => Enumerable.Range(0, map).Where(earlier => order[earlier].Ds3 == order[map].Ds3),
                map =>
                {
                    var slot = order[map];
                    var mapReport = mapReports[map];
                    Console.WriteLine($"map {slot.Ds3} ({slot.Name}) -> Elden Ring {slot.Er}");
                    var mapClock = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        starts[map] = PortMap(ctx, ds3, er, slot, options, textures[slot.Er.AreaFolder], materials, mapReport, turns, map);
                    }
                    catch (Exception ex)
                    {
                        mapReport.AppendLine($"{slot.Ds3}: FAILED: {ex.Message}");
                        Console.Error.WriteLine($"  {slot.Ds3} failed: {ex.Message}");
                    }
                    Time(mapReport, $"map {slot.Ds3} -> {slot.Er}", mapClock);
                });
        }
        finally
        {
            for (int map = 0; map < order.Count; map++)
            {
                report.Append(mapReports[map]);
                if (starts[map] > 0)
                    ported.Add((order[map], starts[map]));
            }
        }
        if (order.Count > 0)
        {
            report.AppendLine(atOnce > 1
                ? $"maps: {order.Count} ported, up to {atOnce} side by side"
                : $"maps: {order.Count} ported, one at a time");
            Time(report, "all maps", mapsClock);
        }
        if (_standInWorld.Count > 1)
            Step(report, "world stand-ins", () => WorldDoorways(ctx, ds3));
        // The objects' materials and textures: shared by every map, so they are added once, to every area's binder.
        if (_objects is not null && ported.Count > 0)
        {
            // Objects and map pieces only DS3's cutscenes use, before the materials and the table the cutscenes read.
            Step(report, "cutscene models", _objects.ConvertCutsceneModels);
            materials.AddRange(_objects.Models.MatbinFiles(0));
            foreach (var areaTextures in textures.Values)
                _objects.GiveTextures(areaTextures, ported[0].Slot.Ds3);
            Step(report, "object port", _objects.WriteReports);
        }
        // Missing textures or materials leave the maps grey, not broken, so a failure here doesn't stop the run.
        foreach (var areaTextures in textures.Values)
            Step(report, "textures", () => areaTextures.Write(ctx, ds3, er));
        if (materials.Count > 0)
            Step(report, "materials", () => WriteMaterials(ctx, er, materials, keepExisting: options.Only.Count > 0));

        if (options.StopAfterMaps)
        {
            Step(report, "map files", () => ListModFiles(ctx));
            report.AppendLine("port-maps: stopped after the maps (--stop-after-maps): no events, effects, params or validation; not a build to play");
            var early = ctx.WorkPath("logs", $"port-maps-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(early, report.ToString());
            Console.WriteLine(report.ToString());
            Console.WriteLine($"port-maps: {ported.Count} map(s) written in {(DateTime.Now - started).TotalSeconds:F0} s, stopped after the maps; report {early}");
            return ported.Count > 0 ? 0 : 1;
        }

        // Event scripts last: they need every ported map, to send warps and IDs to the right slot. A failure leaves
        // the empty scripts written with each map.
        WaitForWeapons(options.AfterPid);
        // The PartsDrawParam row the map pieces name goes into the regulation port-weapons wrote. If it can't be
        // added, the pieces are pointed at a vanilla row, so they never name a row that doesn't exist.
        if (ported.Count > 0)
            Step(report, "map piece draw settings", () =>
            {
                var (row, note) = MapDrawParams.Write(ctx);
                if (row != MapDrawParams.FirstRow)
                    foreach (var p in ported)
                        Repoint(ctx, p.Slot.Er, row);
                return note;
            });
        // The asset rows of the ported objects. An asset part whose row is missing must not ship, so if the rows
        // can't be written the objects are taken back out of the map files.
        if (_objects is not null && ported.Count > 0)
        {
            var objectClock = System.Diagnostics.Stopwatch.StartNew();
            // The moving objects' sound banks first: a row names its bank only when the bank was written.
            Step(report, "object sounds", _objects.WriteSounds);
            objectClock.Restart();
            try
            {
                report.AppendLine(_objects.WriteParams());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                int removed = ported.Sum(p => ObjectPort.Strip(ctx, p.Slot.Er));
                report.AppendLine($"object params: FAILED: {ex.Message}; the {removed} object parts were taken out of the maps again");
                Console.Error.WriteLine($"  object params failed: {ex.Message}");
            }
            Time(report, "object params", objectClock);
        }
        // The floor materials of our own the converted collisions name (HitMaterials).
        if (ported.Count > 0)
        {
            Step(report, "hit materials", () =>
            {
                var regulation = ActionButtons.LoadRegulation(ctx);
                var line = HitMaterials.AddRows(regulation.Get("HitMtrlParam") ?? throw new InvalidDataException("Elden Ring has no HitMtrlParam"));
                regulation.Save(ctx.ModPath("regulation.bin"));
                return line;
            });
        }
        // DS3's map effects (Firelink's candles, fog, light shafts), converted by port-weapons into the common binder.
        if (ported.Count > 0)
        {
            Step(report, "map effects", () => MapSfx.Apply(ctx, ds3, ported.Select(p => p.Slot)));
            // The effects DS3's objects carry (candle flames), on the asset rows written above.
            if (_objects is not null)
                Step(report, "object effects", () => ObjectSfx.Apply(ctx, ds3, ported.Select(p => p.Slot), MapSfx.ReadTable(ctx),
                    id => _partNames.GetValueOrDefault(id)));
            // Lamps at the flames those objects burn with: DS3 baked their light, Elden Ring lights them with lamps.
            if (_objects is not null)
                Step(report, "flame lights", () => FlameLights.Apply(ctx, ds3, ported.Select(p => p.Slot),
                    id => _partNames.GetValueOrDefault(id)));
        }
        if (enemyPort is not null)
        {
            // The SpEffects the maps' scripts use get rows too, so the events step can point the scripts at them.
            if (enemyPort.Effects is { } fx && ported.Count > 0)
                try
                {
                    fx.ScriptSpEffectIds = MapEvents.ScriptSpEffects(ctx, ds3, ported.Select(p => p.Slot).ToList());
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    report.AppendLine($"enemy params: script SpEffects not read ({ex.Message}); scripts keep DS3 IDs where no move casts them");
                }
            // The lots the maps' scripts award get ItemLotParam_map rows with the drops (Ds3Ids, DropPort.PortScriptLots).
            if (ported.Count > 0)
                try
                {
                    var lots = MapEvents.ScriptIds(ctx, ds3, ported.Select(p => p.Slot).ToList(), EventPort.Kind.ItemLot);
                    lots.UnionWith(MapTreasure.Lots(ds3, ported.Select(p => p.Slot)));
                    enemyPort.ScriptLots = lots;
                    enemyPort.ScriptBehaviors = MapEvents.ScriptIds(ctx, ds3, ported.Select(p => p.Slot).ToList(), EventPort.Kind.Behavior);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    report.AppendLine($"enemy params: script item lots and behaviours not read ({ex.Message}); the scripts keep DS3's numbers");
                }
            Step(report, "enemy params", () => ChrPort.ApplyParams(ctx, er, enemyPort));
        }
        // DS3's humans in their DS3 armour: models, materials and the rows the placed people name.
        if (_armour is { StandIns.Count: > 0 } armour && ported.Count > 0)
            Step(report, "people armour", () => PortArmour(ctx, ds3, er, armour, ported.Select(p => p.Slot).ToList()));
        // Quiet copies of the stand-ins' Elden Ring rows (no Elden Ring battle music on DS3 maps).
        if (_quiet is { Copies.Count: > 0 } quiet && ported.Count > 0)
            Step(report, "stand-in battle music", () => PortQuietRows(ctx, quiet, ported.Select(p => p.Slot).ToList()));
        // Each ported map's Elden Ring field music and ambience area: its template colosseum's (MapSoundPlace).
        if (ported.Count > 0)
            Step(report, "map music areas", () => PortSoundPlaces(ctx, ported.Select(p => p.Slot).ToList()));
        // DS3's cutscenes (boss intros...) converted first, so the events step can play the ones that made it.
        Dictionary<int, int>? cutscenes = null;
        Dictionary<int, CutscenePort.Info> cutsceneInfo = [];
        if (ported.Count > 0)
            Step(report, "cutscenes", () =>
            {
                var (done, summary) = CutscenePort.Port(ctx, ds3, er,
                    ported.Select(p => new CutscenePort.MapInput(p.Slot, _partNames.GetValueOrDefault(p.Slot.Er) ?? new())).ToList(), out var infos);
                cutscenes = done;
                cutsceneInfo = infos;
                return summary;
            });
        // DS3's music (MapMusic): which regions and script lines play each map's tracks, then the events, then the tracks.
        MapMusic.Plan? music = null;
        if (ported.Count > 0)
            Step(report, "music plan", () =>
            {
                music = MusicPlan(ctx, ds3, ported.Select(p => p.Slot).ToList());
                return music.Summary;
            });
        Dictionary<int, SortedSet<uint>> playedFlags = [];
        if (ported.Count > 0)
            Step(report, "event IDs", () =>
            {
                // What the scripts' item lots, AI rows and items become (Ds3Ids), from the steps above.
                var lines = new List<string>();
                var regulation = ActionButtons.LoadRegulation(ctx);
                ParamStore? ds3Params = null;
                try { ds3Params = ctx.LoadParams(PorterGame.DarkSouls3); } catch (Exception ex) when (ex is not OutOfMemoryException) { lines.Add($"  DS3 params unreadable ({ex.Message})"); }
                var ids = Ds3Ids.Load(ctx, TalkItems(ctx, ds3, er, regulation, ds3Params, lines));
                lines.Insert(0, $"event IDs: {ids.Lots} ported item lots, {ids.Behaviors} behaviour rows, AI rows from the enemy port, items by the drops' rule");
                return string.Join(Environment.NewLine, lines);
            });
            Step(report, "map treasure", () => MapTreasure.Apply(ctx, ds3, ported.Select(p => p.Slot),
                id => _partNames.GetValueOrDefault(id), lot => Ds3Ids.Current?.ItemLot(lot)));
            Step(report, "events", () => MapEvents.Port(ctx, ds3, er, ported.Select(p => p.Slot).ToList(), cutscenes, f => playedFlags = f, music));
        if (music is not null)
            Step(report, "music", () => MapMusic.WriteAudio(ctx, ds3, music));
        // DS3's bonfires as sites of grace (Bonfires): after the events, whose scripts register them.
        if (ported.Count > 0)
            Step(report, "bonfires", () => Bonfires.Apply(ctx, ds3, er, ported.Select(p => p.Slot).ToList(),
                id => _partNames.GetValueOrDefault(id)));

        // A slot under a registered ID that ships no map (map-slots B) gets what the game's own maps have: the
        // template's per-map files under the new ID, and a flag allocation line where the template has one. The map
        // registry itself is never written (see MapRegistry: a line for an unshipped ID crashed every boot).
        // New blocks of the game's own areas (MapSlots.WorldSlots) get a line in the base list; only maps that were
        // ported, so the list never names a map with no files.
        if (ported.Count > 0)
            Step(report, "map registry", () => MapRegistry.Write(ctx, er, ported.Select(p => p.Slot).ToList()));
        // DS3's neighbouring maps share one world space; the rows tell Elden Ring so (MapLinks).
        if (ported.Count > 1)
            Step(report, "map links", () => MapLinks.Write(ctx, ds3, ported.Select(p => p.Slot).ToList()));
        if (ported.Any(p => p.Slot.Host != p.Slot.Er))
        {
            Step(report, "new map ID files", () => NewIdFiles.Copy(ctx, er, ported.Select(p => p.Slot)));
            Step(report, "flag allocation", () => FlagAlloc.WriteForNewIds(ctx, er, ported.Select(p => p.Slot)));
        }

        // DS3's talk scripts for the people placed, with their lines, menu text and shops (after the events step:
        // the flags move the way the scripts moved them, and a new map ID's copied files are already there).
        if (_talk is { UsedCount: > 0 } talk && ported.Count > 0)
            Step(report, "people talk", () => PortTalk(ctx, ds3, er, talk, ported.Select(p => p.Slot).ToList()));

        var warps = ported.Select((p, i) => new RunEvents.Warp(p.Slot.Er, (uint)p.StartEntity, i)).ToList();
        WriteMapList(ctx, ported.Select(p => p.Slot), warps);
        // Only arenas of maps that made it, numbered in port order.
        // Their warp events go into common.emevd with the run events, so without the start map there are none.
        var hasRun = ported.Any(p => p.Slot.Ds3 == MapId.Parse("m40_00_00_00") && !p.Slot.Probe);
        var arenas = _arenas.Where(a => hasRun && ported.Any(p => p.Slot.Er == a.Er)).Take(BossArenas.MaxArenas)
            .Select((a, i) => a with { Index = i }).ToList();
        File.WriteAllLines(ctx.ModPath(BossArenas.ListFile), BossArenas.ListLines(arenas));
        // The F1 menu's Cutscenes tab: like the arena warps, its events go into common.emevd with the run events.
        var chrNames = File.Exists(ctx.ModPath(EnemyPort.ChrMap.FileName)) ? EnemyPort.ChrMap.Parse(File.ReadAllLines(ctx.ModPath(EnemyPort.ChrMap.FileName))) : null;
        var menuCutscenes = hasRun
            ? CutsceneMenu.Entries(cutsceneInfo.Values, ported.GroupBy(p => p.Slot.Er).ToDictionary(g => g.Key, g => (g.First().Slot.Name, (uint)g.First().StartEntity)),
                model => chrNames?.NameOf(model))
            : [];
        File.WriteAllLines(ctx.ModPath(CutsceneMenu.ListFile), CutsceneMenu.ListLines(menuCutscenes, playedFlags));
        // The vertex layouts the map pieces were written in, for map-validate (a mesh in any other layout isn't drawn).
        if (_layouts.Count > 0)
            File.WriteAllLines(ctx.ModPath(LayoutsFile), _layouts.Order(StringComparer.Ordinal));
        Step(report, "enemy tables", () => WriteEnemyTables(ctx, ported.Select(p => p.Slot.Ds3.ToString()).ToHashSet()));
        var startMap = ported.FirstOrDefault(p => p.Slot.Ds3 == MapId.Parse("m40_00_00_00") && !p.Slot.Probe);
        if (startMap.Slot is not null)
            Step(report, "run events", () => AddRunEvents(ctx, ds3, er, startMap.Slot.Er, (uint)startMap.StartEntity, warps, arenas, menuCutscenes));
        else
            report.AppendLine("run events: not added (Firelink Shrine / Cemetery of Ash was not ported)");
        // Elden Ring warps the player to a region after a cutscene; DS3's scripts (and the F1 events) name a player part.
        if (ported.Count > 0)
            Step(report, "cutscene warp points", () => CutsceneWarpPoints.Apply(ctx, ported.Select(p => p.Slot.Er).Distinct()));

        // Every ported map side by side, so a step that works on one map and loses pieces on another is seen (RULE 4).
        // In the maps' port order, whichever finished first.
        var coverage = _coverage.Where(r => ported.Any(p => p.Slot.Er == r.Er)).OrderBy(r => order.FindIndex(s => s.Er == r.Er)).ToList();
        var coverageWarnings = MapCoverage.Warnings(coverage);
        try
        {
            foreach (var line in MapCoverage.Summary(coverage))
                report.AppendLine(line);
            var coverageFile = ctx.WorkPath("reports", MapCoverage.ReportFile);
            Directory.CreateDirectory(Path.GetDirectoryName(coverageFile)!);
            File.WriteAllText(coverageFile, MapCoverage.Tsv(coverage));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report.AppendLine($"map coverage: FAILED: {ex.Message}");
        }

        // Read everything back and check it, so the first test in game starts from a known state.
        try
        {
            var check = MapValidate.Validate(ctx, er);
            foreach (var warning in coverageWarnings)
                check.Warn("coverage: " + warning);
            MapValidate.Write(ctx, check);
            report.AppendLine($"validation: {check.Fails} FAIL, {check.Warns} WARN (Mods\\ER\\darksign_maps_check.txt)");
        }
        catch (Exception ex)
        {
            report.AppendLine($"validation: FAILED to run: {ex.Message}");
        }

        var log = ctx.WorkPath("logs", $"port-maps-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(log, report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine($"port-maps: {ported.Count} map(s) ported in {(DateTime.Now - started).TotalSeconds:F0} s; report {log}");
        return ported.Count > 0 ? 0 : 1;
    }

    public const string ModFilesReport = "map-files.tsv";

    /// <summary>Every file in the mod folder with its size and SHA-256, sorted by path (--stop-after-maps).</summary>
    private static string ListModFiles(PorterContext ctx)
    {
        var root = Path.GetDirectoryName(ctx.ModPath("x"))!;
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var lines = new string[files.Count];
        long bytes = 0;
        Parallel.For(0, files.Count, Parallelism.Options, i =>
        {
            using var stream = File.OpenRead(files[i]);
            Interlocked.Add(ref bytes, stream.Length);
            lines[i] = $"{Path.GetRelativePath(root, files[i]).Replace('\\', '/')}\t{stream.Length}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))}";
        });
        var report = ctx.WorkPath("reports", ModFilesReport);
        File.WriteAllLines(report, lines);
        var all = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
        return $"map files: {files.Count} files, {bytes / (1 << 20)} MB in the mod folder; SHA-256 of the whole list {all} (Work\\reports\\{ModFilesReport})";
    }

    /// <summary>The music plan of the ported maps: their DS3 layouts and scripts (DS3's common_func included).</summary>
    private static MapMusic.Plan MusicPlan(PorterContext ctx, GameArchives ds3, IReadOnlyList<MapSlots.Slot> slots)
    {
        var maps = slots.Where(s => !s.Probe && ds3.Contains(MsbPath(s.Ds3))).Select(s => (s, MSB3.Read(ds3.Read(MsbPath(s.Ds3))))).ToList();
        var (scripts, common) = MapEvents.ReadDs3(ds3, slots);
        var all = scripts.Select(kv => new MapMusic.Script(kv.Key, kv.Value)).ToList();
        if (common is not null)
            all.Add(new MapMusic.Script(null, common));
        return MapMusic.Build(maps, all, Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common"))));
    }

    /// <summary>
    /// Deletes everything port-maps writes (and nothing port-weapons or the enemy porter writes: regulation.bin,
    /// darksign_chr_map.tsv and the DLC02 material and item binders stay), for every slot of both map-slot methods.
    /// </summary>
    public static void CleanOutputs(PorterContext ctx)
    {
        var root = Path.GetDirectoryName(ctx.ModPath("darksign_maps.tsv"))!;
        string Local(string archivePath) => Path.Combine([root, .. ModParts(archivePath)]);
        void Delete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"  could not delete {path}: {ex.Message}");
            }
        }

        var every = MapSlots.PaintedWorldSlots().Concat(MapSlots.WorldSlots()).Concat(MapSlots.NewIdSlots()).Concat(MapSlots.LegacyNewIdSlots()).ToList();
        var ids = every.Select(s => s.Er).Distinct().ToList();
        foreach (var id in ids)
        {
            Delete(Local(MsbPath(id)));
            Delete(Local(MsbPath(MapDistantView.Of(id))));
            Delete(Local(MapAtmosphere.GparamPath(id)));
            Delete(Local(MapEvents.ScriptPath(id)));
            // Converted DS3 cutscenes (CutscenePort): sAA_BB_NNNN under the slot's area and block.
            var cutsceneFolder = Path.GetDirectoryName(Local(CutscenePort.ErPath(CutscenePort.Name(id.Area * 1_000_000 + id.Block * 10_000))))!;
            if (Directory.Exists(cutsceneFolder))
                foreach (var file in Directory.GetFiles(cutsceneFolder, $"s{id.Area:D2}_{id.Block:D2}_*.cutscenebnd.dcx"))
                    Delete(file);
            // Their sound banks (CutsceneSound): sd\sAA_BB_NNNN.bnk.
            var soundFolder = Path.Combine(root, "sd");
            if (Directory.Exists(soundFolder))
                foreach (var file in Directory.GetFiles(soundFolder, $"s{id.Area:D2}_{id.Block:D2}_*.bnk"))
                    Delete(file);
            var folder = Local($"/map/{id.AreaFolder}/{id}/x")[..^2];
            if (Directory.Exists(folder))
                foreach (var file in Directory.GetFiles(folder))
                    Delete(file);
        }
        foreach (var area in ids.Select(i => i.AreaFolder).Distinct())
            for (int n = 0; n < 10; n++)
                foreach (var ext in new[] { "tpfbhd", "tpfbdt" })
                    Delete(Local($"/map/{area}/{area}_{n:D4}.{ext}"));
        foreach (var area in ids.Select(i => i.AreaFolder).Distinct())
            foreach (var ext in new[] { "tpfbhd", "tpfbdt" })
                Delete(Local(MapTextures.CommonBinder(area)[..^6] + ext));
        // The per-map copies of a new-ID slot outside its map folder (breakgeom, onav, draw params).
        foreach (var slot in every.Where(s => s.Host != s.Er))
            foreach (var path in NewIdFiles.OutputPaths(
                         [$"/map/breakgeom/lod0/{slot.Host}_lod0.breakgeom.dcx", $"/map/onav/{slot.Host}.onav.dcx",
                          $"/param/drawparam/m{slot.Host.Area:D2}_{slot.Host.Block:D2}_0000.gparam.dcx"], slot.Host, slot.Er))
                Delete(Local(path));
        // MapRegistry.ListPath: rewritten by every full run that adds maps (MapRegistry.Write).
        foreach (var path in new[] { "/event/common.emevd.dcx", MapEvents.CommonFunc, MaterialBinder, OldMaterialBinder, ActionButtons.ModMenu, FlagAlloc.LegacyList, MapRegistry.ListPath })
            Delete(Local(path));
        var pictureFolder = Path.Combine(root, BonfireImages.Folder);
        if (Directory.Exists(pictureFolder))
            foreach (var file in Directory.GetFiles(pictureFolder, "*" + BonfireImages.Extension))
                Delete(file);
                var musicFolder = Path.Combine(root, MapMusic.Folder);
        if (Directory.Exists(musicFolder))
            foreach (var file in Directory.GetFiles(musicFolder))
                Delete(file);
        foreach (var file in new[] { MapEvents.FlagFile, "darksign_maps.tsv", BossArenas.ListFile, CutsceneMenu.ListFile, BossKits.FileName, "darksign_maps_check.txt", FlagAlloc.ProbeFile, MapMusic.ListFile, BonfireTravel.ListFile,
                     DarkSign.Randomizer.Data.EnemyTables.PlacedFile, DarkSign.Randomizer.Data.EnemyTables.ErFile })
            Delete(Path.Combine(root, file));
    }

    /// <summary>
    /// Writes the DS3 armour the placed people wear and their rows; if the rows can't be written, the people go back
    /// to their stand-in's own look so no part names a row that doesn't exist.
    /// </summary>
    private static string PortArmour(PorterContext ctx, GameArchives ds3, GameArchives er, PeopleArmour armour, List<MapSlots.Slot> slots)
    {
        var lines = new List<string>();
        try
        {
            var materials = new List<DarkSign.Porter.Enemies.ChrMaterial>();
            lines.Add(armour.WriteModels(ctx, ds3, er, materials));
            if (materials.Count > 0)
                DarkSign.Porter.Enemies.ChrMaterial.Write(ctx, er, materials);
            var regulation = ActionButtons.LoadRegulation(ctx);
            lines.Add(armour.WriteParams(regulation, PeopleWeapons(ctx, regulation, lines)));
            regulation.Save(ctx.ModPath("regulation.bin"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"people armour: FAILED ({ex.Message}); {armour.Revert(ctx, slots)} people put back on their Elden Ring stand-in's look");
        }
        lines.AddRange(armour.Notes.Select(n => "  people armour: " + n));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Writes the quiet copies of the stand-ins' rows; if they can't be written, the stand-ins go back on Elden Ring's
    /// own rows so no part names a row that doesn't exist.
    /// </summary>
    private static string PortQuietRows(PorterContext ctx, QuietStandIns quiet, List<MapSlots.Slot> slots)
    {
        try
        {
            var regulation = ActionButtons.LoadRegulation(ctx);
            var line = quiet.WriteParams(regulation);
            regulation.Save(ctx.ModPath("regulation.bin"));
            return line;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"stand-in battle music: FAILED ({ex.Message}); {quiet.Revert(ctx, slots)} stand-ins put back on their Elden Ring rows (Elden Ring battle music possible)";
        }
    }

    /// <summary>Gives each ported map its template's field music and ambience area in MapDefaultInfoParam.</summary>
    private static string PortSoundPlaces(PorterContext ctx, List<MapSlots.Slot> slots)
    {
        var regulation = ActionButtons.LoadRegulation(ctx);
        var param = regulation.Get(MapSoundPlace.ParamName) ?? throw new InvalidDataException($"Elden Ring has no {MapSoundPlace.ParamName}");
        var line = MapSoundPlace.Apply(param, slots);
        regulation.Save(ctx.ModPath("regulation.bin"));
        return line;
    }

    /// <summary>
    /// Writes the converted DS3 talk scripts, lines and shops; if that fails, the placed people lose their talk
    /// scripts again so no part names a script that isn't there.
    /// </summary>
    private static string PortTalk(PorterContext ctx, GameArchives ds3, GameArchives er, TalkPort talk, List<MapSlots.Slot> slots)
    {
        var lines = new List<string>();
        try
        {
            var regulation = ActionButtons.LoadRegulation(ctx);
            ParamStore? ds3Params = null;
            try
            {
                ds3Params = ctx.LoadParams(PorterGame.DarkSouls3);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                lines.Add($"  people talk: DS3's params unreadable ({ex.Message})");
            }
            var flagPath = ctx.ModPath(MapEvents.FlagFile);
            var flags = FlagMap.Load(flagPath);
            var defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
            var mover = new EventPort(defs, defs, slots.Select(s => (s.Ds3, s.Er)).Distinct().ToList(), flags);
            var summary = talk.Write(ctx, ds3, er, slots, regulation, ds3Params, f => mover.Move(f, EventPort.Kind.Flag), TalkItems(ctx, ds3, er, regulation, ds3Params, lines));
            flags.Save(flagPath);
            lines.Insert(0, summary);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Insert(0, $"people talk: FAILED ({ex.GetType().Name}: {ex.Message}); {TalkPort.Revert(ctx, slots)} people left silent");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A DS3 shop ware (equipType 0 weapon, 1 armour, 2 ring, 3 goods) -> the Elden Ring item, by the drops' rule.</summary>
    private static Func<int, int, int?> TalkItems(PorterContext ctx, GameArchives ds3, GameArchives er, ParamStore regulation, ParamStore? ds3Params, List<string> lines)
    {
        try
        {
            var notes = new List<string>();
            var items = new DarkSign.Porter.Enemies.DropPort.ItemMap(DarkSign.Porter.Enemies.DropPort.PortedWeapons(ctx, notes));
            var names = DarkSign.Porter.Enemies.DropPort.Names.Load(er, ds3, notes);
            var kinds = new[]
            {
                (DarkSign.Porter.Enemies.DropPort.Kind.Weapon, "EquipParamWeapon"), (DarkSign.Porter.Enemies.DropPort.Kind.Armor, "EquipParamProtector"),
                (DarkSign.Porter.Enemies.DropPort.Kind.Ring, "EquipParamAccessory"), (DarkSign.Porter.Enemies.DropPort.Kind.Goods, "EquipParamGoods"),
            };
            foreach (var (kind, param) in kinds)
            {
                PARAM? ds3Param = ds3Params?.Get(param), erParam = regulation.Get(param);
                items.Add(kind,
                    names.Ds3.GetValueOrDefault(kind) is { Count: > 0 } d ? d : DarkSign.Porter.Enemies.DropPort.RowNames(ds3Param),
                    names.Er.GetValueOrDefault(kind) is { Count: > 0 } e ? e : DarkSign.Porter.Enemies.DropPort.RowNames(erParam),
                    erParam?.Rows.Select(r => r.ID));
            }
            lines.AddRange(notes.Select(n => "  people talk: " + n));
            return (type, id) => type is >= 0 and <= 3 ? items.Map(kinds[type].Item1, id)?.Id : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"  people talk: shop wares not mapped ({ex.Message}); the shelves are empty");
            return (_, _) => null;
        }
    }

    /// <summary>
    /// DS3 weapon or ammunition ID -> the Elden Ring row a ported person holds, by the drops' rule (the ported weapon,
    /// else the Elden Ring item of the same name; <see cref="DarkSign.Porter.Enemies.DropPort.ItemMap"/>), only rows
    /// the regulation has. Null (the stand-ins' own weapons) when the tables can't be read.
    /// </summary>
    private static Func<int, int?>? PeopleWeapons(PorterContext ctx, ParamStore regulation, List<string> lines)
    {
        try
        {
            var notes = new List<string>();
            var items = new DarkSign.Porter.Enemies.DropPort.ItemMap(DarkSign.Porter.Enemies.DropPort.PortedWeapons(ctx, notes));
            var erWeapons = regulation.Get("EquipParamWeapon") ?? throw new InvalidDataException("the regulation has no EquipParamWeapon");
            var ds3Weapons = ctx.LoadParams(PorterGame.DarkSouls3).Get("EquipParamWeapon") ?? throw new InvalidDataException("DS3 has no EquipParamWeapon");
            items.Add(DarkSign.Porter.Enemies.DropPort.Kind.Weapon, DarkSign.Porter.Enemies.DropPort.RowNames(ds3Weapons),
                DarkSign.Porter.Enemies.DropPort.RowNames(erWeapons), erWeapons.Rows.Select(r => r.ID));
            lines.AddRange(notes.Select(n => "  people weapons: " + n));
            // A weapon Elden Ring has by name: DS3's people hold the look-only copy with DS3's model (WeaponPort.Look),
            // when port-weapons wrote its row.
            var looks = DarkSign.Porter.Enemies.DropPort.LookWeapons(ctx).Where(l => erWeapons[l.Value] is not null).ToDictionary(l => l.Key, l => l.Value);
            if (looks.Count > 0)
                lines.Add($"  people weapons: {looks.Count} weapons Elden Ring has by name are held as look-only copies with DS3's model");
            return ds3 => ds3 > 0 && looks.TryGetValue(ds3 - ds3 % 10000, out var look) ? look : items.Map(DarkSign.Porter.Enemies.DropPort.Kind.Weapon, ds3)?.Id;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add($"  people weapons: not mapped ({ex.Message}); people hold their stand-in's weapons");
            return null;
        }
    }

    /// <summary>Points every map piece of a written layout at another PartsDrawParam row.</summary>
    private static void Repoint(PorterContext ctx, MapId erId, short row)
    {
        var path = ctx.ModPath(ModParts(MsbPath(erId)));
        var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
        foreach (var piece in msb.Parts.MapPieces)
            piece.PartsDrawParamID = row;
        File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
    }

    /// <summary>Waits for the port-weapons process (see <see cref="Options.AfterPid"/>) to finish writing regulation.bin.</summary>
    private static void WaitForWeapons(int pid)
    {
        if (pid <= 0)
            return;
        try
        {
            using var weapons = System.Diagnostics.Process.GetProcessById(pid);
            // A finished process's ID can be reused; only another porter is waited for.
            if (!weapons.HasExited && weapons.ProcessName.Contains("DarkSignPorter", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("port-maps: maps done; waiting for port-weapons to finish regulation.bin before the events step");
                // Never forever: after an hour the events step runs on whatever regulation is there, as it would alone.
                if (!weapons.WaitForExit(TimeSpan.FromHours(1)))
                    Console.WriteLine("port-maps: port-weapons is still running after an hour; continuing");
            }
        }
        catch (ArgumentException)
        {
            // Already finished (no process with that ID).
        }
        catch (InvalidOperationException)
        {
            // Exited between the lookup and the wait.
        }
    }

    private static void Step(StringBuilder report, string name, Func<string> step)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            report.AppendLine(step());
        }
        catch (Exception ex)
        {
            report.AppendLine($"{name}: FAILED: {ex.Message}");
            Console.Error.WriteLine($"  {name} failed: {ex.Message}");
        }
        Time(report, name, clock);
    }

    /// <summary>A "time:" line in the report and the console, so a slow build shows which step to speed up.</summary>
    private static void Time(StringBuilder report, string name, System.Diagnostics.Stopwatch clock)
    {
        var line = $"time: {name} {clock.Elapsed.TotalSeconds:F1} s";
        report.AppendLine(line);
        Console.WriteLine(line);
    }

    /// <summary>Ports one map; returns the run's start entity ID, or 0 when the map couldn't be ported.</summary>
    private static int PortMap(PorterContext ctx, GameArchives ds3, GameArchives er, MapSlots.Slot slot, Options options,
        MapTextures textures, List<BinderFile> materials, StringBuilder report, MapTurns turns, int turn)
    {
        var (ds3Id, erId) = (slot.Ds3, slot.Er);
        // What every map shares is touched only inside a turn (MapTurns): this one covers the layout, the enemies and
        // people placed, and the material templates. A map that fails gives its turns back in MapTurns.Run.
        var stepClock = System.Diagnostics.Stopwatch.StartNew();
        turns.Take(MapTurns.Prepare, turn);
        double waited = stepClock.Elapsed.TotalSeconds;
        stepClock.Restart();
        int reportStart = report.Length;   // this map's part of the port log, for its coverage row (MapCoverage)
        EnemyPort.Result? enemiesPlaced = null;
        ObjectPort.Result? objectsPlaced = null;
        (int Fights, int Warps) bossWarps = (0, 0);
        int collisionFilesEmpty = 0, collisionFilesFailed = 0;
        // A slot with a template map (the spare IDs of map-slots B, the Painted World's m19_70) takes its formats and
        // template parts from the template, a colosseum proven to load; its per-map side files follow (NewIdFiles).
        var hostId = slot.Template is { } template && er.Contains(MsbPath(template)) ? template
            : er.Contains(MsbPath(erId)) ? erId : slot.Host;
        if (slot.Probe && er.Contains(MsbPath(erId)))
            throw new InvalidDataException($"{erId} already exists in Elden Ring, so it can't test new map IDs");
        if (hostId != erId)
            report.AppendLine($"  {erId}: formats, template parts and per-map side files come from {hostId}");
        var ds3Msb = MSB3.Read(ds3.Read(MsbPath(ds3Id)));
        var (hostBytes, msbCompression) = er.ReadWithCompression(MsbPath(hostId));
        var host = MSBE.Read(hostBytes);

        var slots = MapSlots.All.Where(s => !s.Probe && (options.Only.Count == 0 || options.Only.Contains(s.Ds3.ToString())))
            .Select(s => (s.Ds3, s.Er)).ToList();
        var layout = MsbPort.Convert(ds3Msb, host, ds3Id, erId, slots, ConnectTemplate(er));
        report.AppendLine($"{ds3Id} -> {erId}: {layout.MapPieces} map pieces, {layout.Collisions} collisions, {layout.Players} player points");
        foreach (var note in layout.Notes)
            report.AppendLine($"  note: {note}");

        // DS3 part name -> ported part name, for the regions and map-file events that name parts.
        var partNames = new Dictionary<string, string>(StringComparer.Ordinal);
        // Enemies and bosses where DS3 had them. A failure here leaves the map empty of enemies, not broken.
        {
            try
            {
                var enemies = Enemies(ctx, ds3, er, options, report);
                var scripts = Ds3Scripts(ds3, ds3Id).ToList();
                var bosses = EnemyPort.BossEncounter(ds3Msb, EnemyPort.BossEntities(scripts, ds3Layout: true));
                var seats = EnemyPort.Seats(ds3Msb, EnemyPort.EventWarps(scripts));
                var placed = EnemyPort.Place(ds3Msb, layout.Msb, ds3Id, erId,
                    enemies.Chooser, e => enemies.Describe(e, bosses), EnemyPort.AnyEnemyPart(enemies.Catalog), _placedEnemies, partNames,
                    enemies.People, seats, armour: _armour, talk: _talk, quiet: _quiet);
                enemiesPlaced = placed;
                report.AppendLine($"  enemies: {placed.Placed} placed ({placed.Ported} ported DS3 characters, {placed.StandIns} Elden Ring stand-ins, "
                                  + $"{placed.People} people{(_armour is null ? ", DS3's own people only" : ", DS3's own people and DS3 humans in their DS3 armour")}), {placed.Npcs} people left out, {placed.Skipped} with nothing to stand in; {bosses.Count} DS3 boss entities");
                foreach (var note in placed.Notes)
                    report.AppendLine($"  enemies: {note}");
            }
            catch (Exception ex)
            {
                layout.Msb.Parts.Enemies.Clear();
                layout.Msb.Models.Enemies.Clear();
                partNames.Clear();
                report.AppendLine($"  enemies: FAILED ({ex.Message}); the map has no enemies");
            }
        }

        // Models. The material templates and binder layout come from vanilla map pieces (see MaterialTemplates).
        var (models, hostBnd, bndCompression, binderMap) = MaterialTemplates(er, hostId, host);
        // A test slot (see PieceTest) swaps the models' materials, or the models themselves, for the host's own.
        var test = PieceTestFor(slot, options);
        if (test == PieceTest.VanillaMaterial)
        {
            try
            {
                var hostFlver = FLVER2.Read(hostBnd.Files.First(f => FLVER2.Is(f.Bytes)).Bytes);
                models = MapModelPort.Create([hostFlver], AllMatbins(er), hostFlver, plain: true).WithVanillaMaterials();
            }
            catch (InvalidDataException ex)
            {
                report.AppendLine($"  piece test vanilla-material: not possible here ({ex.Message}); the map is built normally");
                test = PieceTest.None;
            }
        }
        if (test == PieceTest.PortedMaterial)
            models = models.WithTemplateTextures();
        models.TintMaterials = options.TintMaterials;
        if (test != PieceTest.None)
            report.AppendLine(test switch
            {
                PieceTest.VanillaModel => $"  TEST MAP (vanilla-model): every map piece is Elden Ring's own model from {binderMap}, not DS3's. It loads: binders, layout and draw settings are fine",
                PieceTest.VanillaMaterial => $"  TEST MAP (vanilla-material): DS3 geometry drawn with Elden Ring's own material {models.TemplateSummary}. It loads: the converted geometry is fine",
                _ => "  TEST MAP (ported-material): DS3 geometry drawn with the port's own materials, which keep Elden Ring's textures. It loads: the added materials are fine, and a map that still hangs waits on its textures",
            });
        report.AppendLine($"  material templates: {models.TemplateSummary} (binder layout from {binderMap})");
        lock (_layouts)
            _layouts.UnionWith(models.TemplateLayouts());
        // The main slot this DS3 map's pieces were converted into, if this slot copies them (it waited for that slot).
        (MapId Er, HashSet<string> Textures) main;
        lock (_converted)
            _converted.TryGetValue(ds3Id, out main);
        turns.Give(MapTurns.Prepare, turn);
        report.AppendLine($"  time: layout, enemies and material templates {stepClock.Elapsed.TotalSeconds:F0} s"
                          + (waited >= 1 ? $" (after waiting {waited:F0} s for the maps before it)" : ""));

        // Every map piece binder converts in parallel (thousands per map). A probe slot copies what its main slot
        // already converted from the same DS3 map, renamed to its own map ID, instead of converting it again.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var mapPieces = layout.Msb.Models.MapPieces.ToList();
        var failures = new string?[mapPieces.Count];
        int copied = 0;
        Parallel.For(0, mapPieces.Count, Parallelism.Options, i =>
        {
            var number = ModelNumber(mapPieces[i].Name);
            var target = ctx.ModPath(ModParts(ErMapBndPath(erId, number)));
            try
            {
                if (test == PieceTest.VanillaModel)
                {
                    File.WriteAllBytes(target, DCX.Compress(MapModelPort.VanillaBinder(hostBnd, erId, number, binderMap).Write(), bndCompression));
                    return;
                }
                if (main.Er != erId && main.Er != default && File.Exists(ctx.ModPath(ModParts(ErMapBndPath(main.Er, number)))))
                {
                    var bnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(ctx.ModPath(ModParts(ErMapBndPath(main.Er, number)))), out var c));
                    foreach (var f in bnd.Files)
                        f.Name = f.Name.Replace(main.Er.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase);
                    File.WriteAllBytes(target, DCX.Compress(bnd.Write(), c));
                    Interlocked.Increment(ref copied);
                    return;
                }
                var source = Ds3MapBndPath(ds3Id, number);
                if (!ds3.Contains(source))
                    throw new FileNotFoundException(source);
                var ds3Bnd = BND4.Read(ds3.Read(source));
                var binder = models.ConvertBinder(ds3Bnd, hostBnd, ds3Id, erId, number, binderMap);
                File.WriteAllBytes(target, DCX.Compress(binder.Write(), bndCompression));
            }
            catch (Exception ex)
            {
                failures[i] = ex.Message;
            }
        });
        var missingModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mapPieces.Count; i++)
            if (failures[i] is { } failure)
            {
                missingModels.Add(mapPieces[i].Name);
                report.AppendLine($"  model {mapPieces[i].Name}: left out ({failure})");
            }
        foreach (var note in models.Notes.Distinct().Take(20))
            report.AppendLine($"  note: {note}");
        if (models.SolidDecals > 0)
            report.AppendLine($"  solid models under DS3's decal shader: {models.SolidDecals} meshes drawn as ordinary surfaces");
        if (models.EmptyFlvers > 0)
            report.AppendLine($"  extra FLVERs: {models.EmptyFlvers} left out of their binders (only shadow casters, water or effects in them); the drawable FLVER beside each was kept");
        if (models.LeftOut.Count > 0)
            report.AppendLine($"  meshes left out (no Elden Ring material for them yet): {models.LeftOut.Values.Sum()} ("
                              + string.Join(", ", models.LeftOut.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")) + ")");
        // DS3's low-detail stand-ins, judged now that every model's meshes are known. A failure keeps them all.
        var standIns = new HashSet<string>(StringComparer.Ordinal);
        var farViews = new HashSet<string>(StringComparer.Ordinal);
        if (copied > 0 || test != PieceTest.None)
            report.AppendLine("  low-detail stand-ins: not judged in a copied or test map");
        else
        {
            try
            {
                bool layerOk = MsbPort.BaseLayerMeans(ds3Msb);
                var skip = ds3Msb.Parts.MapPieces
                    .Where(p => missingModels.Contains(p.ModelName) || MsbPort.HelperKind(p, layerOk) is not null)
                    .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var judged = MapStandIns.Find(ds3Msb, models.ModelMeshes, skip);
                _standInWorld[ds3Id] = (erId, judged, ds3Msb.Parts.ConnectCollisions.Select(c => c.MapID.ToArray()).ToList(), ds3Msb);
                standIns = judged.LeftOut;
                farViews = judged.FarViews;
                report.AppendLine("  " + judged.Summary);
                foreach (var line in judged.Lines)
                    report.AppendLine("    " + line);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report.AppendLine($"  low-detail stand-ins: FAILED ({ex.Message}); all kept");
            }
        }
        try
        {
            var piecesReport = ctx.WorkPath("reports", $"map-pieces-{erId}.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(piecesReport)!);
            File.WriteAllText(piecesReport, PieceReport(ds3Msb, models, missingModels, standIns, farViews));
            report.AppendLine($"  piece report: Work\\reports\\{Path.GetFileName(piecesReport)} (every DS3 map piece: where it is, how big, its materials, and why it was left out)");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  piece report: FAILED: {ex.Message}");
        }
        // Large pieces load their full textures from further out (MapDrawParams.TextureBySize).
        {
            try
            {
                report.AppendLine("  " + MapDrawParams.PointBySize(layout.Msb, model =>
                    models.ModelMeshes.TryGetValue(ModelNumber(model), out var meshes) && meshes.Any(m => m.Vertices > 0)
                        ? Vector3.Distance(meshes.Where(m => m.Vertices > 0).Select(m => m.Min).Aggregate(Vector3.Min),
                            meshes.Where(m => m.Vertices > 0).Select(m => m.Max).Aggregate(Vector3.Max)) / 2
                        : null));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report.AppendLine($"  texture distance by size: FAILED ({ex.Message}); every piece keeps row {MapDrawParams.FirstRow}");
            }
        }
        // From here to the boss arenas the map changes what the run shares (the area's textures, the materials, HKLib's
        // caches under the collision and navmesh, the object port, the arena list): one map at a time, in port order.
        stepClock.Restart();
        turns.Take(MapTurns.Shared, turn);
        waited = stepClock.Elapsed.TotalSeconds;
        if (options.TintMaterials)
        {
            foreach (var (name, marks, rgb) in MapModelPort.Tints)
            {
                textures.Provide(name, new TPF.Texture(name, 0, 0, ModelPort.SolidDxt1(rgb), TPF.TPFPlatform.PC));
                report.AppendLine($"  tint (diagnostic): {name[9..]} = {marks}");
            }
        }
        textures.Need(models.Textures, ds3Id);
        int notesSoFar = models.Notes.Count;
        materials.AddRange(models.MatbinFiles(0));
        foreach (var note in models.Notes.Skip(notesSoFar).Distinct().Take(5))
            report.AppendLine($"  note: {note}");
        report.AppendLine($"  sunlight: {models.Materials.Count(m => m.Template.Kind is "nolight" or "nolight-blend" or "nolight-edge")} unlit materials (sky domes and the layers around them) cast no shadow");
        if (test != PieceTest.VanillaModel && copied == 0)
        {
            report.AppendLine($"  load: {models.LoadSummary}");
            report.AppendLine($"  {models.Lods.Summary(MapDrawParams.Lod.Vanilla)}");
        }
        if (copied == 0)
        {
            report.AppendLine("  " + models.Overlays.Summary);
            report.AppendLine("  " + models.Blends.Summary);
            report.AppendLine("  " + models.Skies.Summary);
            report.AppendLine("  " + models.UnlitBlends.Summary);
            report.AppendLine("  " + models.HalfNormalStats.Summary);
        }
        if (copied > 0)
            textures.Need(main.Textures, ds3Id);
        else if (!slot.Probe && test == PieceTest.None)
            lock (_converted)
                _converted[ds3Id] = (erId, models.Textures);
        report.AppendLine($"  time: {mapPieces.Count} map piece binders in {clock.Elapsed.TotalSeconds - waited:F0} s"
                          + (copied > 0 ? $" ({copied} copied from {main.Er})" : "") + $" on {Parallelism.Threads} threads"
                          + (waited >= 1 ? $" (then waited {waited:F0} s for the maps before it)" : ""));

        // Collision.
        var missingCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emptyCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collisionModels = layout.Msb.Models.Collisions.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        clock.Restart();
        var collisionResults = new Dictionary<string, MapCollisions.Result>();
        foreach (var kind in new[] { "h", "l" })
        {
            var result = MapCollisions.Port(ctx, ds3, er, ds3Id, erId, kind, hostId);
            collisionResults[kind] = result;
            report.AppendLine($"  collision {kind}: {result.Summary}");
            collisionFilesEmpty += result.Empty?.Count ?? 0;
            collisionFilesFailed += result.Failed;
        }
        foreach (var (name, isEmpty) in CollisionModelsLeftOut(collisionModels, collisionResults))
        {
            missingCollisions.Add(name);
            if (isEmpty)
                emptyCollisions.Add(name);
        }
        foreach (var name in missingCollisions)
            report.AppendLine($"  collision {name}: left out ({(emptyCollisions.Contains(name) ? "its DS3 file is empty: no collision to convert" : "no converted file")})");
        foreach (var line in EmptySideNotes(ds3Msb, collisionResults))
            report.AppendLine("  " + line);
        report.AppendLine($"  time: collision {clock.Elapsed.TotalSeconds:F0} s");
        clock.Restart();

        // Navmesh, so enemies placed in the map can find their way. A failure leaves them running straight at the player.
        try
        {
            foreach (var line in MapNavmesh.Port(ctx, ds3, er, ds3Id, erId, hostId, ds3Msb))
                report.AppendLine("  " + line);
        }
        catch (Exception ex)
        {
            report.AppendLine($"  navmesh: FAILED: {ex.Message}");
        }
        report.AppendLine($"  time: navmesh {clock.Elapsed.TotalSeconds:F0} s");

        // DS3's objects as assets, then the regions and map-file events (they name enemies and objects). Each is
        // checked by writing the layout and reading it back; one that fails is taken out again, so the map is never
        // worse off than without it.
        clock.Restart();
        if (test != PieceTest.None)
            report.AppendLine("  objects: not placed in a test map");
        else
        {
            try
            {
                var objects = Objects(ctx, ds3, er, options, models, report);
                var placed = objects.Place(ds3Msb, layout.Msb, ds3Id, erId);
                MSBE.Read(layout.Msb.Write());
                objectsPlaced = placed;
                foreach (var (from, to) in placed.PartNames)
                    partNames[from] = to;
                report.AppendLine("  " + placed.Summary);
                foreach (var note in placed.Notes)
                    report.AppendLine($"  objects: {note}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                objectsPlaced = null;
                ObjectPort.Strip(layout.Msb);
                report.AppendLine($"  objects: FAILED ({ex.Message}); the map has no objects");
            }
            report.AppendLine($"  time: objects {clock.Elapsed.TotalSeconds:F0} s");
        }
        IReadOnlySet<int> warpPoints = new HashSet<int>();
        {
            var playersBefore = layout.Msb.Parts.Players.Count;
            try
            {
                var regions = RegionPort.Convert(ds3Msb, layout.Msb, ds3Id, erId, partNames);
                MSBE.Read(layout.Msb.Write());
                report.AppendLine("  " + regions.Summary);
                foreach (var note in regions.Notes)
                    report.AppendLine($"  regions: {note}");
                // DS3 warps land on regions, Elden Ring's on player parts (Vordt's door sent the player to the start).
                var warpNotes = new List<string>();
                warpPoints = MapEvents.WarpPoints(ctx, ds3, ds3Id);
                RegionPort.AddWarpPoints(ds3Msb, layout.Msb, ds3Id, erId, warpPoints, warpNotes);
                MSBE.Read(layout.Msb.Write());
                foreach (var note in warpNotes)
                    report.AppendLine($"  {note}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RegionPort.Strip(layout.Msb);
                if (layout.Msb.Parts.Players.Count > playersBefore)
                    layout.Msb.Parts.Players.RemoveRange(playersBefore, layout.Msb.Parts.Players.Count - playersBefore);
                warpPoints = new HashSet<int>();
                report.AppendLine($"  regions: FAILED ({ex.Message}); the map has no DS3 regions");
            }
        }

        // DS3's room reverb per piece of ground as Elden Ring reverb regions (MapReverb): without them Elden Ring's
        // automatic reverb echoes everywhere in DS3's walled-in maps. A failure leaves the automatic reverb.
        try
        {
            var reverb = MapReverb.Port(ds3, ds3Msb, ds3Id, layout.Msb);
            MSBE.Read(layout.Msb.Write());
            report.AppendLine("  " + reverb.Summary);
            foreach (var note in reverb.Notes)
                report.AppendLine($"  reverb: {note}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MapReverb.Strip(layout.Msb);
            report.AppendLine($"  reverb: FAILED ({ex.Message}); Elden Ring's automatic reverb");
        }

        _partNames[erId] = new Dictionary<string, string>(partNames, StringComparer.Ordinal);

        // An arrival point in each boss arena, for the F1 menu's boss warps. A failure leaves the map without them.
        try
        {
            if (ds3.Contains(MapEvents.ScriptPath(ds3Id)))
            {
                // Flags DS3's common events turn on aren't talk-only (BossArenas.TalkGate).
                var ds3CommonFunc = ds3.Contains(MapEvents.CommonFunc) ? new[] { EMEVD.Read(ds3.Read(MapEvents.CommonFunc)) } : [];
                var fights = BossArenas.Find(ds3Msb, EMEVD.Read(ds3.Read(MapEvents.ScriptPath(ds3Id))), ds3Id,
                    e => _enemies?.Ds3Npc?[e.NPCParamID]?.Name, warpPoints, ds3CommonFunc);
                var notes = new List<string>();
                var arenas = BossArenas.Place(layout.Msb, ds3Msb, fights, slot, notes);
                _arenas.RemoveAll(a => a.Er == erId);
                _arenas.AddRange(arenas);
                report.AppendLine($"  boss arenas: {arenas.Count} of {fights.Count} DS3 boss fights get an F1 warp");
                bossWarps = (fights.Count, arenas.Count);
                foreach (var note in notes)
                    report.AppendLine($"  {note}");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            layout.Msb.Parts.Players.RemoveAll(p => p.EntityID % 10_000 >= BossArenas.FirstPointNumber && p.EntityID % 10_000 < BossArenas.FirstPointNumber + 30);
            report.AppendLine($"  boss arenas: FAILED ({ex.Message}); no boss warps for this map");
        }
        turns.Give(MapTurns.Shared, turn);
        stepClock.Restart();

        // Drop what has no model, then write the layout.
        var msb = layout.Msb;
        int piecesNoModel = msb.Parts.MapPieces.Count(p => missingModels.Contains(p.ModelName));
        msb.Parts.MapPieces.RemoveAll(p => missingModels.Contains(p.ModelName));
        msb.Parts.MapPieces.RemoveAll(p => standIns.Contains(p.Name));
        var usedModels = msb.Parts.MapPieces.Select(p => p.ModelName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        msb.Models.MapPieces.RemoveAll(m => missingModels.Contains(m.Name) || (standIns.Count > 0 && !usedModels.Contains(m.Name)));
        // Pieces whose model carries DS3's lower detail lists switch to them by distance (MapDrawParams.LodRow).
        {
            int switching = 0;
            lock (models.LodModels)
                foreach (var piece in msb.Parts.MapPieces.Where(p => MapDrawParams.IsPieceRow(p.PartsDrawParamID) && models.LodModels.Contains(ModelNumber(p.ModelName))))
                {
                    piece.PartsDrawParamID += MapDrawParams.LodOffset;
                    switching++;
                }
            report.AppendLine($"  lower detail by distance: {switching} of {msb.Parts.MapPieces.Count} map pieces (their PartsDrawParam row + {MapDrawParams.LodOffset}: {MapDrawParams.Lod.Vanilla})");
        }
        msb.Parts.Collisions.RemoveAll(p => missingCollisions.Contains(p.ModelName));
        msb.Parts.ConnectCollisions.RemoveAll(p => missingCollisions.Contains(p.ModelName)
                                                   || msb.Parts.Collisions.All(c => c.Name != p.CollisionName));
        msb.Models.Collisions.RemoveAll(m => missingCollisions.Contains(m.Name));
        var keptCollisions = msb.Parts.Collisions.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var enemy in msb.Parts.Enemies.Where(e => !string.IsNullOrEmpty(e.CollisionPartName) && !keptCollisions.Contains(e.CollisionPartName)))
            enemy.CollisionPartName = "";
        // Light: ambient from the host's environment map, lamps from DS3's light file. A failure leaves the map
        // as dark as it was before, not broken.
        List<MapBakedLight.Volume> baked = [];
        {
            foreach (var (name, step) in new (string, Func<string>)[]
                     {
                         ("ambient light", () => MapLighting.AddAmbient(msb, host, ds3Msb)),
                         ("baked light", () =>
                         {
                             var (volumes, line) = MapBakedLight.Build(ds3, er, ds3Id, hostId, msb, ds3Msb);
                             baked = volumes;
                             return line;
                         }),
                         ("environment light", () => MapEnvironment.Write(ctx, er, erId, hostId, baked)),
                         ("light volume info", () => MapBakedLight.WriteIvInfo(ctx, er, hostId, erId, baked)),
                         ("lamps", () => MapLighting.PortLights(ctx, ds3, er, ds3Id, erId, hostId, ds3Msb)),
                     })
            {
                try
                {
                    report.AppendLine("  " + step());
                }
                catch (Exception ex)
                {
                    report.AppendLine($"  {name}: FAILED: {ex.Message}");
                }
            }
        }
        // Sky: the host's distant view (Elden Ring's sky and the Erdtree) is emptied. A failure leaves it as it was.
        try
        {
            report.AppendLine("  " + MapDistantView.Write(ctx, er, erId));
        }
        catch (Exception ex)
        {
            report.AppendLine($"  sky: FAILED: {ex.Message}");
        }
        // Colours of light and air: DS3's instead of the colosseum's. A failure leaves the host's.
        var lightSets = new HashSet<int>();
        try
        {
            report.AppendLine("  " + MapAtmosphere.Write(ctx, ds3, er, ds3Id, erId, hostId, ds3Msb, msb, lightSets));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { File.Delete(ctx.ModPath(ModParts(MapAtmosphere.GparamPath(erId)))); } catch (IOException) { }
            lightSets.Clear();
            report.AppendLine($"  atmosphere: FAILED: {ex.Message}; the host's colours stay");
        }
        // Parts on a light set the slot's draw params lack go back to the default (all of them when the atmosphere
        // wasn't written: the host's file has none of DS3's sets).
        report.AppendLine("  " + MapLightSets.Finish(msb, lightSets));
        File.WriteAllBytes(ctx.ModPath(ModParts(MsbPath(erId))), DCX.Compress(msb.Write(), msbCompression));
        report.AppendLine($"  time: lighting, sky, atmosphere and layout {stepClock.Elapsed.TotalSeconds:F0} s");
        report.AppendLine($"  written: {msb.Parts.MapPieces.Count} map pieces, {msb.Parts.Collisions.Count} collisions, {msb.Parts.Players.Count} player points, {msb.Parts.Enemies.Count} enemies, "
                          + $"{msb.Parts.Assets.Count} objects, {msb.Regions.GetEntries().Count} regions, {msb.Events.GetEntries().Count} map-file events");
        if (!slot.Probe)
            lock (_coverage)
            {
                _coverage.RemoveAll(r => r.Er == erId);
                _coverage.Add(new MapCoverage.Row(ds3Id, erId, slot.Name)
                {
                    PiecesDs3 = ds3Msb.Parts.MapPieces.Count, PiecesWritten = msb.Parts.MapPieces.Count, PiecesNoModel = piecesNoModel,
                    Models = mapPieces.Count, ModelsLeftOut = missingModels.Count, MeshesLeftOut = models.LeftOut.Values.Sum(),
                    CollisionsDs3 = ds3Msb.Parts.Collisions.Count, CollisionsWritten = msb.Parts.Collisions.Count,
                    CollisionFilesEmpty = collisionFilesEmpty, CollisionFilesFailed = collisionFilesFailed,
                    ObjectsDs3 = ds3Msb.Parts.Objects.Count, ObjectsPlaced = objectsPlaced?.Placed ?? 0,
                    EnemiesPlaced = enemiesPlaced?.Placed ?? 0, EnemiesPorted = enemiesPlaced?.Ported ?? 0,
                    EnemyStandIns = enemiesPlaced?.StandIns ?? 0, EnemiesNothing = enemiesPlaced?.Skipped ?? 0,
                    BossFights = bossWarps.Fights, BossWarps = bossWarps.Warps,
                    Failed = MapCoverage.FailedLines(report.ToString(reportStart, report.Length - reportStart)),
                });
            }

        // The slot's own event script would run colosseum events; replace it with an empty one.
        // A new map ID gets one too, in the template's format.
        var eventPath = $"/event/{erId}.emevd.dcx";
        var hostEventPath = $"/event/{hostId}.emevd.dcx";
        if (er.Contains(hostEventPath))
        {
            var vanilla = EMEVD.Read(er.Read(hostEventPath));
            var (_, eventCompression) = er.ReadWithCompression(hostEventPath);
            File.WriteAllBytes(ctx.ModPath(ModParts(eventPath)), DCX.Compress(RunEvents.EmptyMapScript(vanilla).Write(), eventCompression));
        }

        if (msb.Parts.Players.All(p => p.EntityID != (uint)layout.StartEntityId))
            return 0;
        return msb.Parts.MapPieces.Count > 0 && msb.Parts.Collisions.Count > 0 ? layout.StartEntityId : 0;
    }

    /// <summary>What enemy placement needs once per run: the stand-in catalog, the ported-character table and DS3's names.</summary>
    private sealed record EnemyContext(List<EnemyPort.ErEnemy> Catalog, EnemyPort.Chooser Chooser, PARAM? Ds3Npc, EnemyPort.People People)
    {
        public EnemyPort.Ds3Enemy Describe(MSB3.Part.Enemy e, HashSet<long> bosses)
        {
            var name = Ds3Npc?[e.NPCParamID]?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = e.ModelName;
            // A talker in a boss fight (Friede, Ariandel) is the boss, not a person (EnemyPort.BossEncounter).
            var npc = EnemyPort.IsPerson(e, bosses);
            return new EnemyPort.Ds3Enemy(e.ModelName, e.NPCParamID, name, bosses.Contains(e.EntityID), npc);
        }
    }

    private static ObjectPort? _objects;
    private static bool _objectsFailed;

    /// <summary>
    /// The run's object port, made with the first map that needs it: it numbers every object model of every DS3 map
    /// that has a slot, so a partial (--only) run gives each model the number a full run does.
    /// </summary>
    private static ObjectPort Objects(PorterContext ctx, GameArchives ds3, GameArchives er, Options options, MapModelPort models, StringBuilder report)
    {
        if (_objects is not null)
            return _objects;
        if (_objectsFailed)
            throw new InvalidDataException("the object port could not start (see the first map)");
        try
        {
            var maps = new List<MSB3>();
            foreach (var slot in MapSlots.All.Where(s => !s.Probe && ds3.Contains(MsbPath(s.Ds3))))
                maps.Add(MSB3.Read(ds3.Read(MsbPath(slot.Ds3))));
            PARAM? objectParam = null;
            try { objectParam = ctx.LoadParams(PorterGame.DarkSouls3).Get("ObjectParam"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { report.AppendLine($"  note: DS3 ObjectParam unavailable ({ex.Message}); no object gets collision"); }
            var method = objectParam is null ? ObjectPort.Method.NoCollision : ObjectPort.Method.Full;
            PARAM? objActParam = null;
            try { objActParam = ctx.LoadParams(PorterGame.DarkSouls3).Get("ObjActParam"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { report.AppendLine($"  note: DS3 ObjActParam unavailable ({ex.Message}); doors and levers move only by script"); }
            return _objects = new ObjectPort(ctx, ds3, er, models.Fresh(), method, maps, objectParam, MapCollisions.LastTemplate)
            {
                Ds3ObjActParam = objActParam,
            };
        }
        catch
        {
            _objectsFailed = true;
            throw;
        }
    }

    private static EnemyContext? _enemies;
    /// <summary>DS3's humans in their DS3 armour (null: off, or not planned).</summary>
    private static PeopleArmour? _armour;
    private static QuietStandIns? _quiet;
    private static TalkPort? _talk;
    /// <summary>darksign_enemies.tsv rows for this run: every DS3 enemy placed, for the randomizer.</summary>
    private static readonly List<string> _placedEnemies = [];

    /// <summary>The boss arenas of the maps ported so far (BossArenas), for the F1 menu's warps.</summary>
    private static readonly List<BossArenas.Arena> _arenas = [];
    /// <summary>Each ported map's DS3 -> Elden Ring part names, for the cutscene port (cutscenes name the parts they move).</summary>
    private static readonly Dictionary<MapId, Dictionary<string, string>> _partNames = [];
    /// <summary>Each ported map's coverage row, for the side-by-side summary at the end of the run (<see cref="MapCoverage"/>).</summary>
    private static readonly List<MapCoverage.Row> _coverage = [];

    // Each ported DS3 map's judged pieces and its connect collisions' map numbers, for "world stand-ins".
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MapId, (MapId Er, MapStandIns.Result Judged, List<byte[]> Connects, MSB3 Msb)> _standInWorld = new();

    /// <summary>
    /// The "world stand-ins" step: DS3's doorway display lists
    /// (<see cref="MapConnectDisplay"/>) written into every ported layout, so a loaded neighbour shows from each doorway
    /// only what DS3 shows there and nothing from collisions with no doorway to it. Report:
    /// Work\reports\map-world-display.tsv (every piece standing over a neighbour's floor where DS3 never shows it, and
    /// where the port now hides it).
    /// </summary>
    private static string WorldDoorways(PorterContext ctx, GameArchives ds3)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var entries = _standInWorld.OrderBy(e => e.Key.ToString()).ToList();
        var floors = new System.Collections.Concurrent.ConcurrentDictionary<MapId, List<MapWorldFloor.Cell>>();
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)) }, e =>
        {
            try { floors[e.Key] = MapWorldFloor.Sample(ds3, e.Key, e.Value.Msb); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                floors[e.Key] = [];
                failed.Add($"{e.Key}: {ex.Message}");
            }
        });
        var data = entries.Select(e => new MapConnectDisplay.MapData(e.Key, e.Value.Er, e.Value.Msb, e.Value.Judged.Pieces, floors[e.Key])).ToList();
        var plan = MapConnectDisplay.Build(data);
        var check = MapConnectDisplay.Verify(data, plan);
        var lines = new List<string>
        {
            $"  check (walkable floor of each map and its neighbours, DS3's groups against the ones written): a neighbour's pieces drawn over floor where DS3 hides them "
            + $"{check.NeighbourBefore:N0} cells before, {check.NeighbourAfter:N0} after; own stand-ins for a neighbour on its doorways {check.OwnBefore:N0} before, {check.OwnAfter:N0} after; "
            + $"cells where a piece DS3 shows is now hidden: {check.HiddenButShown:N0}"
            + (check.Leftovers.Count > 0 ? $"; still drawn: {string.Join(", ", check.Leftovers.Take(8))}" : "")
            + (check.WronglyHidden.Count > 0 ? $"; wrongly hidden: {string.Join(", ", check.WronglyHidden.Take(8))}" : ""),
        };
        var tsv = new StringBuilder("DS3 map\tElden Ring slot\tpiece\tstands over the floor of\thidden on the doorways into them\n");
        int written = 0;
        foreach (var d in data)
        {
            if (!plan.Maps.TryGetValue(d.Er, out var mp))
                continue;
            var path = ctx.ModPath(ModParts(MsbPath(d.Er)));
            if (!File.Exists(path))
                continue;
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
            var (pieces, collisions, connects) = MapConnectDisplay.Apply(msb, mp);
            File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
            written++;
            lines.Add($"  {d.Ds3} -> {d.Er}: {pieces} pieces, {collisions} collisions, {connects} doorways written; {mp.DoorwayLists} DS3 doorway lists into it; "
                      + $"{d.Floor.Count:N0} floor cells; {mp.BorderHidden.Count} own pieces over a neighbour hidden on its doorways"
                      + (mp.BorderHidden.Count > 0 ? $" ({string.Join(", ", mp.BorderHidden.Keys.Take(12))}{(mp.BorderHidden.Count > 12 ? ", ..." : "")})" : ""));
            foreach (var (piece, over) in mp.BorderHidden.OrderBy(b => b.Key, StringComparer.Ordinal))
                tsv.Append($"{d.Ds3}\t{d.Er}\t{piece}\t{string.Join(",", over)}\tyes\n");
        }
        var report = ctx.WorkPath("reports", "map-world-display.tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, tsv.ToString());
        return $"world stand-ins: DS3's doorway display lists written into {written} maps own pieces drawn from all of their own map's collisions, "
               + $"a neighbour's pieces only through a doorway that shows them in DS3 ({clock.Elapsed.TotalSeconds:F0} s); {string.Join("; ", plan.Notes)}"
               + (failed.IsEmpty ? "" : $"; floor not read for {string.Join(", ", failed)}")
               + $"; report {report}"
               + (lines.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, lines) : "");
    }

    private static EnemyContext Enemies(PorterContext ctx, GameArchives ds3, GameArchives er, Options options, StringBuilder report)
    {
        if (_enemies is not null)
            return _enemies;
        var notes = new List<string>();
        PARAM? erNpc = null, ds3Npc = null, erCharaInit = null;
        try
        {
            var erParams = ctx.LoadParams(PorterGame.EldenRing);
            erNpc = erParams.Get("NpcParam");
            erCharaInit = erParams.Get("CharaInitParam");
        }
        catch (Exception ex) { notes.Add($"Elden Ring NpcParam names unavailable ({ex.Message}); stand-ins picked by model only"); }
        try { ds3Npc = ctx.LoadParams(PorterGame.DarkSouls3).Get("NpcParam"); }
        catch (Exception ex) { notes.Add($"DS3 NpcParam names unavailable ({ex.Message}); every DS3 enemy gets the most common stand-in"); }
        var catalog = EnemyPort.BuildCatalog(er, erNpc, notes, out var people, erCharaInit);
        _quiet = QuietStandIns.Create(erNpc);
        notes.Add(_quiet is not null ? $"stand-in battle music: stand-ins whose Elden Ring row has a threat level other than {_quiet.QuietLevel} get a quiet copy of it"
            : "stand-in battle music: Elden Ring's NpcParam threat levels unreadable; stand-ins keep their own (Elden Ring battle music possible)");
        _armour = null;
        try
        {
            var ds3Params = ctx.LoadParams(PorterGame.DarkSouls3);
            var maps = MapSlots.All.Select(s => s.Ds3).Distinct().Where(id => ds3.Contains(MsbPath(id))).Select(id => MSB3.Read(ds3.Read(MsbPath(id))));
            _armour = PeopleArmour.Plan(ds3Params.Get("CharaInitParam")!, ds3Params.Get("EquipParamProtector")!, PeopleArmour.PeopleIn(maps),
                n => PeopleArmour.Slots.Any(slot => er.Contains(PeopleArmour.PartsPath(slot.Prefix, 'm', n, false))));
            notes.Add($"people armour: {_armour.Looks.Count} DS3 people's looks planned, {_armour.Models.Count} DS3 armour sets to convert");
            notes.AddRange(_armour.Notes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"people armour: not planned ({ex.Message}); DS3's humans are left out");
        }
        _talk = null;
        try
        {
            _talk = TalkPort.Plan(ds3, MapSlots.All.Select(s => s.Ds3));
            notes.AddRange(_talk.Notes.Select(n => "people talk: " + n));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"people talk: not planned ({ex.Message}); DS3's people stay silent");
        }
        EnemyPort.ChrMap? ported = null;
        var mapPath = ctx.ModPath(EnemyPort.ChrMap.FileName);
        if (File.Exists(mapPath))
            ported = EnemyPort.ChrMap.Parse(File.ReadAllLines(mapPath));
        report.AppendLine($"enemy catalog: {catalog.Count} Elden Ring placements ({catalog.Count(c => c.Boss)} bosses) from "
                          + $"{catalog.Select(c => c.Source).Distinct().Count()} legacy dungeons; {ported?.Count ?? 0} ported DS3 characters");
        foreach (var note in notes)
            report.AppendLine($"  note: {note}");
        if (catalog.Count == 0 && (ported?.Count ?? 0) == 0)
            throw new InvalidDataException("no Elden Ring enemy placements found to stand in for DS3's");
        return _enemies = new EnemyContext(catalog, new EnemyPort.Chooser(catalog, ported), ds3Npc, people);
    }

    private static IEnumerable<EMEVD> Ds3Scripts(GameArchives ds3, MapId ds3Id)
    {
        foreach (var path in new[] { MapEvents.ScriptPath(ds3Id), MapEvents.CommonFunc })
            if (ds3.Contains(path))
                yield return EMEVD.Read(ds3.Read(path));
    }

    // Legacy dungeons with connect collisions to copy (the colosseum slots have none).
    private static readonly string[] ConnectSources = ["m10_00_00_00", "m11_00_00_00", "m14_00_00_00", "m16_00_00_00"];
    private static MSBE.Part.ConnectCollision? _connectTemplate;
    private static bool _connectSearched;

    private static MSBE.Part.ConnectCollision? ConnectTemplate(GameArchives er)
    {
        if (_connectSearched)
            return _connectTemplate;
        _connectSearched = true;
        foreach (var name in ConnectSources)
        {
            var path = MsbPath(MapId.Parse(name));
            if (!er.Contains(path))
                continue;
            try
            {
                _connectTemplate = MSBE.Read(er.Read(path)).Parts.ConnectCollisions.FirstOrDefault();
                if (_connectTemplate is not null)
                    break;
            }
            catch
            {
                // try the next map
            }
        }
        return _connectTemplate;
    }

    /// <summary>
    /// Maps whose map pieces are tried for material templates after the slot's host. The colosseums the slots use
    /// are built from assets (m45_00 has a single map piece binder) and most legacy dungeon pieces use terrain blend
    /// materials, so several maps are tried, then the plain one-layer asset material JortPob builds its models on.
    /// </summary>
    public static readonly string[] TemplateMaps =
        ["m10_00_00_00", "m11_00_00_00", "m14_00_00_00", "m15_00_00_00", "m16_00_00_00", "m13_00_00_00", "m12_01_00_00", "m11_05_00_00"];
    public const string TemplateAsset = "/asset/aeg/aeg006/aeg006_030.geombnd.dcx";

    private static readonly Dictionary<MapId, (MapModelPort, BND4, DCX.CompressionInfo, MapId)> _templates = new();
    // The main slot each DS3 map's map pieces were converted into this run, and the textures they use.
    private static readonly Dictionary<MapId, (MapId Er, HashSet<string> Textures)> _converted = new();

    public const string LayoutsFile = "darksign_map_layouts.txt";
    private static readonly HashSet<string> _layouts = new();

    // The vanilla map pieces of the template maps, read once per run however many slots are ported.
    private static readonly Dictionary<MapId, List<FLVER2>> _templateFlvers = new();

    private static (MapModelPort Models, BND4 Binder, DCX.CompressionInfo Compression, MapId BinderMap) MaterialTemplates(
        GameArchives er, MapId hostId, MSBE host)
    {
        if (_templates.TryGetValue(hostId, out var known))
            return (known.Item1.Fresh(), known.Item2, known.Item3, known.Item4);
        var matbins = AllMatbins(er);
        var flvers = new List<FLVER2>();
        (BND4 Bnd, DCX.CompressionInfo Compression, MapId Map, FLVER2 Header)? layout = null;
        var tried = new List<string>();

        IEnumerable<(MapId Id, MSBE? Msb)> Maps()
        {
            yield return (hostId, host);
            foreach (var name in TemplateMaps)
            {
                var id = MapId.Parse(name);
                if (id != hostId && er.Contains(MsbPath(id)))
                    yield return (id, null);
            }
        }

        // The host first (its binder gives the layout), then the template maps until every kind of material has its
        // best template: the colosseums only have an albedo-only one, Stormveil has the normal-mapped, alpha-tested,
        // blended and unlit ones.
        MapModelPort? models = null;
        foreach (var (id, given) in Maps())
        {
            int count = 0;
            if (given is null && _templateFlvers.TryGetValue(id, out var cached))
            {
                flvers.AddRange(cached);
                count = cached.Count;
            }
            else
            {
                var msb = given ?? MSBE.Read(er.Read(MsbPath(id)));
                var binders = msb.Models.MapPieces.Select(m => ErMapBndPath(id, ModelNumber(m.Name))).Where(er.Contains).Distinct().Take(40).ToList();
                var read = new List<FLVER2>();
                foreach (var path in binders)
                {
                    var (bytes, compression) = er.ReadWithCompression(path);
                    var bnd = BND4.Read(bytes);
                    var found = bnd.Files.Where(f => FLVER2.Is(f.Bytes)).Select(f => FLVER2.Read(f.Bytes)).ToList();
                    if (found.Count > 0 && layout is null)
                        layout = (bnd, compression, id, found[0]);
                    read.AddRange(found);
                }
                if (given is null)
                    _templateFlvers[id] = read;
                flvers.AddRange(read);
                count = binders.Count;
            }
            tried.Add($"{id} ({count})");
            if (layout is not null && flvers.Count > 0)
            {
                try
                {
                    models = MapModelPort.Create(flvers, matbins, layout.Value.Header, plain: false);
                    if (models.Complete(false))
                        break;
                }
                catch (InvalidDataException)
                {
                    // No one-layer material yet: read the next map too.
                }
            }
        }
        if (layout is null)
            throw new InvalidDataException($"no Elden Ring map piece binder found to copy the layout from (tried {string.Join(", ", tried)})");
        if (models is null && er.Contains(TemplateAsset))
        {
            var asset = BND4.Read(er.Read(TemplateAsset)).Files.Where(f => FLVER2.Is(f.Bytes)).Select(f => FLVER2.Read(f.Bytes));
            flvers.AddRange(asset);
            tried.Add(ItemText.Leaf(TemplateAsset));
            try
            {
                models = MapModelPort.Create(flvers, matbins, layout.Value.Header, plain: false);
            }
            catch (InvalidDataException)
            {
            }
        }
        if (models is null)
            throw new InvalidDataException($"no plain one-layer material to use as a template in {string.Join(", ", tried)}");
        // DS3's water surfaces: Elden Ring's own pool water, from a map that has some (MapModelPort.WaterBinders).
        if (!models.HasWater)
            foreach (var (map, number) in MapModelPort.WaterBinders)
            {
                var path = ErMapBndPath(map, number);
                if (!er.Contains(path))
                    continue;
                try
                {
                    var water = BND4.Read(er.Read(path)).Files.Where(f => FLVER2.Is(f.Bytes)).Select(f => FLVER2.Read(f.Bytes)).ToList();
                    var withWater = MapModelPort.Create([.. flvers, .. water], matbins, layout.Value.Header, plain: false);
                    tried.Add(ItemText.Leaf(path));
                    if (withWater.HasWater)
                    {
                        flvers.AddRange(water);
                        models = withWater;
                        break;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
                {
                    tried.Add($"{ItemText.Leaf(path)} (unreadable: {ex.Message})");
                }
            }
        _templates[hostId] = (models, layout.Value.Bnd, layout.Value.Compression, layout.Value.Map);
        return (models.Fresh(), layout.Value.Bnd, layout.Value.Compression, layout.Value.Map);
    }

    public static string MsbPath(MapId id) => $"/map/mapstudio/{id}.msb.dcx";
    public static string Ds3MapBndPath(MapId id, string number) => $"/map/{id}/{id}_{number}.mapbnd.dcx";
    public static string ErMapBndPath(MapId id, string number) => $"/map/{id.AreaFolder}/{id}/{id}_{number}.mapbnd.dcx";

    // "m000100" -> "000100"; "h000100" -> "000100"
    /// <summary>
    /// The collision models (and so the parts) left out of a ported map, and whether DS3's h file for each was empty. A
    /// model stays when either binder got its collision: DS3's l collision alone is ground too (the Painted World's 28
    /// parts with an empty h file, whose enemies died on load while those parts were left out).
    /// </summary>
    public static IEnumerable<(string Model, bool Empty)> CollisionModelsLeftOut(IEnumerable<string> models,
        IReadOnlyDictionary<string, MapCollisions.Result> results)
    {
        bool Converted(string kind, string model) => results.TryGetValue(kind, out var r) && r.Ported.Contains(model);
        foreach (var name in models)
        {
            var model = ModelNumber(name);
            if (Converted("h", model) || Converted("l", model))
                continue;
            yield return (name, results.TryGetValue("h", out var h) && h.Empty?.Contains(model) == true);
        }
    }

    /// <summary>
    /// Port log lines for collision models whose DS3 file is an empty placeholder in one binder and real in the other
    /// (the empty side gets the other binder's collision): how many DS3 collision parts use them and how many DS3 enemies name
    /// one as the floor they stand on, so a log shows whether enemies stood on ground the port used to leave out.
    /// </summary>
    public static IEnumerable<string> EmptySideNotes(MSB3 ds3Msb, IReadOnlyDictionary<string, MapCollisions.Result> results)
    {
        HashSet<string> Of(string kind, Func<MapCollisions.Result, HashSet<string>?> pick) =>
            results.TryGetValue(kind, out var r) ? pick(r) ?? [] : [];
        var oneSided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        oneSided.UnionWith(Of("h", r => r.Borrowed));
        oneSided.UnionWith(Of("l", r => r.Borrowed));
        var h = Of("h", r => r.Ported);
        var l = Of("l", r => r.Ported);
        oneSided.UnionWith(h.Except(l, StringComparer.OrdinalIgnoreCase).Where(m => Of("l", r => r.Empty).Contains(m)));
        oneSided.UnionWith(l.Except(h, StringComparer.OrdinalIgnoreCase).Where(m => Of("h", r => r.Empty).Contains(m)));
        if (oneSided.Count == 0)
            yield break;
        var parts = ds3Msb.Parts.Collisions.Where(c => oneSided.Contains(ModelNumber(c.ModelName ?? ""))).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var standing = ds3Msb.Parts.Enemies.Where(e => parts.Contains(e.CollisionName ?? "")).ToList();
        yield return $"collision with an empty DS3 file on one side: {oneSided.Count} models, {parts.Count} collision parts kept, the same collision in h and l; "
                     + $"{standing.Count} DS3 enemies name one as their floor"
                     + (standing.Count == 0 ? "" : $" ({string.Join(", ", standing.Take(12).Select(e => $"{e.Name} {e.EntityID} on {e.CollisionName}"))}{(standing.Count > 12 ? ", ..." : "")})");
    }

    public static string ModelNumber(string model) => model.Length > 1 && char.IsLetter(model[0]) ? model[1..] : model;

    // "/map/m45/x.dcx" -> ["map", "m45", "x.dcx"]
    public static string[] ModParts(string archivePath) => archivePath.TrimStart('/').Split('/');

    internal static Dictionary<string, BinderFile> AllMatbins(GameArchives er)
    {
        var all = new Dictionary<string, BinderFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var binder in Explore.MaterialBinders.Where(er.Contains))
            foreach (var f in BND4.Read(er.Read(binder)).Files)
                all.TryAdd(Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)), f);
        return all;
    }

    private static string WriteMaterials(PorterContext ctx, GameArchives er, List<BinderFile> materials, bool keepExisting = false)
    {
        var (bytes, compression) = er.ReadWithCompression(MaterialBinder);
        var bnd = BND4.Read(bytes);
        // A partial (--only) run keeps the materials an earlier run added for the other maps.
        var modPath = ctx.ModPath(ModParts(MaterialBinder));
        if (keepExisting && File.Exists(modPath))
            bnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(modPath)));
        var names = bnd.Files.Select(f => ItemText.Leaf(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextId = bnd.Files.Count == 0 ? 0 : bnd.Files.Max(f => f.ID) + 1;
        var added = 0;
        foreach (var file in materials)
        {
            if (!names.Add(ItemText.Leaf(file.Name)))
            {
                // Same name as one already there (an earlier partial run's): the new one replaces it.
                if (keepExisting && bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(ItemText.Leaf(file.Name), StringComparison.OrdinalIgnoreCase)) is { } old)
                    old.Bytes = file.Bytes;
                continue;
            }
            file.ID = nextId++;
            bnd.Files.Add(file);
            added++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
        File.WriteAllBytes(modPath, DCX.Compress(bnd.Write(), compression));
        return $"materials: {added} map materials added to {ItemText.Leaf(MaterialBinder)}";
    }

    /// <summary>The randomizer's enemy tables (Randomizer EnemyTables): the DS3 enemies placed in the maps that were
    /// ported, and the Elden Ring enemies they were picked from, so the randomizer mixes real spots and characters.</summary>
    private static string WriteEnemyTables(PorterContext ctx, HashSet<string> portedMaps)
    {
        var placed = _placedEnemies.Where(r => portedMaps.Contains(r.Split('\t')[0])).ToList();
        File.WriteAllLines(ctx.ModPath(DarkSign.Randomizer.Data.EnemyTables.PlacedFile), [DarkSign.Randomizer.Data.EnemyTables.PlacedHeader, .. placed]);
        var catalog = _enemies is null ? [] : EnemyPort.CatalogRows(_enemies.Catalog).ToList();
        File.WriteAllLines(ctx.ModPath(DarkSign.Randomizer.Data.EnemyTables.ErFile), [DarkSign.Randomizer.Data.EnemyTables.ErHeader, .. catalog]);
        return $"enemy tables: {placed.Count} DS3 enemies placed, {catalog.Count} Elden Ring enemies (for the randomizer)";
    }

    /// <summary>darksign_maps.tsv: one line per ported map, read by the F1 menu's "DS3 run" tab.</summary>
    private static void WriteMapList(PorterContext ctx, IEnumerable<MapSlots.Slot> slots, IReadOnlyList<RunEvents.Warp> warps)
    {
        var lines = new List<string> { "# DS3 map\tElden Ring slot\tname\twarp request flag (written by DarkSignPorter port-maps)" };
        lines.AddRange(slots.Select((s, i) => $"{s.Ds3}\t{s.Er}\t{s.Name}\t{RunEvents.WarpRequestFlag(warps[i].Index)}"));
        File.WriteAllLines(ctx.ModPath("darksign_maps.tsv"), lines);
    }

    private static string AddRunEvents(PorterContext ctx, GameArchives ds3, GameArchives er, MapId host, uint startEntity, IReadOnlyList<RunEvents.Warp> warps,
        IReadOnlyList<BossArenas.Arena> arenas, IReadOnlyList<CutsceneMenu.Entry> cutscenes)
    {
        const string Common = "/event/common.emevd.dcx";
        var modCommon = ctx.ModPath(ModParts(Common));
        var (vanillaBytes, compression) = er.ReadWithCompression(Common);
        // Build on the mod's copy when another port step already changed it.
        var common = EMEVD.Read(File.Exists(modCommon) ? DCX.Decompress(File.ReadAllBytes(modCommon)) : vanillaBytes);
        var scripts = new List<EMEVD> { EMEVD.Read(vanillaBytes) };
        if (er.Contains("/event/m11_10_00_00.emevd.dcx"))
            scripts.Insert(0, EMEVD.Read(er.Read("/event/m11_10_00_00.emevd.dcx")));
        var back = RunEvents.FindReturnPoint(scripts)
                   ?? throw new InvalidDataException("no WarpPlayer target found in Elden Ring's event scripts to return to");
        RunEvents.AddTo(common, host, startEntity, back);
        RunEvents.AddWarps(common, warps);
        // Talk flags move the way the converted scripts moved them (darksign_flags.tsv, written by the events step).
        var flags = FlagMap.Load(ctx.ModPath(MapEvents.FlagFile));
        var defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        var slots = arenas.Select(a => (a.Ds3, a.Er)).Distinct().ToList();
        var flagPort = new EventPort(defs, defs, slots, flags);
        BossArenas.AddWarps(common, arenas, f => flagPort.Move(f, EventPort.Kind.Flag));
        CutsceneMenu.AddEvents(common, cutscenes);
        // DS3's new-game flags (Ds3Setup): DS3's common event 50 sets them; the maps' scripts read them.
        string setupNote;
        try
        {
            if (!ds3.Contains(Ds3Setup.Ds3Common))
                setupNote = "DS3 setup: DS3 has no common.emevd; DS3's new-game flags stay off";
            else
            {
                var erDefs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("er-common")));
                var maps = MapSlots.All.Where(s => !s.Probe).Select(s => (s.Ds3, s.Er)).Distinct().ToList();
                setupNote = Ds3Setup.AddTo(common, EMEVD.Read(ds3.Read(Ds3Setup.Ds3Common)), new EventPort(defs, erDefs, maps, flags));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            setupNote = $"DS3 setup: FAILED ({ex.Message}); DS3's new-game flags stay off";
        }
        flags.Save(ctx.ModPath(MapEvents.FlagFile));
        File.WriteAllBytes(modCommon, DCX.Compress(common.Write(), compression));
        return $"run events: start warps to {host} entity {startEntity}; leave warps to {back.Map} entity {back.EntityId}; "
               + $"{warps.Count} per-map warps ({string.Join(", ", warps.Select(w => $"{w.Map} flag {RunEvents.WarpRequestFlag(w.Index)}"))}); "
               + $"{arenas.Count} boss arena warps ({string.Join(", ", arenas.Select(a => $"{a.Name} flag {BossArenas.RequestFlag(a.Index)}"))}); "
               + $"{cutscenes.Count} F1 cutscene events ({string.Join(", ", cutscenes.Select(c => $"{c.ErName} {c.Label} flag {CutsceneMenu.RequestFlag(c.Index)}"))})"
               + "\n  " + setupNote;
    }
}
