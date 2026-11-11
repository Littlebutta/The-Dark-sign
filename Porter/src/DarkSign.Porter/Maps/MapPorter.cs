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
        public bool Ds3Groups { get; set; }
        public bool NoGroups { get; set; }
        /// <summary>Only the albedo-only material JortPob uses (fallback "map-materials" B).</summary>
        public bool PlainMaterials { get; set; }
        /// <summary>--no-piece-test: accepted for older launchers; every slot is built normally anyway (see <see cref="PieceTest"/>).</summary>
        public bool NoPieceTest { get; set; }
        /// <summary>--piece-test &lt;Elden Ring map id&gt;=&lt;vanilla-model|vanilla-material|ported-material|none&gt;: a slot's test, instead of the default.</summary>
        public Dictionary<string, PieceTest> PieceTests { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Map pieces name vanilla PartsDrawParam row 2046 instead of the port's own (fallback "map-draw" B).</summary>
        public bool VanillaDrawParam { get; set; }
        public bool BinaryCollision { get; set; }
        public bool KeepSimdTree { get; set; }

        /// <summary>
        /// --after-pid: the port-weapons process started alongside this one. The events step edits the regulation.bin
        /// port-weapons writes, so it waits for that process to finish; everything before it runs side by side.
        /// </summary>
        public int AfterPid { get; set; }

        /// <summary>--enemies auto|standin|none (launcher Fallbacks "map-enemies"): who stands where DS3's enemies stood.</summary>
        public EnemyPort.Method Enemies { get; set; } = EnemyPort.Method.Auto;

        public static Options Parse(IEnumerable<string> args)
        {
            var o = new Options();
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
                switch (list[i].ToLowerInvariant())
                {
                    case "--ds3-groups": o.Ds3Groups = true; break;
                    case "--no-groups": o.NoGroups = true; break;
                    case "--plain-materials": o.PlainMaterials = true; break;
                    case "--no-piece-test": o.NoPieceTest = true; break;
                    case "--piece-test" when i + 1 < list.Count && list[i + 1].Split('=') is [var map, var test]:
                        o.PieceTests[MapId.Parse(map).ToString()] = ParsePieceTest(test); i++; break;
                    case "--vanilla-draw-param": o.VanillaDrawParam = true; break;
                    case "--binary-collision": o.BinaryCollision = true; break;
                    case "--simd": o.KeepSimdTree = true; break;
                    case "--enemies" when i + 1 < list.Count: o.Enemies = EnemyPort.ParseMethod(list[i + 1]); i++; break;
                    case "--after-pid" when i + 1 < list.Count && int.TryParse(list[i + 1], out var pid): o.AfterPid = pid; i++; break;
                    default: o.Only.Add(MapId.Parse(list[i]).ToString()); break;
                }
            return o;
        }
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
        _templates.Clear();
        _converted.Clear();
        // A full run starts from Elden Ring's own files, not from what an earlier run left in Mods\ER: every PC that
        // builds from the same game files then gets byte-identical maps, events and flag numbers (co-op), and a slot
        // of the other map-slots method doesn't linger. A --only run is a partial rebuild and keeps the rest.
        if (options.Only.Count == 0)
            CleanOutputs(ctx);
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

        // Only map IDs the game ships: a registry line for any other crashed every boot (1.0.238), and an unregistered
        // ID never loads, so such a slot is reported and left out rather than built.
        var registered = MapRegistry.Registered(er);
        var slots = MapSlots.All.Where(s => options.Only.Count == 0 || options.Only.Contains(s.Ds3.ToString())).ToList();
        foreach (var slot in MapRegistry.Unregistered(registered, slots))
        {
            report.AppendLine(MapRegistry.SkipReason(slot));
            Console.Error.WriteLine("  " + MapRegistry.SkipReason(slot));
            slots.Remove(slot);
        }
        // Probe slots last: they copy their main slot's converted map pieces.
        foreach (var slot in slots.OrderBy(s => s.Probe))
        {
            Console.WriteLine($"map {slot.Ds3} ({slot.Name}) -> Elden Ring {slot.Er}");
            var mapClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!textures.TryGetValue(slot.Er.AreaFolder, out var areaTextures))
                    textures[slot.Er.AreaFolder] = areaTextures = new MapTextures(slot.Er);
                var start = PortMap(ctx, ds3, er, slot, options, areaTextures, materials, report);
                if (start > 0)
                    ported.Add((slot, start));
            }
            catch (Exception ex)
            {
                report.AppendLine($"{slot.Ds3}: FAILED: {ex.Message}");
                Console.Error.WriteLine($"  failed: {ex.Message}");
            }
            Time(report, $"map {slot.Ds3} -> {slot.Er}", mapClock);
        }
        // Missing textures or materials leave the maps grey, not broken, so a failure here doesn't stop the run.
        foreach (var areaTextures in textures.Values)
            Step(report, "textures", () => areaTextures.Write(ctx, ds3, er));
        if (materials.Count > 0)
            Step(report, "materials", () => WriteMaterials(ctx, er, materials, keepExisting: options.Only.Count > 0));

        // Event scripts last: they need every ported map, to send warps and IDs to the right slot. A failure leaves
        // the empty scripts written with each map.
        WaitForWeapons(options.AfterPid);
        // The PartsDrawParam row the map pieces name goes into the regulation port-weapons wrote. If it can't be
        // added, the pieces are pointed at a vanilla row, so they never name a row that doesn't exist.
        if (ported.Count > 0 && !options.VanillaDrawParam)
            Step(report, "map piece draw settings", () =>
            {
                var (row, note) = MapDrawParams.Write(ctx);
                if (row != MapDrawParams.FirstRow)
                    foreach (var p in ported)
                        Repoint(ctx, p.Slot.Er, row);
                return note;
            });
        if (enemyPort is not null)
            Step(report, "enemy params", () => ChrPort.ApplyParams(ctx, er, enemyPort));
        if (ported.Count > 0)
            Step(report, "events", () => MapEvents.Port(ctx, ds3, er, ported.Select(p => p.Slot).ToList()));

        // A slot under a registered ID that ships no map (map-slots B) gets what the game's own maps have: the
        // template's per-map files under the new ID, and a flag allocation line where the template has one. The map
        // registry itself is never written (see MapRegistry: a line for an unshipped ID crashed every boot).
        if (ported.Any(p => p.Slot.Host != p.Slot.Er))
        {
            Step(report, "new map ID files", () => NewIdFiles.Copy(ctx, er, ported.Select(p => p.Slot)));
            Step(report, "flag allocation", () => FlagAlloc.WriteForNewIds(ctx, er, ported.Select(p => p.Slot)));
        }

        var warps = ported.Select((p, i) => new RunEvents.Warp(p.Slot.Er, (uint)p.StartEntity, i)).ToList();
        WriteMapList(ctx, ported.Select(p => p.Slot), warps);
        // The vertex layouts the map pieces were written in, for map-validate (a mesh in any other layout isn't drawn).
        if (_layouts.Count > 0)
            File.WriteAllLines(ctx.ModPath(LayoutsFile), _layouts.Order(StringComparer.Ordinal));
        Step(report, "enemy tables", () => WriteEnemyTables(ctx, ported.Select(p => p.Slot.Ds3.ToString()).ToHashSet()));
        var startMap = ported.FirstOrDefault(p => p.Slot.Ds3 == MapId.Parse("m40_00_00_00") && !p.Slot.Probe);
        if (startMap.Slot is not null)
            Step(report, "run events", () => AddRunEvents(ctx, er, startMap.Slot.Er, (uint)startMap.StartEntity, warps));
        else
            report.AppendLine("run events: not added (Firelink Shrine / Cemetery of Ash was not ported)");

        // Read everything back and check it, so the first test in game starts from a known state.
        try
        {
            var check = MapValidate.Validate(ctx, er);
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

        var every = MapSlots.Colosseums.Concat(MapSlots.NewIdSlots()).Concat(MapSlots.LegacyNewIdSlots()).ToList();
        var ids = every.Select(s => s.Er).Distinct().ToList();
        foreach (var id in ids)
        {
            Delete(Local(MsbPath(id)));
            Delete(Local(MapEvents.ScriptPath(id)));
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
        // MapRegistry.ListPath: builds 1.0.234-1.0.238 wrote it for the m50 probe and it crashed the boot; never again.
        foreach (var path in new[] { "/event/common.emevd.dcx", MapEvents.CommonFunc, MaterialBinder, OldMaterialBinder, ActionButtons.ModMenu, FlagAlloc.LegacyList, MapRegistry.ListPath })
            Delete(Local(path));
        foreach (var file in new[] { MapEvents.FlagFile, "darksign_maps.tsv", "darksign_maps_check.txt", FlagAlloc.ProbeFile,
                     DarkSign.Randomizer.Data.EnemyTables.PlacedFile, DarkSign.Randomizer.Data.EnemyTables.ErFile })
            Delete(Path.Combine(root, file));
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
        MapTextures textures, List<BinderFile> materials, StringBuilder report)
    {
        var (ds3Id, erId) = (slot.Ds3, slot.Er);
        // A map ID Elden Ring doesn't ship takes its formats and template parts from the slot's template map.
        var hostId = er.Contains(MsbPath(erId)) ? erId : slot.Host;
        if (slot.Probe && hostId == erId)
            throw new InvalidDataException($"{erId} already exists in Elden Ring, so it can't test new map IDs");
        if (hostId != erId)
            report.AppendLine($"  {erId} is a new map ID; formats come from {hostId}");
        var ds3Msb = MSB3.Read(ds3.Read(MsbPath(ds3Id)));
        var (hostBytes, msbCompression) = er.ReadWithCompression(MsbPath(hostId));
        var host = MSBE.Read(hostBytes);

        var slots = MapSlots.All.Where(s => !s.Probe && (options.Only.Count == 0 || options.Only.Contains(s.Ds3.ToString())))
            .Select(s => (s.Ds3, s.Er)).ToList();
        var layout = MsbPort.Convert(ds3Msb, host, ds3Id, erId, new MsbPort.Options
            {
                AllVisible = !options.Ds3Groups, NoGroups = options.NoGroups,
                DrawParamId = options.VanillaDrawParam ? MapDrawParams.VanillaRow : MapDrawParams.FirstRow,
            },
            slots, ConnectTemplate(er));
        report.AppendLine($"{ds3Id} -> {erId}: {layout.MapPieces} map pieces, {layout.Collisions} collisions, {layout.Players} player points");
        foreach (var note in layout.Notes)
            report.AppendLine($"  note: {note}");

        // Enemies and bosses where DS3 had them. A failure here leaves the map empty of enemies, not broken.
        if (options.Enemies != EnemyPort.Method.None)
        {
            try
            {
                var enemies = Enemies(ctx, ds3, er, options, report);
                var bosses = EnemyPort.BossEntities(Ds3Scripts(ds3, ds3Id), ds3Layout: true);
                var placed = EnemyPort.Place(ds3Msb, layout.Msb, ds3Id, erId, new MsbPort.Options { AllVisible = !options.Ds3Groups, NoGroups = options.NoGroups },
                    enemies.Chooser, e => enemies.Describe(e, bosses), EnemyPort.AnyEnemyPart(enemies.Catalog), _placedEnemies);
                report.AppendLine($"  enemies: {placed.Placed} placed ({placed.Ported} ported DS3 characters, {placed.StandIns} Elden Ring stand-ins), "
                                  + $"{placed.Npcs} NPCs left out, {placed.Skipped} with nothing to stand in; {bosses.Count} DS3 boss entities");
                foreach (var note in placed.Notes)
                    report.AppendLine($"  enemies: {note}");
            }
            catch (Exception ex)
            {
                layout.Msb.Parts.Enemies.Clear();
                layout.Msb.Models.Enemies.Clear();
                report.AppendLine($"  enemies: FAILED ({ex.Message}); the map has no enemies");
            }
        }

        // Models. The material templates and binder layout come from vanilla map pieces (see MaterialTemplates).
        var (models, hostBnd, bndCompression, binderMap) = MaterialTemplates(er, hostId, host, options.PlainMaterials);
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

        // Every map piece binder converts in parallel (thousands per map). A probe slot copies what its main slot
        // already converted from the same DS3 map, renamed to its own map ID, instead of converting it again.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var mapPieces = layout.Msb.Models.MapPieces.ToList();
        var failures = new string?[mapPieces.Count];
        _converted.TryGetValue(ds3Id, out var main);
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
                var binder = models.ConvertBinder(BND4.Read(ds3.Read(source)), hostBnd, ds3Id, erId, number, binderMap);
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
        if (models.LeftOut.Count > 0)
            report.AppendLine($"  meshes left out (no Elden Ring material for them yet): {models.LeftOut.Values.Sum()} ("
                              + string.Join(", ", models.LeftOut.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")) + ")");
        textures.Need(models.Textures, ds3Id);
        materials.AddRange(models.MatbinFiles(0));
        if (copied > 0)
            textures.Need(main.Textures, ds3Id);
        else if (!slot.Probe && test == PieceTest.None)
            _converted[ds3Id] = (erId, models.Textures);
        report.AppendLine($"  time: {mapPieces.Count} map piece binders in {clock.Elapsed.TotalSeconds:F0} s"
                          + (copied > 0 ? $" ({copied} copied from {main.Er})" : "") + $" on {Parallelism.Threads} threads");

        // Collision.
        var missingCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collisionModels = layout.Msb.Models.Collisions.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        clock.Restart();
        foreach (var kind in new[] { "h", "l" })
        {
            var result = MapCollisions.Port(ctx, ds3, er, ds3Id, erId, kind, options, hostId);
            report.AppendLine($"  collision {kind}: {result.Summary}");
            if (kind == "h")
                foreach (var name in collisionModels.Where(n => !result.Ported.Contains(ModelNumber(n))))
                    missingCollisions.Add(name);
        }
        foreach (var name in missingCollisions)
            report.AppendLine($"  collision {name}: left out (no converted file)");
        report.AppendLine($"  time: collision {clock.Elapsed.TotalSeconds:F0} s");
        clock.Restart();

        // Navmesh, so enemies placed in the map can walk. A failure only leaves them standing.
        try
        {
            report.AppendLine("  " + MapNavmesh.Port(ctx, ds3, er, ds3Id, erId, options, hostId));
        }
        catch (Exception ex)
        {
            report.AppendLine($"  navmesh: FAILED: {ex.Message}");
        }
        report.AppendLine($"  time: navmesh {clock.Elapsed.TotalSeconds:F0} s");

        // Drop what has no model, then write the layout.
        var msb = layout.Msb;
        msb.Parts.MapPieces.RemoveAll(p => missingModels.Contains(p.ModelName));
        msb.Models.MapPieces.RemoveAll(m => missingModels.Contains(m.Name));
        msb.Parts.Collisions.RemoveAll(p => missingCollisions.Contains(p.ModelName));
        msb.Parts.ConnectCollisions.RemoveAll(p => missingCollisions.Contains(p.ModelName)
                                                   || msb.Parts.Collisions.All(c => c.Name != p.CollisionName));
        msb.Models.Collisions.RemoveAll(m => missingCollisions.Contains(m.Name));
        var keptCollisions = msb.Parts.Collisions.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var enemy in msb.Parts.Enemies.Where(e => !string.IsNullOrEmpty(e.CollisionPartName) && !keptCollisions.Contains(e.CollisionPartName)))
            enemy.CollisionPartName = "";
        File.WriteAllBytes(ctx.ModPath(ModParts(MsbPath(erId))), DCX.Compress(msb.Write(), msbCompression));
        report.AppendLine($"  written: {msb.Parts.MapPieces.Count} map pieces, {msb.Parts.Collisions.Count} collisions, {msb.Parts.Players.Count} player points, {msb.Parts.Enemies.Count} enemies");

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
    private sealed record EnemyContext(List<EnemyPort.ErEnemy> Catalog, EnemyPort.Chooser Chooser, PARAM? Ds3Npc)
    {
        public EnemyPort.Ds3Enemy Describe(MSB3.Part.Enemy e, HashSet<long> bosses)
        {
            var name = Ds3Npc?[e.NPCParamID]?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = e.ModelName;
            var npc = e.ModelName.StartsWith("c0000", StringComparison.OrdinalIgnoreCase) || e.TalkID > 0;
            return new EnemyPort.Ds3Enemy(e.ModelName, e.NPCParamID, name, bosses.Contains(e.EntityID), npc);
        }
    }

    private static EnemyContext? _enemies;
    /// <summary>darksign_enemies.tsv rows for this run: every DS3 enemy placed, for the randomizer.</summary>
    private static readonly List<string> _placedEnemies = [];

    private static EnemyContext Enemies(PorterContext ctx, GameArchives ds3, GameArchives er, Options options, StringBuilder report)
    {
        if (_enemies is not null)
            return _enemies;
        var notes = new List<string>();
        PARAM? erNpc = null, ds3Npc = null;
        try { erNpc = ctx.LoadParams(PorterGame.EldenRing).Get("NpcParam"); }
        catch (Exception ex) { notes.Add($"Elden Ring NpcParam names unavailable ({ex.Message}); stand-ins picked by model only"); }
        try { ds3Npc = ctx.LoadParams(PorterGame.DarkSouls3).Get("NpcParam"); }
        catch (Exception ex) { notes.Add($"DS3 NpcParam names unavailable ({ex.Message}); every DS3 enemy gets the most common stand-in"); }
        var catalog = EnemyPort.BuildCatalog(er, erNpc, notes);
        EnemyPort.ChrMap? ported = null;
        var mapPath = ctx.ModPath(EnemyPort.ChrMap.FileName);
        if (options.Enemies == EnemyPort.Method.Auto && File.Exists(mapPath))
            ported = EnemyPort.ChrMap.Parse(File.ReadAllLines(mapPath));
        report.AppendLine($"enemy catalog: {catalog.Count} Elden Ring placements ({catalog.Count(c => c.Boss)} bosses) from "
                          + $"{catalog.Select(c => c.Source).Distinct().Count()} legacy dungeons; {ported?.Count ?? 0} ported DS3 characters");
        foreach (var note in notes)
            report.AppendLine($"  note: {note}");
        if (catalog.Count == 0 && (ported?.Count ?? 0) == 0)
            throw new InvalidDataException("no Elden Ring enemy placements found to stand in for DS3's");
        return _enemies = new EnemyContext(catalog, new EnemyPort.Chooser(catalog, ported, options.Enemies), ds3Npc);
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
        GameArchives er, MapId hostId, MSBE host, bool plain)
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
                    models = MapModelPort.Create(flvers, matbins, layout.Value.Header, plain);
                    if (models.Complete(plain))
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
                models = MapModelPort.Create(flvers, matbins, layout.Value.Header, plain);
            }
            catch (InvalidDataException)
            {
            }
        }
        if (models is null)
            throw new InvalidDataException($"no plain one-layer material to use as a template in {string.Join(", ", tried)}");
        _templates[hostId] = (models, layout.Value.Bnd, layout.Value.Compression, layout.Value.Map);
        return (models.Fresh(), layout.Value.Bnd, layout.Value.Compression, layout.Value.Map);
    }

    public static string MsbPath(MapId id) => $"/map/mapstudio/{id}.msb.dcx";
    public static string Ds3MapBndPath(MapId id, string number) => $"/map/{id}/{id}_{number}.mapbnd.dcx";
    public static string ErMapBndPath(MapId id, string number) => $"/map/{id.AreaFolder}/{id}/{id}_{number}.mapbnd.dcx";

    // "m000100" -> "000100"; "h000100" -> "000100"
    public static string ModelNumber(string model) => model.Length > 1 && char.IsLetter(model[0]) ? model[1..] : model;

    // "/map/m45/x.dcx" -> ["map", "m45", "x.dcx"]
    public static string[] ModParts(string archivePath) => archivePath.TrimStart('/').Split('/');

    private static Dictionary<string, BinderFile> AllMatbins(GameArchives er)
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

    private static string AddRunEvents(PorterContext ctx, GameArchives er, MapId host, uint startEntity, IReadOnlyList<RunEvents.Warp> warps)
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
        RunEvents.AddTo(common, host, startEntity, back, RunEvents.AutoStart);
        RunEvents.AddWarps(common, warps);
        File.WriteAllBytes(modCommon, DCX.Compress(common.Write(), compression));
        return $"run events: start warps to {host} entity {startEntity}; leave warps to {back.Map} entity {back.EntityId}; "
               + $"{warps.Count} per-map warps ({string.Join(", ", warps.Select(w => $"{w.Map} flag {RunEvents.WarpRequestFlag(w.Index)}"))})";
    }
}
