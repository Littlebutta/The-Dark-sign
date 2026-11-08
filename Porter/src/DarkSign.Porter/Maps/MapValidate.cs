using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// `map-validate` (also run at the end of `port-maps`): reads back every file the map port wrote, from the mod folder,
/// and checks what the game will rely on when it loads the map. Every layout part has a model; every map piece model
/// has a binder whose FLVER reads, whose materials name a MATBIN that exists, and whose MATBINs name textures that
/// exist; every collision model has a converted file; the run start point and events are in place; and each written
/// file has the same container format (BND version, DCX type) as the vanilla file it replaces. The report ranks
/// problems as FAIL (the game will likely crash or the map won't work) and WARN (something will look or act wrong).
/// </summary>
public static class MapValidate
{
    public enum Level { Ok, Warn, Fail }

    public sealed class Report
    {
        public List<(Level Level, string Text)> Lines { get; } = [];
        public void Ok(string text) => Lines.Add((Level.Ok, text));
        public void Warn(string text) => Lines.Add((Level.Warn, text));
        public void Fail(string text) => Lines.Add((Level.Fail, text));
        public int Fails => Lines.Count(l => l.Level == Level.Fail);
        public int Warns => Lines.Count(l => l.Level == Level.Warn);

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var (level, text) in Lines)
                sb.AppendLine($"{level switch { Level.Ok => "ok  ", Level.Warn => "WARN", _ => "FAIL" }}  {text}");
            sb.AppendLine($"map-validate: {Fails} FAIL, {Warns} WARN");
            return sb.ToString();
        }
    }

    public static int Run(PorterContext ctx)
    {
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var report = Validate(ctx, er);
        Write(ctx, report);
        return report.Fails == 0 ? 0 : 1;
    }

    public static void Write(PorterContext ctx, Report report)
    {
        var text = report.ToString();
        File.WriteAllText(ctx.WorkPath("logs", $"map-validate-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), text);
        // Next to the mod, so it's easy to find and send back after a test.
        File.WriteAllText(ctx.ModPath("darksign_maps_check.txt"), text);
        Console.WriteLine(text);
    }

    public static Report Validate(PorterContext ctx, GameArchives er)
    {
        var report = new Report();
        string Mod(string archivePath) => ctx.ModPath(MapPorter.ModParts(archivePath));

        var listed = ReadMapList(ctx.ModPath("darksign_maps.tsv"));
        if (listed.Count == 0)
            report.Fail("darksign_maps.tsv lists no ported map; the F1 menu's DS3 run tab will have nothing to start");

        // Materials: vanilla names plus whatever the port added to the DLC01 binder.
        var matbins = new Dictionary<string, MATBIN>(StringComparer.OrdinalIgnoreCase);
        foreach (var binder in Explore.MaterialBinders)
        {
            var path = File.Exists(Mod(binder)) ? Mod(binder) : null;
            var bytes = path is not null ? DCX.Decompress(File.ReadAllBytes(path)) : er.Contains(binder) ? er.Read(binder) : null;
            if (bytes is null)
                continue;
            foreach (var f in BND4.Read(bytes).Files)
            {
                var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name));
                if (!matbins.ContainsKey(name))
                    try { matbins[name] = MATBIN.Read(f.Bytes); }
                    catch (Exception ex) { report.Fail($"material {name} in {ItemText.Leaf(binder)} doesn't read: {ex.Message}"); }
            }
            if (path is not null)
                CompareFormat(report, er, binder, path);
        }

        foreach (var (ds3Id, erId) in listed)
        {
            var textures = TextureNames(ctx, er, erId, report);
            ValidateMap(ctx, er, report, erId, matbins, textures);
        }
        ValidateEvents(ctx, er, report, listed.Select(l => l.Er).FirstOrDefault());
        foreach (var (_, erId) in listed)
            ValidateMapScript(ctx, er, report, erId);
        ValidateCommonFunc(ctx, er, report);
        ValidateActionButtons(ctx, report, listed.Select(l => (MapId?)l.Er).Append(null).ToList());
        try
        {
            var flags = FlagMap.Load(ctx.ModPath(MapEvents.FlagFile));
            FlagAlloc.Check(er, report, listed.Select(l => l.Er), flags.Flags.Values.Concat(flags.Events.Values));
            // The in-game check (dksign_testmenu.dll) asks the game itself about these blocks.
            File.WriteAllLines(ctx.ModPath(FlagAlloc.ProbeFile),
                FlagAlloc.ProbeLines(listed.Select(l => l.Er), flags.Flags.Values, slotFlags: !MapFlags.Common));
        }
        catch (Exception ex)
        {
            report.Warn($"flag allocation check didn't run: {ex.Message}");
        }
        return report;
    }

    private static List<(MapId Ds3, MapId Er)> ReadMapList(string path)
    {
        if (!File.Exists(path))
            return [];
        return File.ReadAllLines(path)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 2)
            .Select(p => (MapId.Parse(p[0]), MapId.Parse(p[1])))
            .ToList();
    }

    private static HashSet<string> TextureNames(PorterContext ctx, GameArchives er, MapId erId, Report report)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bhd in Enumerable.Range(0, 10).Select(n => $"/map/{erId.AreaFolder}/{erId.AreaFolder}_{n:D4}.tpfbhd")
                     .Prepend(MapTextures.CommonBinder(erId.AreaFolder)))
        {
            var mod = ctx.ModPath(MapPorter.ModParts(bhd));
            BXF4? bxf = null;
            if (File.Exists(mod))
            {
                try { bxf = BXF4.Read(mod, mod[..^3] + "bdt"); }
                catch (Exception ex) { report.Fail($"{ItemText.Leaf(bhd)} doesn't read: {ex.Message}"); }
                if (er.Contains(bhd))
                    CompareFormat(report, er, bhd, mod, split: true);
                else if (bhd != MapTextures.CommonBinder(erId.AreaFolder))
                    report.Warn($"{ItemText.Leaf(bhd)} is new (Elden Ring has no such file); if map textures are missing in game, this binder is not loaded");
            }
            else if (er.Contains(bhd))
                bxf = BXF4.Read(er.Read(bhd), er.Read(bhd[..^3] + "bdt"));
            foreach (var f in bxf?.Files ?? [])
                names.Add(MapTextures.TextureNameOf(f.Name));
        }
        return names;
    }

    public static void ValidateMap(PorterContext ctx, GameArchives er, Report report, MapId erId,
        IReadOnlyDictionary<string, MATBIN> matbins, IReadOnlySet<string> textures)
    {
        string Mod(string archivePath) => ctx.ModPath(MapPorter.ModParts(archivePath));
        var msbPath = MapPorter.MsbPath(erId);
        if (!File.Exists(Mod(msbPath)))
        {
            report.Fail($"{erId}: no layout written ({msbPath})");
            return;
        }
        MSBE msb;
        try
        {
            msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(Mod(msbPath))));
        }
        catch (Exception ex)
        {
            report.Fail($"{erId}: layout doesn't read back: {ex.Message}");
            return;
        }
        CompareFormat(report, er, msbPath, Mod(msbPath));
        CheckLayout(report, msb, erId);
        CheckEnemies(ctx, er, report, msb, erId);

        // Map pieces.
        int pieces = 0, materials = 0;
        var missingMatbins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedFormat = false;
        foreach (var model in msb.Models.MapPieces)
        {
            var path = MapPorter.ErMapBndPath(erId, MapPorter.ModelNumber(model.Name));
            if (!File.Exists(Mod(path)))
            {
                report.Fail($"{erId}: map piece model {model.Name} has no binder ({ItemText.Leaf(path)})");
                continue;
            }
            try
            {
                var bnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(Mod(path))));
                if (!checkedFormat)
                {
                    CompareWithHostBinder(report, er, path, Mod(path));
                    checkedFormat = true;
                }
                var flvers = bnd.Files.Where(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).ToList();
                if (flvers.Count == 0)
                    report.Fail($"{erId}: {ItemText.Leaf(path)} holds no .flver");
                if (!flvers.Any(f => ItemText.Leaf(f.Name).StartsWith($"{erId}_{MapPorter.ModelNumber(model.Name)}", StringComparison.OrdinalIgnoreCase)))
                    report.Warn($"{erId}: {ItemText.Leaf(path)} has no FLVER named after the model ({string.Join(", ", flvers.Select(f => ItemText.Leaf(f.Name)))})");
                foreach (var f in flvers)
                {
                    var flver = FLVER2.Read(f.Bytes);
                    foreach (var issue in CheckFlver(flver))
                        report.Fail($"{erId}: {ItemText.Leaf(f.Name)}: {issue}");
                    foreach (var m in flver.Materials)
                    {
                        materials++;
                        var name = ModelPort.MatbinName(m.MTD);
                        if (!matbins.TryGetValue(name, out var matbin))
                        {
                            missingMatbins.Add(name);
                            continue;
                        }
                        foreach (var s in matbin.Samplers)
                        {
                            var texture = Path.GetFileNameWithoutExtension(ItemText.Leaf(s.Path));
                            if (texture.Length > 0 && name.StartsWith("DKS_", StringComparison.OrdinalIgnoreCase) && !textures.Contains(texture))
                                missingTextures.Add(texture);
                        }
                    }
                }
                pieces++;
            }
            catch (Exception ex)
            {
                report.Fail($"{erId}: {ItemText.Leaf(path)} doesn't read back: {ex.Message}");
            }
        }
        report.Ok($"{erId}: {pieces} of {msb.Models.MapPieces.Count} map piece models read back ({materials} materials)");
        if (missingMatbins.Count > 0)
            report.Fail($"{erId}: {missingMatbins.Count} materials name a MATBIN that doesn't exist: {Sample(missingMatbins)}");
        if (missingTextures.Count > 0)
            report.Warn($"{erId}: {missingTextures.Count} textures used by ported materials aren't in the area's texture binders (they'll draw grey): {Sample(missingTextures)}");

        // Collision.
        foreach (var kind in new[] { "h", "l" })
        {
            var bhd = MapCollisions.ErBinderPath(erId, kind);
            if (!File.Exists(Mod(bhd)))
            {
                if (kind == "h")
                    report.Fail($"{erId}: no collision binder written ({ItemText.Leaf(bhd)}); the player will fall through the map");
                continue;
            }
            try
            {
                var bxf = BXF4.Read(Mod(bhd), Mod(bhd)[..^3] + "bdt");
                if (er.Contains(bhd))
                    CompareFormat(report, er, bhd, Mod(bhd), split: true);
                var numbers = bxf.Files.Where(f => f.Name.Contains(".hkx", StringComparison.OrdinalIgnoreCase))
                    .Select(f => MapCollisions.ModelNumberOf(ItemText.Leaf(f.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (kind == "h")
                {
                    var missing = msb.Models.Collisions.Where(m => !numbers.Contains(MapPorter.ModelNumber(m.Name))).Select(m => m.Name).ToList();
                    if (missing.Count > 0)
                        report.Fail($"{erId}: {missing.Count} collision models have no file in {ItemText.Leaf(bhd)}: {Sample(missing)}");
                }
                var wrongNames = bxf.Files.Where(f => f.Name.Contains(".hkx", StringComparison.OrdinalIgnoreCase)
                                                      && !ItemText.Leaf(f.Name).StartsWith($"{kind}{erId.Digits}_", StringComparison.OrdinalIgnoreCase)).ToList();
                if (wrongNames.Count > 0)
                    report.Warn($"{erId}: {wrongNames.Count} collision files aren't named {kind}{erId.Digits}_NNNNNN: {Sample(wrongNames.Select(f => ItemText.Leaf(f.Name)))}");
                var compendium = bxf.Files.FirstOrDefault(f => f.Name.Contains(".compendium", StringComparison.OrdinalIgnoreCase));
                var compendiumBytes = compendium is null ? null : DCX.Is(compendium.Bytes) ? DCX.Decompress(compendium.Bytes) : compendium.Bytes;
                int read = 0;
                foreach (var f in bxf.Files.Where(f => f.Name.Contains(".hkx", StringComparison.OrdinalIgnoreCase)).Take(5))
                {
                    CollisionPort.Read(DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes, compendiumBytes);
                    read++;
                }
                report.Ok($"{erId}: collision {kind}: {numbers.Count} files; {read} sampled files read back");
            }
            catch (Exception ex)
            {
                report.Fail($"{erId}: collision binder {ItemText.Leaf(bhd)} doesn't read back: {ex.Message}");
            }
        }

        // Navmesh (warning only: without it enemies stand still, the map still loads).
        var nvm = MapNavmesh.ErBinderPath(erId);
        if (!File.Exists(Mod(nvm)))
            report.Warn($"{erId}: no navmesh written; enemies placed here won't walk");
        else
            try
            {
                var bnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(Mod(nvm))));
                CompareFormat(report, er, nvm, Mod(nvm));
                report.Ok($"{erId}: navmesh binder reads back ({bnd.Files.Count(f => f.Name.Contains(".hkx", StringComparison.OrdinalIgnoreCase))} navmeshes)");
            }
            catch (Exception ex)
            {
                report.Warn($"{erId}: navmesh binder doesn't read back: {ex.Message}");
            }
    }

    /// <summary>Layout checks that need no other file: names, models, entity IDs and the run's start point.</summary>
    public static void CheckLayout(Report report, MSBE msb, MapId erId)
    {
        var parts = msb.Parts.GetEntries();
        var duplicateNames = parts.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateNames.Count > 0)
            report.Fail($"{erId}: duplicate part names: {Sample(duplicateNames)}");
        var models = msb.Models.GetEntries().Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var noModel = parts.Where(p => !models.Contains(p.ModelName)).Select(p => p.Name).ToList();
        if (noModel.Count > 0)
            report.Fail($"{erId}: parts whose model isn't in the layout's model list: {Sample(noModel)}");
        var duplicateEntities = parts.Where(p => p.EntityID != 0).GroupBy(p => p.EntityID).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();
        if (duplicateEntities.Count > 0)
            report.Warn($"{erId}: entity IDs used by more than one part: {Sample(duplicateEntities)}");
        var start = msb.Parts.Players.FirstOrDefault(p => p.EntityID == (uint)MsbPort.StartEntityId(erId));
        if (start is null)
            report.Fail($"{erId}: no player start with entity {MsbPort.StartEntityId(erId)}; the DS3 run warp has nowhere to land");
        else
        {
            report.Ok($"{erId}: run start {start.Name} at {start.Position}");
            // The start must stand over (or near) some collision, or the player falls.
            if (msb.Parts.Collisions.Count > 0)
            {
                var nearest = msb.Parts.Collisions.Min(c => Vector2Distance(c.Position, start.Position));
                if (nearest > 200)
                    report.Warn($"{erId}: the nearest collision part is {nearest:F0} m from the run start");
            }
        }
        if (msb.Parts.Collisions.Count == 0)
            report.Fail($"{erId}: the layout has no collision parts; the player will fall");
        if (msb.Parts.MapPieces.Count == 0)
            report.Fail($"{erId}: the layout has no map pieces");
        report.Ok($"{erId}: layout reads back: {msb.Parts.MapPieces.Count} map pieces, {msb.Parts.Collisions.Count} collisions, {msb.Parts.Players.Count} player points");
    }

    private static float Vector2Distance(System.Numerics.Vector3 a, System.Numerics.Vector3 b) =>
        new System.Numerics.Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>FLVER problems that crash or garble the game: indices out of range, empty buffers, broken layouts.</summary>
    public static IEnumerable<string> CheckFlver(FLVER2 flver)
    {
        for (int i = 0; i < flver.Meshes.Count; i++)
        {
            var mesh = flver.Meshes[i];
            if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count)
                yield return $"mesh {i} uses material {mesh.MaterialIndex} of {flver.Materials.Count}";
            if (mesh.NodeIndex >= flver.Nodes.Count)
                yield return $"mesh {i} uses node {mesh.NodeIndex} of {flver.Nodes.Count}";
            foreach (var b in mesh.VertexBuffers)
                if (b.LayoutIndex < 0 || b.LayoutIndex >= flver.BufferLayouts.Count)
                    yield return $"mesh {i} uses buffer layout {b.LayoutIndex} of {flver.BufferLayouts.Count}";
            foreach (var fs in mesh.FaceSets)
                if (fs.Indices.Any(x => x >= mesh.Vertices.Count && x != 0xFFFF && x != -1))
                {
                    yield return $"mesh {i} has a face index past its {mesh.Vertices.Count} vertices";
                    break;
                }
        }
        for (int i = 0; i < flver.Materials.Count; i++)
        {
            var m = flver.Materials[i];
            if (m.GXIndex >= flver.GXLists.Count)
                yield return $"material {i} uses GX list {m.GXIndex} of {flver.GXLists.Count}";
            if (string.IsNullOrEmpty(m.MTD))
                yield return $"material {i} has no MATBIN path";
        }
    }

    /// <summary>A slot's ported event script: reads back, has its constructors and keeps the link to common_func.</summary>
    public static void ValidateMapScript(PorterContext ctx, GameArchives er, Report report, MapId erId)
    {
        var archivePath = MapEvents.ScriptPath(erId);
        var path = ctx.ModPath(MapPorter.ModParts(archivePath));
        if (!File.Exists(path))
        {
            report.Warn($"{erId}: no event script written; the slot's own Elden Ring events will run");
            return;
        }
        try
        {
            var script = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(path)));
            if (script.Events.All(e => e.ID != 0))
                report.Fail($"{erId}: event script has no constructor (event 0)");
            var dupes = script.Events.GroupBy(e => e.ID).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();
            if (dupes.Count > 0)
                report.Fail($"{erId}: event script has duplicate event IDs: {Sample(dupes)}");
            var commonCalls = script.Events.SelectMany(e => e.Instructions).Count(i => i.Bank == 2000 && i.ID == 6);
            if (commonCalls > 0 && script.LinkedFileOffsets.Count == 0)
                report.Fail($"{erId}: event script starts {commonCalls} common events but doesn't link common_func");
            if (er.Contains(archivePath) && EMEVD.Read(er.Read(archivePath)).Format != script.Format)
                report.Fail($"{erId}: event script format {script.Format} differs from Elden Ring's");
            report.Ok($"{erId}: event script reads back ({script.Events.Count} events)");
        }
        catch (Exception ex)
        {
            report.Fail($"{erId}: event script doesn't read back: {ex.Message}");
        }
    }

    /// <summary>common_func.emevd with the ported DS3 common events: reads back and keeps every Elden Ring event.</summary>
    public static void ValidateCommonFunc(PorterContext ctx, GameArchives er, Report report)
    {
        var path = ctx.ModPath(MapPorter.ModParts(MapEvents.CommonFunc));
        if (!File.Exists(path))
            return;
        try
        {
            var common = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(path)));
            if (er.Contains(MapEvents.CommonFunc))
            {
                var vanilla = EMEVD.Read(er.Read(MapEvents.CommonFunc));
                var missing = vanilla.Events.Select(e => e.ID).Except(common.Events.Select(e => e.ID)).ToList();
                if (missing.Count > 0)
                    report.Fail($"common_func.emevd lost {missing.Count} of Elden Ring's own events: {Sample(missing.Select(m => m.ToString()))}");
            }
            report.Ok($"common_func.emevd: {common.Events.Count(e => FlagMap.IsEventSlot(e.ID))} ported DS3 common events");
        }
        catch (Exception ex)
        {
            report.Fail($"common_func.emevd doesn't read back: {ex.Message}");
        }
    }

    /// <summary>Every ActionButtonParam row a ported script points at is in the mod's regulation.</summary>
    public static void ValidateActionButtons(PorterContext ctx, Report report, IReadOnlyList<MapId?> maps)
    {
        var used = new HashSet<int>();
        foreach (var id in maps)
        {
            var path = ctx.ModPath(MapPorter.ModParts(id is { } m ? MapEvents.ScriptPath(m) : MapEvents.CommonFunc));
            if (!File.Exists(path))
                continue;
            try
            {
                foreach (var ins in EMEVD.Read(DCX.Decompress(File.ReadAllBytes(path))).Events.SelectMany(e => e.Instructions))
                    if (ins.Bank == 3 && ins.ID == 24 && ins.ArgData.Length >= 8 && ActionButtons.IsOurs(BitConverter.ToInt32(ins.ArgData, 4)))
                        used.Add(BitConverter.ToInt32(ins.ArgData, 4));
            }
            catch
            {
                // reported by the script checks
            }
        }
        if (used.Count == 0)
            return;
        try
        {
            var param = ActionButtons.LoadRegulation(ctx).Get("ActionButtonParam");
            var missing = used.Where(r => param?[r] is null).Select(r => r.ToString()).ToList();
            if (missing.Count > 0)
                report.Fail($"ported scripts use {missing.Count} ActionButtonParam rows the regulation doesn't have: {Sample(missing)}");
            else
                report.Ok($"action buttons: all {used.Count} ActionButtonParam rows the ported scripts use are in the regulation");
        }
        catch (Exception ex)
        {
            report.Fail($"action buttons: can't read the mod's ActionButtonParam: {ex.Message}");
        }
    }

    /// <summary>Every placed enemy's character exists (Elden Ring's or ported) and stands on a collision of the map.</summary>
    private static void CheckEnemies(PorterContext ctx, GameArchives er, Report report, MSBE msb, MapId erId)
    {
        if (msb.Parts.Enemies.Count == 0)
        {
            report.Warn($"{erId}: no enemies placed (Fallbacks \"map-enemies\" C, or nothing could stand in; see the port-maps log)");
            return;
        }
        var missing = msb.Models.Enemies.Select(m => m.Name)
            .Where(m => !er.Contains($"/chr/{m.ToLowerInvariant()}.chrbnd.dcx") && !File.Exists(ctx.ModPath("chr", $"{m.ToLowerInvariant()}.chrbnd.dcx")))
            .ToList();
        if (missing.Count > 0)
            report.Fail($"{erId}: enemies use characters with no chrbnd: {Sample(missing)}");
        var floating = msb.Parts.Enemies.Count(e => string.IsNullOrEmpty(e.CollisionPartName));
        if (floating > 0)
            report.Warn($"{erId}: {floating} enemies have no collision to stand on (they may fall)");
        report.Ok($"{erId}: {msb.Parts.Enemies.Count} enemies, {msb.Models.Enemies.Count} characters");
    }

    public static void ValidateEvents(PorterContext ctx, GameArchives er, Report report, MapId host)
    {
        const string Common = "/event/common.emevd.dcx";
        var path = ctx.ModPath(MapPorter.ModParts(Common));
        if (!File.Exists(path))
        {
            report.Fail("common.emevd was not written; the F1 menu's Start/Leave buttons will do nothing");
            return;
        }
        try
        {
            var common = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(path)));
            CompareFormat(report, er, Common, path);
            foreach (var issue in CheckRunEvents(common, host))
                report.Fail($"common.emevd: {issue}");
            if (er.Contains(Common))
            {
                var vanilla = EMEVD.Read(er.Read(Common));
                var missing = vanilla.Events.Select(e => e.ID).Except(common.Events.Select(e => e.ID)).ToList();
                if (missing.Count > 0)
                    report.Fail($"common.emevd lost {missing.Count} of Elden Ring's own events: {Sample(missing.Select(m => m.ToString()))}");
                if (vanilla.Format != common.Format)
                    report.Fail($"common.emevd format {common.Format}, Elden Ring's is {vanilla.Format}");
            }
            report.Ok("common.emevd: DS3 run events in place");
        }
        catch (Exception ex)
        {
            report.Fail($"common.emevd doesn't read back: {ex.Message}");
        }
    }

    /// <summary>The run events exist, are started by the constructor, and carry no layer masks.</summary>
    public static IEnumerable<string> CheckRunEvents(EMEVD common, MapId host)
    {
        foreach (var id in new[] { RunEvents.StartEventId, RunEvents.LeaveEventId })
        {
            var e = common.Events.FirstOrDefault(x => x.ID == id);
            if (e is null)
            {
                yield return $"event {id} is missing";
                continue;
            }
            if (e.Instructions.Any(i => i.Layer is not null))
                yield return $"event {id} has an instruction with a layer mask";
        }
        var constructor = common.Events.FirstOrDefault(e => e.ID == 0);
        foreach (var id in new[] { RunEvents.StartEventId, RunEvents.LeaveEventId })
            if (constructor is null || !constructor.Instructions.Any(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 && BitConverter.ToUInt32(i.ArgData, 4) == id))
                yield return $"event {id} is never started from the constructor (event 0)";
        var start = common.Events.FirstOrDefault(x => x.ID == RunEvents.StartEventId);
        var warp = start?.Instructions.FirstOrDefault(i => i.Bank == 2003 && i.ID == 14);
        if (warp is not null && (warp.ArgData[0] != host.Area || warp.ArgData[1] != host.Block
                                 || BitConverter.ToUInt32(warp.ArgData, 4) != (uint)MsbPort.StartEntityId(host)))
            yield return $"the start event warps to m{warp.ArgData[0]:D2}_{warp.ArgData[1]:D2} entity {BitConverter.ToUInt32(warp.ArgData, 4)}, not {host} entity {MsbPort.StartEntityId(host)}";
    }

    /// <summary>A written file's container format against the vanilla file at the same path.</summary>
    private static void CompareFormat(Report report, GameArchives er, string archivePath, string modPath, bool split = false)
    {
        if (!er.Contains(archivePath))
            return;
        try
        {
            if (split)
            {
                var a = BXF4.Read(er.Read(archivePath), er.Read(archivePath[..^3] + "bdt"));
                var b = BXF4.Read(modPath, modPath[..^3] + "bdt");
                if (a.Version != b.Version || a.Format != b.Format || a.Unicode != b.Unicode || a.Extended != b.Extended)
                    report.Fail($"{ItemText.Leaf(archivePath)}: binder header differs from Elden Ring's ({b.Format}/{b.Version} vs {a.Format}/{a.Version})");
                return;
            }
            var (vanillaBytes, vanillaCompression) = er.ReadWithCompression(archivePath);
            var modBytes = DCX.Decompress(File.ReadAllBytes(modPath), out var modCompression);
            if (vanillaCompression.Type != modCompression.Type)
                report.Fail($"{ItemText.Leaf(archivePath)}: DCX {modCompression.Type}, Elden Ring's is {vanillaCompression.Type}");
            if (BND4.Is(vanillaBytes) && BND4.Is(modBytes))
            {
                var a = BND4.Read(vanillaBytes);
                var b = BND4.Read(modBytes);
                if (a.Version != b.Version || a.Format != b.Format || a.Unicode != b.Unicode || a.Extended != b.Extended)
                    report.Fail($"{ItemText.Leaf(archivePath)}: binder header differs from Elden Ring's ({b.Format}/{b.Version} vs {a.Format}/{a.Version})");
            }
        }
        catch (Exception ex)
        {
            report.Warn($"{ItemText.Leaf(archivePath)}: couldn't compare with Elden Ring's: {ex.Message}");
        }
    }

    // Map piece binders are new files, so their format is compared with one of the host's own.
    private static void CompareWithHostBinder(Report report, GameArchives er, string archivePath, string modPath)
    {
        var area = archivePath[..(archivePath.LastIndexOf('/') + 1)];
        var vanilla = er.Entries.Where(e => e.Path is not null && e.Path.StartsWith(area, StringComparison.OrdinalIgnoreCase)
                                            && e.Path.EndsWith(".mapbnd.dcx", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path!).FirstOrDefault();
        if (vanilla is null)
        {
            report.Warn($"no vanilla map piece binder under {area} to compare with");
            return;
        }
        CompareFormat(report, er, vanilla, modPath);
    }

    private static string Sample(IEnumerable<string> items)
    {
        var list = items.ToList();
        return string.Join(", ", list.Take(8)) + (list.Count > 8 ? $", ... ({list.Count})" : "");
    }
}
