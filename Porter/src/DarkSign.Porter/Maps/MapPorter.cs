using System.Text;
using SoulsFormats;

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
                    case "--binary-collision": o.BinaryCollision = true; break;
                    case "--simd": o.KeepSimdTree = true; break;
                    case "--enemies" when i + 1 < list.Count: o.Enemies = EnemyPort.ParseMethod(list[i + 1]); i++; break;
                    case "--after-pid" when i + 1 < list.Count && int.TryParse(list[i + 1], out var pid): o.AfterPid = pid; i++; break;
                    default: o.Only.Add(MapId.Parse(list[i]).ToString()); break;
                }
            return o;
        }
    }

    // Elden Ring keeps the map materials this port adds in the DLC01 binder; the weapon port uses DLC02, so the two
    // never overwrite each other's work.
    private const string MaterialBinder = "/material/allmaterial_dlc01.matbinbnd.dcx";

    public static int Run(PorterContext ctx, IEnumerable<string> args)
    {
        var options = Options.Parse(args);
        var started = DateTime.Now;
        _placedEnemies.Clear();
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var report = new StringBuilder();
        var ported = new List<(MapSlots.Slot Slot, int StartEntity)>();
        var textures = new Dictionary<string, MapTextures>(); // by Elden Ring area folder
        var materials = new List<BinderFile>();

        foreach (var slot in MapSlots.All.Where(s => options.Only.Count == 0 || options.Only.Contains(s.Ds3.ToString())))
        {
            Console.WriteLine($"map {slot.Ds3} ({slot.Name}) -> Elden Ring {slot.Er}");
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
        }
        // Missing textures or materials leave the maps grey, not broken, so a failure here doesn't stop the run.
        foreach (var areaTextures in textures.Values)
            Step(report, "textures", () => areaTextures.Write(ctx, ds3, er));
        if (materials.Count > 0)
            Step(report, "materials", () => WriteMaterials(ctx, er, materials));

        // Event scripts last: they need every ported map, to send warps and IDs to the right slot. A failure leaves
        // the empty scripts written with each map.
        WaitForWeapons(options.AfterPid);
        if (ported.Count > 0)
            Step(report, "events", () => MapEvents.Port(ctx, ds3, er, ported.Select(p => p.Slot).ToList()));

        var warps = ported.Select((p, i) => new RunEvents.Warp(p.Slot.Er, (uint)p.StartEntity, i)).ToList();
        WriteMapList(ctx, ported.Select(p => p.Slot), warps);
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
        try
        {
            report.AppendLine(step());
        }
        catch (Exception ex)
        {
            report.AppendLine($"{name}: FAILED: {ex.Message}");
            Console.Error.WriteLine($"  {name} failed: {ex.Message}");
        }
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
        var layout = MsbPort.Convert(ds3Msb, host, ds3Id, erId, new MsbPort.Options { AllVisible = !options.Ds3Groups },
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
                var placed = EnemyPort.Place(ds3Msb, layout.Msb, ds3Id, erId, new MsbPort.Options { AllVisible = !options.Ds3Groups },
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

        // Models.
        var hostPieceBinders = host.Models.MapPieces
            .Select(m => ErMapBndPath(hostId, ModelNumber(m.Name)))
            .Where(er.Contains)
            .Take(8)
            .ToList();
        if (hostPieceBinders.Count == 0)
            throw new InvalidDataException($"no map piece binder of {hostId} found to use as a template");
        var (hostBndBytes, bndCompression) = er.ReadWithCompression(hostPieceBinders[0]);
        var hostBnd = BND4.Read(hostBndBytes);
        var hostFlvers = hostPieceBinders.SelectMany(p => BND4.Read(er.Read(p)).Files)
            .Where(f => FLVER2.Is(f.Bytes)).Select(f => FLVER2.Read(f.Bytes)).ToList();
        var models = MapModelPort.Create(hostFlvers, AllMatbins(er), erId);
        report.AppendLine($"  material templates: {models.TemplateSummary}");

        var missingModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in layout.Msb.Models.MapPieces)
        {
            var number = ModelNumber(model.Name);
            var source = Ds3MapBndPath(ds3Id, number);
            try
            {
                if (!ds3.Contains(source))
                    throw new FileNotFoundException(source);
                var binder = models.ConvertBinder(BND4.Read(ds3.Read(source)), hostBnd, ds3Id, erId, number, hostId);
                File.WriteAllBytes(ctx.ModPath(ModParts(ErMapBndPath(erId, number))), DCX.Compress(binder.Write(), bndCompression));
            }
            catch (Exception ex)
            {
                missingModels.Add(model.Name);
                report.AppendLine($"  model {model.Name}: left out ({ex.Message})");
            }
        }
        foreach (var note in models.Notes.Distinct().Take(20))
            report.AppendLine($"  note: {note}");
        textures.Need(models.Textures, ds3Id);
        materials.AddRange(models.MatbinFiles(0));

        // Collision.
        var missingCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collisionModels = layout.Msb.Models.Collisions.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        // Navmesh, so enemies placed in the map can walk. A failure only leaves them standing.
        try
        {
            report.AppendLine("  " + MapNavmesh.Port(ctx, ds3, er, ds3Id, erId, options, hostId));
        }
        catch (Exception ex)
        {
            report.AppendLine($"  navmesh: FAILED: {ex.Message}");
        }

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

    private static string WriteMaterials(PorterContext ctx, GameArchives er, List<BinderFile> materials)
    {
        var (bytes, compression) = er.ReadWithCompression(MaterialBinder);
        var bnd = BND4.Read(bytes);
        var names = bnd.Files.Select(f => ItemText.Leaf(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextId = bnd.Files.Count == 0 ? 0 : bnd.Files.Max(f => f.ID) + 1;
        var added = 0;
        foreach (var file in materials)
        {
            if (!names.Add(ItemText.Leaf(file.Name)))
                continue;
            file.ID = nextId++;
            bnd.Files.Add(file);
            added++;
        }
        File.WriteAllBytes(ctx.ModPath(ModParts(MaterialBinder)), DCX.Compress(bnd.Write(), compression));
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
