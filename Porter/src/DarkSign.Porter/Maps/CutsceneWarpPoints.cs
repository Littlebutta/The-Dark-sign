using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Gives every "Play Cutscene to Player and Warp" (2002[11]) in the mod's scripts a warp point Elden Ring can use.
///
/// After the cutscene, 2002[11] puts the player at its third argument. In Elden Ring that entity is always a region:
/// an "Other" sphere of radius 1 named "...カットシーン後ワープ位置" ("warp position after the cutscene"; m10 10002810 and
/// 10002811, m10 10002852, m14 14002802, m19 19002810-19002813). Dark Souls III names a player part there instead
/// (c0000_2810 in High Wall, c0000_2810/2812 in Painted World), and the converted scripts and the F1 cutscene events
/// kept that: the player start part. Every ported cutscene then hung on black at the end with the game frozen and
/// Esc dead, whatever the cutscene held (offline tests 2026-11-23: the full cutscenes, one stripped to its camera,
/// and Elden Ring's own s10_00_0050 in the same slot all hung).
///
/// So for each such warp whose target is a part (not a region) of the map it names, this adds an Elden Ring style
/// region at the part's spot and facing, with a free entity ID of that map, and points the instruction at it. The
/// part keeps its ID, so the scripts' other uses of it (WarpPlayer, the bonfire spawn) are unchanged.
/// </summary>
public static class CutsceneWarpPoints
{
    private const int Bank = 2002, Id = 11;
    private const int EntityOffset = 8, MapOffset = 12;

    /// <summary>The free entity numbers tried for the new regions, in order (map base + these).</summary>
    public static IEnumerable<int> Candidates => Enumerable.Range(2950, 50).Concat(Enumerable.Range(2900, 50)).Concat(Enumerable.Range(2700, 100));

    public static MapId FromPacked(int packed) => new(packed / 1_000_000, packed / 10_000 % 100, packed / 100 % 100, packed % 100);

    /// <summary>The scripts the warps can be in: the maps' own and the common ones (the F1 cutscene events).</summary>
    public static IEnumerable<string> ScriptPaths(IEnumerable<MapId> maps) =>
        maps.Select(MapEvents.ScriptPath).Append("/event/common.emevd.dcx").Append(MapEvents.CommonFunc).Distinct();

    public sealed class Layouts
    {
        private readonly Func<MapId, MSBE?> _load;
        public readonly Dictionary<MapId, MSBE?> Loaded = [];
        public readonly HashSet<MapId> Changed = [];
        public Layouts(Func<MapId, MSBE?> load) => _load = load;
        public MSBE? Get(MapId id) => Loaded.TryGetValue(id, out var m) ? m : Loaded[id] = _load(id);
    }

    public sealed record Counts(int Warps, int AlreadyRegion, int Added, int Reused, int Parameterized, int NoLayout, int NotFound, int NoFreeId)
    {
        public string Summary =>
            $"cutscene warp points: {Warps} cutscene warps; {AlreadyRegion} already at a region, {Added} regions added and {Reused} reused for warps to a part"
            + (Parameterized > 0 ? $", {Parameterized} take their target from the caller (left as they are)" : "")
            + (NoLayout > 0 ? $", {NoLayout} name a map that wasn't ported" : "")
            + (NotFound > 0 ? $", WARN {NotFound} name an entity the map doesn't have" : "")
            + (NoFreeId > 0 ? $", WARN {NoFreeId} found no free entity ID" : "");
    }

    /// <summary>Rewrites the 2002[11] warps of <paramref name="scripts"/>. Layouts that get a region are listed in
    /// <see cref="Layouts.Changed"/>; returns the counts and whether any script changed.</summary>
    public static Counts Fix(IEnumerable<EMEVD> scripts, Layouts layouts, List<string>? notes = null)
    {
        int warps = 0, region = 0, added = 0, reused = 0, param = 0, noLayout = 0, notFound = 0, noId = 0;
        // (map, part entity) -> new region entity, so every cutscene warping to one part shares one region.
        var made = new Dictionary<(MapId, uint), uint>();
        foreach (var script in scripts)
            foreach (var e in script.Events)
                for (int i = 0; i < e.Instructions.Count; i++)
                {
                    var ins = e.Instructions[i];
                    if (ins.Bank != Bank || ins.ID != Id || ins.ArgData.Length < MapOffset + 4)
                        continue;
                    warps++;
                    if (e.Parameters.Any(p => p.InstructionIndex == i && p.TargetStartByte < MapOffset + 4 && p.TargetStartByte + p.ByteCount > EntityOffset))
                    {
                        param++;
                        continue;
                    }
                    uint entity = BitConverter.ToUInt32(ins.ArgData, EntityOffset);
                    var map = FromPacked(BitConverter.ToInt32(ins.ArgData, MapOffset));
                    var msb = layouts.Get(map);
                    if (msb is null)
                    {
                        noLayout++;
                        continue;
                    }
                    if (msb.Regions.GetEntries().Any(r => r.EntityID == entity))
                    {
                        region++;
                        continue;
                    }
                    if (!made.TryGetValue((map, entity), out var target))
                    {
                        var part = msb.Parts.GetEntries().FirstOrDefault(p => p.EntityID == entity && entity != 0);
                        if (part is null)
                        {
                            notFound++;
                            notes?.Add($"{map} event {e.ID}: cutscene warp to entity {entity}, which the map doesn't have");
                            continue;
                        }
                        var free = FreeId(msb, map);
                        if (free is null)
                        {
                            noId++;
                            continue;
                        }
                        target = free.Value;
                        AddRegion(msb, part, target);
                        layouts.Changed.Add(map);
                        made[(map, entity)] = target;
                        added++;
                        notes?.Add($"{map}: warp point {target} at {part.Name} (was entity {entity})");
                    }
                    else
                        reused++;
                    var args = (byte[])ins.ArgData.Clone();
                    BitConverter.GetBytes(target).CopyTo(args, EntityOffset);
                    e.Instructions[i] = new EMEVD.Instruction(ins.Bank, ins.ID, args);
                }
        return new Counts(warps, region, added, reused, param, noLayout, notFound, noId);
    }

