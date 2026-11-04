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

        public static Options Parse(IEnumerable<string> args)
        {
            var o = new Options();
            foreach (var a in args)
                switch (a.ToLowerInvariant())
                {
                    case "--ds3-groups": o.Ds3Groups = true; break;
                    case "--binary-collision": o.BinaryCollision = true; break;
                    case "--simd": o.KeepSimdTree = true; break;
                    default: o.Only.Add(MapId.Parse(a).ToString()); break;
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

        WriteMapList(ctx, ported.Select(p => p.Slot));
        var startMap = ported.FirstOrDefault(p => p.Slot.Ds3 == MapId.Parse("m40_00_00_00"));
        if (startMap.Slot is not null)
            report.AppendLine(AddRunEvents(ctx, er, startMap.Slot.Er, (uint)startMap.StartEntity));
        else
            report.AppendLine("run events: not added (Firelink Shrine / Cemetery of Ash was not ported)");

        var log = ctx.WorkPath("logs", $"port-maps-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(log, report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine($"port-maps: {ported.Count} map(s) ported in {(DateTime.Now - started).TotalSeconds:F0} s; report {log}");
        return ported.Count > 0 ? 0 : 1;
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
        var ds3Msb = MSB3.Read(ds3.Read(MsbPath(ds3Id)));
        var (hostBytes, msbCompression) = er.ReadWithCompression(MsbPath(erId));
        var host = MSBE.Read(hostBytes);

        var layout = MsbPort.Convert(ds3Msb, host, ds3Id, erId, new MsbPort.Options { AllVisible = !options.Ds3Groups });
        report.AppendLine($"{ds3Id} -> {erId}: {layout.MapPieces} map pieces, {layout.Collisions} collisions, {layout.Players} player points");
        foreach (var note in layout.Notes)
            report.AppendLine($"  note: {note}");

        // Models.
        var hostPieceBinders = host.Models.MapPieces
            .Select(m => ErMapBndPath(erId, ModelNumber(m.Name)))
            .Where(er.Contains)
            .Take(8)
            .ToList();
        if (hostPieceBinders.Count == 0)
            throw new InvalidDataException($"no map piece binder of {erId} found to use as a template");
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
                var binder = models.ConvertBinder(BND4.Read(ds3.Read(source)), hostBnd, ds3Id, erId, number);
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
            var result = MapCollisions.Port(ctx, ds3, er, ds3Id, erId, kind, options);
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
            report.AppendLine("  " + MapNavmesh.Port(ctx, ds3, er, ds3Id, erId, options));
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
        msb.Models.Collisions.RemoveAll(m => missingCollisions.Contains(m.Name));
        File.WriteAllBytes(ctx.ModPath(ModParts(MsbPath(erId))), DCX.Compress(msb.Write(), msbCompression));
        report.AppendLine($"  written: {msb.Parts.MapPieces.Count} map pieces, {msb.Parts.Collisions.Count} collisions, {msb.Parts.Players.Count} player points");

        // The slot's own event script would run colosseum events; replace it with an empty one.
        var eventPath = $"/event/{erId}.emevd.dcx";
        if (er.Contains(eventPath))
        {
            var vanilla = EMEVD.Read(er.Read(eventPath));
            var (_, eventCompression) = er.ReadWithCompression(eventPath);
            File.WriteAllBytes(ctx.ModPath(ModParts(eventPath)), DCX.Compress(RunEvents.EmptyMapScript(vanilla).Write(), eventCompression));
        }

        if (msb.Parts.Players.All(p => p.EntityID != (uint)layout.StartEntityId))
            return 0;
        return msb.Parts.MapPieces.Count > 0 && msb.Parts.Collisions.Count > 0 ? layout.StartEntityId : 0;
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

    /// <summary>darksign_maps.tsv: one line per ported map, read by the F1 menu's "DS3 run" tab.</summary>
    private static void WriteMapList(PorterContext ctx, IEnumerable<MapSlots.Slot> slots)
    {
        var lines = new List<string> { "# DS3 map\tElden Ring slot\tname (written by DarkSignPorter port-maps)" };
        lines.AddRange(slots.Select(s => $"{s.Ds3}\t{s.Er}\t{s.Name}"));
        File.WriteAllLines(ctx.ModPath("darksign_maps.tsv"), lines);
    }

    private static string AddRunEvents(PorterContext ctx, GameArchives er, MapId host, uint startEntity)
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
        File.WriteAllBytes(modCommon, DCX.Compress(common.Write(), compression));
        return $"run events: start warps to {host} entity {startEntity}; leave warps to {back.Map} entity {back.EntityId}";
    }
}