    /// <summary>A free entity ID in the map's own range, or null.</summary>
    public static uint? FreeId(MSBE msb, MapId map)
    {
        var used = new HashSet<uint>();
        foreach (var p in msb.Parts.GetEntries())
        {
            used.Add(p.EntityID);
            foreach (var g in p.EntityGroupIDs)
                used.Add(g);
        }
        foreach (var r in msb.Regions.GetEntries())
            used.Add((uint)r.EntityID);
        foreach (var ev in msb.Events.GetEntries())
            used.Add(ev.EntityID);
        uint baseId = (uint)(map.Area * 1_000_000 + map.Block * 10_000);
        foreach (var n in Candidates)
            if (!used.Contains(baseId + (uint)n))
                return baseId + (uint)n;
        return null;
    }

    /// <summary>The region, as Elden Ring's own warp points are: an Other sphere of radius 1 at the part's spot.</summary>
    public static MSBE.Region.Other AddRegion(MSBE msb, MSBE.Part part, uint entity)
    {
        var names = msb.Regions.GetEntries().Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var name = $"[DS3] cutscene warp point {part.Name}";
        for (int n = 2; names.Contains(name); n++)
            name = $"[DS3] cutscene warp point {part.Name} ({n})";
        var region = new MSBE.Region.Other
        {
            Name = name,
            Shape = new MSB.Shape.Sphere { Radius = 1 },
            Position = part.Position,
            Rotation = part.Rotation,
            RegionID = msb.Regions.GetEntries().Select(r => r.RegionID).DefaultIfEmpty(-1).Max() + 1,
            MapStudioLayer = 0xFFFFFFFF,
            MapID = -1,
            UnkE08 = 255,
            UnkS04 = 0,
            UnkS0C = -1,
            EntityID = entity,
        };
        msb.Regions.Others.Add(region);
        return region;
    }

    /// <summary>Applies <see cref="Fix"/> to the mod's files: the scripts at <see cref="ScriptPaths"/> and the
    /// layouts of the maps they name. Run after every step that writes scripts (events, run events).</summary>
    public static string Apply(PorterContext ctx, IEnumerable<MapId> maps)
    {
        var mapList = maps.ToList();
        var compression = new Dictionary<MapId, DCX.CompressionInfo>();
        var layouts = new Layouts(id =>
        {
            var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(id)));
            if (!File.Exists(path))
                return null;
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var c));
            compression[id] = c;
            return msb;
        });
        var scripts = new List<(string Path, EMEVD Script, DCX.CompressionInfo Compression)>();
        foreach (var rel in ScriptPaths(mapList))
        {
            var path = ctx.ModPath(MapPorter.ModParts(rel));
            if (!File.Exists(path))
                continue;
            scripts.Add((path, EMEVD.Read(DCX.Decompress(File.ReadAllBytes(path), out var c)), c));
        }
        var before = scripts.Select(s => s.Script.Write()).ToList();
        var notes = new List<string>();
        var counts = Fix(scripts.Select(s => s.Script), layouts, notes);
        foreach (var id in layouts.Changed)
        {
            var bytes = layouts.Loaded[id]!.Write();
            MSBE.Read(bytes);
            File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(id))), DCX.Compress(bytes, compression[id]));
        }
        for (int i = 0; i < scripts.Count; i++)
        {
            var bytes = scripts[i].Script.Write();
            if (!bytes.AsSpan().SequenceEqual(before[i]))
                File.WriteAllBytes(scripts[i].Path, DCX.Compress(bytes, scripts[i].Compression));
        }
        return counts.Summary + string.Concat(notes.Take(12).Select(n => "\n  " + n));
    }
}
