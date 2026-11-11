using System.Numerics;
using SoulsFormats;
using Ds3 = HKX2;

namespace DarkSign.Porter.Maps;

/// <summary>Offline checks of the map layout, model and event steps (run by `map-selftest`).</summary>
public static partial class MapPortChecks
{
    private static readonly MapId Ds3Id = MapId.Parse("m40_00_00_00");
    private static readonly MapId ErId = MapId.Parse("m45_00_00_00");

    public static IEnumerable<(string Name, Action Test)> All()
    {
        yield return ("map ids: parse, names and entity moves", MapIds);
        yield return ("layout: DS3 map pieces, collisions and players become Elden Ring parts that write and read back", Layout);
        yield return ("layout: the run start is the DS3 arrival point (entity x990-x999) with entity 45000990", LayoutStart);
        yield return ("layout: DS3 group masks are copied by position when asked", LayoutGroups);
        yield return ("events: run events have DarkScript3's Elden Ring layouts and survive a write", RunEventLayout);
        yield return ("events: adding the run events twice leaves one copy", RunEventsIdempotent);
        yield return ("events: one warp event per ported map, started from the constructor, replaced on a rerun", PerMapWarps);
        yield return ("slots: every default slot is a map ID Elden Ring ships (no probe under an unshipped ID)", DefaultSlots);
        yield return ("events: the return point prefers a Roundtable Hold warp", ReturnPoint);
        yield return ("fallbacks: auto-start adds one start request per load outside a run, and a rerun without it removes it", AutoStartRun);
        yield return ("fallbacks: the new-map-ID slots give each DS3 map a registered file-less ID, built from a colosseum", NewIdSlots);
        yield return ("names: collision and texture binder entries", Names);
        yield return ("navmesh: DS3 Havok AI 2014 -> Elden Ring 2018 keeps faces, edges, graph and tree (XML)", () => Navmesh(xml: true));
        yield return ("navmesh: DS3 Havok AI 2014 -> Elden Ring 2018 keeps faces, edges, graph and tree (binary)", () => Navmesh(xml: false));
        yield return ("models: a DS3 map piece converts into a valid Elden Ring FLVER with its own material", ModelEndToEnd);
        foreach (var check in VisualChecks())
            yield return check;
        foreach (var check in LightingChecks())
            yield return check;
        yield return ("sky: the host's distant view (Elden Ring's sky and Erdtree) is replaced with an empty layout, --host-sky keeps it", DistantView);
        yield return ("validate: layout checks catch a missing start point and parts without models", ValidateLayout);
        yield return ("validate: run event checks pass on ported events and catch a missing one", ValidateEvents);
        yield return ("navmesh: binder entries are renamed to the host map and keep the host's compendium", NavmeshBinder);
        yield return ("rebuild: a full run deletes port-maps' own old outputs (both slot methods) and keeps the other steps' files", CleanStale);
        yield return ("flags: the allocation lists parse, blocks map to their maps, and a new map ID gets a line", FlagAllocProbe);
        yield return ("map registry: worldmsblist parses, and a slot under an ID it doesn't name is skipped, never registered", MapRegistryList);
        yield return ("new map IDs: the template's per-map files are found, renamed and the port's own outputs skipped", NewIdFileSet);
        yield return ("flags: in-game probe flags are each block's last number, and map-flags B uses JortPob's saved ranges", FlagProbesAndLocalFlags);
    }

    private static void CleanStale()
    {
        var root = Path.Combine(Path.GetTempPath(), "dks-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var ctx = new PorterContext(root);
            string[] ours = [ctx.ModPath("map", "mapstudio", "m50_00_00_00.msb.dcx"), ctx.ModPath("map", "mapstudio", "m45_02_00_99.msb.dcx"), ctx.ModPath("map", "m45", "m45_01_00_00", "h45_01_00_00.hkxbhd"),
                ctx.ModPath("map", "mapstudio", "m11_70_00_00.msb.dcx"), ctx.ModPath("map", "m11", "m11_70_00_00", "f11_70_00_00.hkxbhd"),
                ctx.ModPath("map", "worldmsblist.worldloadlistlist.dcx"), ctx.ModPath("map", "breakgeom", "lod0", "m50_00_00_00_lod0.breakgeom.dcx"),
                ctx.ModPath("map", "m45", "m45_0000.tpfbhd"), ctx.ModPath("event", "common.emevd.dcx"), ctx.ModPath("event", "common_func.emevd.dcx"),
                ctx.ModPath("event", "m51_00_00_00.emevd.dcx"), ctx.ModPath("material", "allmaterial_dlc01.matbinbnd.dcx"), ctx.ModPath("material", "allmaterial.matbinbnd.dcx"),
                ctx.ModPath("msg", "engus", "menu_dlc02.msgbnd.dcx"), ctx.ModPath("darksign_flags.tsv"), ctx.ModPath("darksign_maps.tsv")];
            string[] theirs = [ctx.ModPath("regulation.bin"), ctx.ModPath("darksign_chr_map.tsv"), ctx.ModPath("material", "allmaterial_dlc02.matbinbnd.dcx"),
                ctx.ModPath("msg", "engus", "item_dlc02.msgbnd.dcx"), ctx.ModPath("event", "m10_00_00_00.emevd.dcx")];
            foreach (var f in ours.Concat(theirs))
                File.WriteAllText(f, "x");
            MapPorter.CleanOutputs(ctx);
            Expect(ours.All(f => !File.Exists(f)), "stale port-maps output left: " + string.Join(", ", ours.Where(File.Exists)));
            Expect(theirs.All(File.Exists), "deleted another step's file: " + string.Join(", ", theirs.Where(f => !File.Exists(f))));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void FlagAllocProbe()
    {
        // The shipped lists' shape: blank lines, then "index,map,type" lines (CRLF), indices numbered across the files.
        var legacy = System.Text.Encoding.UTF8.GetBytes("\r\n\r\n0,m10_00_00_00,0\r\n1,m10_01_00_00,0\r\n72,m45_00_00_00,0\r\n73,m45_01_00_00,0");
        var open = System.Text.Encoding.UTF8.GetBytes("\r\n200,m60_41_38_00,0\r\n201,m60_39_29_00,0\r\n");
        var entries = FlagAlloc.Parse(legacy);
        Expect(entries.Count == 4 && entries[2].Index == 72 && entries[2].Map == ErId && entries[2].Rest == ",0", "parse: " + string.Join(" ", entries));
        Expect(FlagAlloc.BlockMap(45000) == ErId && FlagAlloc.BlockMap(45019) == MapId.Parse("m45_01_00_00"), "legacy block -> map");
        Expect(FlagAlloc.BlockMap(1039290) == MapId.Parse("m60_39_29_00") && FlagAlloc.BlockMap(2053460) == MapId.Parse("m61_53_46_00"),
            "tile block -> map");
        Expect(FlagAlloc.BlockMap(999) is null && FlagAlloc.BlockMap(50000) == MapId.Parse("m50_00_00_00"), "odd blocks");
        var lists = new List<(string, List<FlagAlloc.Entry>)> { ("legacy", entries), ("open", FlagAlloc.Parse(open)) };
        Expect(FlagAlloc.Find(lists, 45000)?.Entry.Index == 72 && FlagAlloc.Find(lists, 1039290)?.Entry.Index == 201
               && FlagAlloc.Find(lists, 50000) is null, "find by block");
        var probe = MapId.Parse("m50_00_00_00");
        var unlisted = System.Text.Encoding.UTF8.GetBytes("\r\n0,m10_00_00_00,0\r\n");
        Expect(FlagAlloc.WithMaps(unlisted, [open], [(probe, ErId)]).Added.Count == 0, "template not listed: no line (mirrors the colosseums)");
        var (data, added) = FlagAlloc.WithMaps(legacy, [open], [(probe, ErId), (probe, ErId), (ErId, ErId)]);
        Expect(added.Count == 1 && added[0].Index == 74 && added[0].Map == probe && added[0].Rest == ",0", "added: " + string.Join(" ", added));
        var text = System.Text.Encoding.UTF8.GetString(data);
        Expect(text.StartsWith(System.Text.Encoding.UTF8.GetString(legacy)) && text.EndsWith("73,m45_01_00_00,0\r\n74,m50_00_00_00,0\r\n"),
            "original text kept, line appended: " + text[^40..].Replace("\r\n", "|"));
        var again = FlagAlloc.Parse(data);
        Expect(again.Count == 5 && FlagAlloc.Find([("legacy", again)], 50000)?.Entry.Index == 74, "reads back");
        Expect(FlagAlloc.WithMaps(data, [open], [(probe, ErId)]).Added.Count == 0, "nothing added twice");
        var blocks = FlagAlloc.UsedBlocks([ErId, ErId], [1030290005, 1030290006, 1038291000]);
        Expect(blocks.Keys.SequenceEqual(new long[] { 45000, 1030290, 1038291, 1039290, 1039291, 1039292 }),
            "blocks: " + string.Join(",", blocks.Keys));
    }

    private static void MapRegistryList()
    {
        // The shipped list's shape (read 2026-11-08): blank lines, "map:/MapStudio/mXX.msb " lines with a trailing
        // space, ",testmap=1" on test maps, groups separated by blank lines, many blank lines at the end.
        var text = "\r\n\r\nmap:/MapStudio/m60_42_34_00.msb \r\n\r\nmap:/MapStudio/m70_51_54_00.msb,testmap=1 \r\n\r\n"
                   + "map:/MapStudio/m10_00_00_00.msb \r\nmap:/MapStudio/m45_00_00_00.msb \r\nmap:/MapStudio/m45_01_00_00.msb \r\n\r\n\r\n";
        var list = System.Text.Encoding.UTF8.GetBytes(text);
        var entries = MapRegistry.Parse(list);
        Expect(entries.Count == 5 && entries[1].Options == ",testmap=1" && entries[3].Map == ErId && entries[3].Options == "",
            "parse: " + string.Join(" ", entries));
        var registered = entries.Select(e => e.Map).ToHashSet();
        var probe = new MapSlots.Slot(Ds3Id, MapId.Parse("m50_00_00_00"), "probe", Template: ErId, Probe: true);
        var real = MapSlots.Colosseums[0];
        var skipped = MapRegistry.Unregistered(registered, [real, probe, MapSlots.Colosseums[1]]);
        Expect(skipped.SequenceEqual([probe]), "only the unshipped ID is skipped: " + string.Join(" ", skipped.Select(s => s.Er)));
        Expect(MapRegistry.SkipReason(probe).Contains("m50_00_00_00") && MapRegistry.SkipReason(probe).Contains("SKIPPED"), "skip reason names the ID");
        // Nothing in the porter writes the registry any more (the 1.0.238 boot crash).
        Expect(typeof(MapRegistry).GetMethods().All(m => !m.Name.StartsWith("Write")), "the registry must never be written");
    }

    private static void NewIdFileSet()
    {
        var template = ErId;
        var paths = new[]
        {
            "/map/m45/m45_00_00_00/f45_00_00_00.hkxbhd", "/map/m45/m45_00_00_00/f45_00_00_00.hkxbdt",
            "/map/m45/m45_00_00_00/h45_00_00_00.hkxbhd", "/map/m45/m45_00_00_00/h45_00_00_00.hkxbdt",
            "/map/m45/m45_00_00_00/l45_00_00_00.hkxbhd", "/map/m45/m45_00_00_00/m45_00_00_00.mfr.dcx",
            "/map/m45/m45_00_00_00/m45_00_00_00.nva.dcx", "/map/m45/m45_00_00_00/m45_00_00_00.nvmhktbnd.dcx",
            "/map/m45/m45_00_00_00/m45_00_00_00_0000.btl.dcx", "/map/m45/m45_00_00_00/m45_00_00_00_high.ivinfobnd.dcx",
            "/map/m45/m45_00_00_00/m45_00_00_00_000001.mapbnd.dcx", "/map/m45/common/m45_0000.tpfbhd",
            "/map/breakgeom/lod0/m45_00_00_00_lod0.breakgeom.dcx", "/map/onav/m45_00_00_00.onav.dcx",
            "/param/drawparam/m45_00_0000.gparam.dcx", "/map/mapstudio/m45_00_00_00.msb.dcx", "/map/mapstudio/m45_00_00_99.msb.dcx",
            "/event/m45_00_00_00.emevd.dcx", "/map/m45/m45_01_00_00/m45_01_00_00.nva.dcx", "/map/m10/m10_00_00_00/m10_00_00_00.nva.dcx",
        };
        var files = NewIdFiles.TemplateFiles(paths, template);
        var expected = new[]
        {
            "/map/breakgeom/lod0/m45_00_00_00_lod0.breakgeom.dcx", "/map/m45/m45_00_00_00/f45_00_00_00.hkxbhd",
            "/map/m45/m45_00_00_00/m45_00_00_00.mfr.dcx", "/map/m45/m45_00_00_00/m45_00_00_00.nva.dcx",
            "/map/m45/m45_00_00_00/m45_00_00_00_0000.btl.dcx", "/map/m45/m45_00_00_00/m45_00_00_00_high.ivinfobnd.dcx",
            "/map/onav/m45_00_00_00.onav.dcx", "/param/drawparam/m45_00_0000.gparam.dcx",
        };
        Expect(files.OrderBy(f => f, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(f => f, StringComparer.Ordinal)), "template files: " + string.Join(" ", files));
        var probe = MapId.Parse("m50_00_00_00");
        Expect(NewIdFiles.Renamed("/map/m45/m45_00_00_00/f45_00_00_00.hkxbhd", template, probe) == "/map/m50/m50_00_00_00/f50_00_00_00.hkxbhd"
               && NewIdFiles.Renamed("/param/drawparam/m45_00_0000.gparam.dcx", template, probe) == "/param/drawparam/m50_00_0000.gparam.dcx"
               && NewIdFiles.Renamed("m45_00_00_00_high.ivinfo", template, probe) == "m50_00_00_00_high.ivinfo"
               && NewIdFiles.Renamed("/map/m45/m45_00_00_00/f45_00_00_00.hkxbhd", template, MapId.Parse("m11_70_00_00")) == "/map/m11/m11_70_00_00/f11_70_00_00.hkxbhd"
               && NewIdFiles.Renamed("/param/drawparam/m45_00_0000.gparam.dcx", template, MapId.Parse("m19_70_00_00")) == "/param/drawparam/m19_70_0000.gparam.dcx", "renamed paths");
        var outputs = NewIdFiles.OutputPaths(files, template, probe).ToList();
        Expect(outputs.Count == expected.Length + 1 && outputs.Contains("/map/m50/m50_00_00_00/f50_00_00_00.hkxbdt"), "outputs include the bdt");
    }

    private static void FlagProbesAndLocalFlags()
    {
        var lines = FlagAlloc.ProbeLines([ErId], [1030290005, 1031290002]).Where(l => !l.StartsWith('#')).ToList();
        var probes = lines.Select(l => long.Parse(l.Split('\t')[0])).ToList();
        Expect(probes.All(p => p % 1000 == 999), "probe flags are a block's last number");
        Expect(probes.Contains(1039290999) && probes.Contains(1039292999) && probes.Contains(45000999) && probes.Contains(45009999)
               && probes.Contains(1030290999) && probes.Contains(1031290999), "probes: " + string.Join(",", probes));
        Expect(!probes.Contains(1039291999), "event IDs need no flag block");
        Expect(!FlagAlloc.ProbeLines([ErId], [], slotFlags: false).Any(l => l.StartsWith("4500")), "map-flags B drops the slot blocks");

        var map = new FlagMap();
        var global = map.Flag(50000);
        var local = map.LocalFlag(14000800);
        Expect(global == FlagMap.FlagSlot(0) && local == FlagMap.LocalSlot(0) && local == 1030294000, $"first local flag {local}");
        Expect(DarkSign.Core.Preflight.JortPobLayout(local) == "saved", "local flags are in a saved range");
        Expect(Enumerable.Range(0, map.LocalCapacity).Select(FlagMap.LocalSlot).All(f => DarkSign.Core.Preflight.JortPobLayout(f) == "saved"),
            "every local slot is saved");
        Expect(Enumerable.Range(0, map.LocalCapacity).Select(FlagMap.LocalSlot).Distinct().Count() == map.LocalCapacity, "local slots unique");
        Expect(!Enumerable.Range(0, map.Capacity).Select(FlagMap.FlagSlot).Intersect(Enumerable.Range(0, map.LocalCapacity).Select(FlagMap.LocalSlot)).Any(),
            "local and global slots never overlap");
        var start = map.LocalRange(14000900, 14000909);
        Expect(start > 0 && Enumerable.Range(0, 10).All(i => map.Flags[14000900 + i] == start + i), "local ranges stay consecutive");
        Expect(map.LocalFlag(14000800) == local, "the same DS3 flag keeps its number");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void MapIds()
    {
        var id = MapId.Parse("m40_00_00_00");
        Expect(id.ToString() == "m40_00_00_00" && id.Digits == "40_00_00_00" && id.AreaFolder == "m40", $"names: {id}");
        Expect(id.EntityBase == 40_000_000 && id.Ds3EntityBase == 4_000_000 && id.Ds3FlagBase == 14_000_000, $"entity base {id.EntityBase}");
        Expect(id.MoveEntity(4000800, ErId) == 45000800, "DS3 entity move (7 digits)");
        Expect(id.MoveEntity(14000800, ErId) == 45000800, "DS3 flag/event move (1AABLLLL)");
        Expect(id.MoveEntity(40001234, ErId) == 45001234, "8-digit move");
        Expect(id.MoveEntity(3000800, ErId) == 3000800 && id.MoveEntity(13000800, ErId) == 13000800, "another map's IDs must stay");
        Expect(MapId.Parse("m30_01_00_00").Ds3EntityBase == 3_010_000, "block digit");
        Expect(id.MoveEntity(0, ErId) == 0 && id.MoveEntity(-1, ErId) == -1, "unset entity must stay");
        Expect(MapSlots.ForDs3(id)?.Er == ErId, "m40 slot");
        Expect(MapPorter.ErMapBndPath(ErId, "000100") == "/map/m45/m45_00_00_00/m45_00_00_00_000100.mapbnd.dcx", "ER mapbnd path");
        Expect(MapPorter.Ds3MapBndPath(id, "000100") == "/map/m40_00_00_00/m40_00_00_00_000100.mapbnd.dcx", "DS3 mapbnd path");
        Expect(MapCollisions.ErBinderPath(ErId, "h") == "/map/m45/m45_00_00_00/h45_00_00_00.hkxbhd", "ER collision path");
        Expect(MapCollisions.Ds3BinderPath(id, "l") == "/map/m40_00_00_00/l40_00_00_00.hkxbhd", "DS3 collision path");
    }

    private static MSB3 SyntheticDs3()
    {
        var msb = new MSB3();
        msb.Models.MapPieces.Add(new MSB3.Model.MapPiece { Name = "m000100" });
        msb.Models.Collisions.Add(new MSB3.Model.Collision { Name = "h000100" });
        msb.Models.Players.Add(new MSB3.Model.Player { Name = "c0000" });
        var piece = new MSB3.Part.MapPiece { Name = "m000100_0000", ModelName = "m000100", Position = new Vector3(1, 2, 3), EntityID = 4000100 };
        piece.DrawGroups[0] = 0x11;
        piece.DispGroups[0] = 0x22;
        piece.BackreadGroups[0] = 0x33;
        msb.Parts.MapPieces.Add(piece);
        msb.Parts.Collisions.Add(new MSB3.Part.Collision { Name = "h000100_0000", ModelName = "h000100", HitFilterID = 8, Rotation = new Vector3(0, 90, 0) });
        msb.Parts.Players.Add(new MSB3.Part.Player { Name = "c0000_0000", ModelName = "c0000", EntityID = 4000500 });
        msb.Parts.Players.Add(new MSB3.Part.Player { Name = "c0000_0001", ModelName = "c0000", EntityID = 4000990, Position = new Vector3(7, 8, 9) });
        return msb;
    }

    private static MSBE SyntheticHost()
    {
        var host = new MSBE();
        host.Models.MapPieces.Add(new MSBE.Model.MapPiece { Name = "m001000", SibPath = @"N:\GR\data\Model\map\m45_00_00_00\sib\m001000.sib" });
        host.Models.Collisions.Add(new MSBE.Model.Collision { Name = "h001000", SibPath = "" });
        host.Models.Players.Add(new MSBE.Model.Player { Name = "c0000", SibPath = "" });
        host.Parts.MapPieces.Add(new MSBE.Part.MapPiece { Name = "m001000_0000", ModelName = "m001000" });
        host.Parts.Collisions.Add(new MSBE.Part.Collision { Name = "h001000_0000", ModelName = "h001000" });
        host.Parts.Players.Add(new MSBE.Part.Player { Name = "c0000_0000", ModelName = "c0000" });
        return host;
    }

    private static void Layout()
    {
        var result = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId);
        Expect(result.MapPieces == 1 && result.Collisions == 1 && result.Players == 2, $"counts {result.MapPieces}/{result.Collisions}/{result.Players}");
        var msb = MSBE.Read(result.Msb.Write());
        var piece = msb.Parts.MapPieces.Single();
        Expect(piece.Name == "m000100_0000" && piece.ModelName == "m000100" && piece.Position == new Vector3(1, 2, 3), "map piece fields");
        Expect(piece.EntityID == 45000100, $"map piece entity {piece.EntityID}");
        Expect(piece.Unk1.DisplayGroups.Concat(piece.Unk1.DrawGroups).Concat(piece.Unk1.CollisionMask).All(g => g == 0)
               && piece.Unk1.Condition1 == 0 && piece.Unk1.Condition2 == 0, "map pieces carry no group masks by default");
        Expect(piece.isUsePartsDrawParamID == 1 && piece.PartsDrawParamID == MapDrawParams.VanillaRow, "map pieces name a PartsDrawParam row");
        var collision = msb.Parts.Collisions.Single();
        Expect((int)collision.HitFilterID == 8 && collision.Rotation == new Vector3(0, 90, 0), "collision fields");
        Expect(msb.Models.MapPieces.Single().SibPath.Contains("m000100") && !msb.Models.MapPieces.Single().SibPath.Contains("m001000"),
            $"sib path {msb.Models.MapPieces.Single().SibPath}");
    }

    private static void LayoutStart()
    {
        var result = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId);
        Expect(result.StartEntityId == 45000990, $"start entity {result.StartEntityId}");
        var start = result.Msb.Parts.Players.Single(p => p.EntityID == 45000990);
        Expect(start.Position == new Vector3(7, 8, 9) && start.Name == "c0000_9990", $"start {start.Name} {start.Position}");
        Expect(result.Msb.Parts.Players.Any(p => p.EntityID == 45000500), "other player point keeps its moved entity");
    }

    private static void LayoutGroups()
    {
        var result = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId, new MsbPort.Options { AllVisible = false });
        var unk1 = result.Msb.Parts.MapPieces.Single().Unk1;
        Expect(unk1.DisplayGroups[0] == 0x11 && unk1.DrawGroups[0] == 0x22 && unk1.CollisionMask[0] == 0x33,
            $"groups {unk1.DisplayGroups[0]:X} {unk1.DrawGroups[0]:X} {unk1.CollisionMask[0]:X}");
    }

    private static EMEVD SyntheticCommon()
    {
        var common = new EMEVD(EMEVD.Game.Sekiro);
        var constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
        constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, 1234u, 0u));
        common.Events.Add(constructor);
        return common;
    }

    private static readonly RunEvents.ReturnPoint Back = new(MapId.Parse("m11_10_00_00"), 11102020);

    private static void RunEventLayout()
    {
        var common = SyntheticCommon();
        RunEvents.AddTo(common, ErId, 45000990, Back);
        common = EMEVD.Read(common.Write());
        var start = common.Events.Single(e => e.ID == RunEvents.StartEventId);
        Expect(start.Instructions.Count == 6, $"start has {start.Instructions.Count} instructions");
        var wait = start.Instructions[0];
        Expect(wait.Bank == 1003 && wait.ID == 0 && wait.ArgData.Length == 8 && wait.ArgData[0] == 1
               && BitConverter.ToUInt32(wait.ArgData, 4) == RunEvents.StartDs3Request, "WaitForEventFlag layout (u8 state, u8 type, u32 flag)");
        var clear = start.Instructions[1];
        Expect(clear.Bank == 2003 && clear.ID == 66 && BitConverter.ToUInt32(clear.ArgData, 4) == RunEvents.StartDs3Request
               && clear.ArgData[8] == 0, "SetEventFlag layout (u8 type, u32 flag, u8 state)");
        var warp = start.Instructions[3];
        Expect(warp.Bank == 2003 && warp.ID == 14 && warp.ArgData[0] == 45 && warp.ArgData[1] == 0
               && BitConverter.ToUInt32(warp.ArgData, 4) == 45000990 && BitConverter.ToInt32(warp.ArgData, 8) == 0,
            "WarpPlayer layout (4 x u8 map, u32 entity, s32 popup)");
        var end = start.Instructions[5];
        Expect(end.Bank == 1000 && end.ID == 4 && end.ArgData[0] == 1, "EndUnconditionally(Restart)");
        Expect(start.Instructions.All(i => i.Layer is null), "no instruction may carry a layer mask");
        var leave = common.Events.Single(e => e.ID == RunEvents.LeaveEventId);
        Expect(leave.Instructions[3].ArgData[0] == 11 && leave.Instructions[3].ArgData[1] == 10
               && BitConverter.ToUInt32(leave.Instructions[3].ArgData, 4) == 11102020, "leave warps to the return point");
        var inits = common.Events.Single(e => e.ID == 0).Instructions.Where(i => i.Bank == 2000 && i.ID == 0).ToList();
        Expect(inits.Count == 3 && inits.Any(i => BitConverter.ToUInt32(i.ArgData, 4) == 1234), "constructor keeps its own events and starts ours");
    }

    private static void RunEventsIdempotent()
    {
        var common = SyntheticCommon();
        RunEvents.AddTo(common, ErId, 45000990, Back);
        RunEvents.AddTo(common, ErId, 45000990, Back);
        Expect(common.Events.Count(e => e.ID == RunEvents.StartEventId) == 1, "start event duplicated");
        Expect(common.Events.Single(e => e.ID == 0).Instructions.Count == 3, "constructor inits duplicated");
    }

    private static void ReturnPoint()
    {
        EMEVD Script(params (byte Area, byte Block, uint Entity)[] warps)
        {
            var s = new EMEVD(EMEVD.Game.Sekiro);
            var e = new EMEVD.Event(100, EMEVD.Event.RestBehaviorType.Default);
            foreach (var (area, block, entity) in warps)
                e.Instructions.Add(RunEvents.Make(2003, 14, area, block, (byte)0, (byte)0, entity, -1));
            s.Events.Add(e);
            return s;
        }
        var point = RunEvents.FindReturnPoint([Script((0, 0, 0), (60, 42, 1042360000)), Script((11, 10, 11102020))]);
        Expect(point?.Map.Area == 11 && point.EntityId == 11102020, $"return point {point}");
        var fallback = RunEvents.FindReturnPoint([Script((60, 42, 1042360000))]);
        Expect(fallback?.EntityId == 1042360000, "fallback to any warp");
    }

    private static void Names()
    {
        Expect(MapCollisions.ModelNumberOf("h40_00_00_00_000100.hkx.dcx") == "000100", "collision model number");
        Expect(MapPorter.ModelNumber("h000100") == "000100" && MapPorter.ModelNumber("m000100") == "000100", "msb model number");
        Expect(MapTextures.TextureNameOf(@"N:\x\m40_00_wall_a.tpf.dcx") == "m40_00_wall_a", "texture name");
        Expect(MapPorter.ModParts("/map/m45/x.tpfbhd").SequenceEqual(["map", "m45", "x.tpfbhd"]), "mod parts");
    }

    internal static byte[] SyntheticDs3Navmesh()
    {
        Ds3.hkaiNavMeshEdge Edge(int a, int b, uint oppositeEdge = 0xFFFFFFFF, uint oppositeFace = 0xFFFFFFFF) => new()
        {
            m_a = a, m_b = b, m_oppositeEdge = oppositeEdge, m_oppositeFace = oppositeFace, m_flags = 0,
            m_userEdgeCost = (short)(BitConverter.SingleToInt32Bits(1.5f) >> 16),
        };
        var mesh = new Ds3.hkaiNavMesh
        {
            m_faces =
            [
                new Ds3.hkaiNavMeshFace { m_startEdgeIndex = 0, m_startUserEdgeIndex = -1, m_numEdges = 3, m_clusterIndex = 0 },
                new Ds3.hkaiNavMeshFace { m_startEdgeIndex = 3, m_startUserEdgeIndex = -1, m_numEdges = 3, m_clusterIndex = 0 },
            ],
            m_edges = [Edge(0, 1), Edge(1, 2, 3, 1), Edge(2, 0), Edge(2, 1, 1, 0), Edge(1, 3), Edge(3, 2)],
            m_vertices = [new(0, 0, 0, 0), new(10, 0, 0, 0), new(0, 0, 10, 0), new(10, 0, 10, 0)],
            m_streamingSets = [],
            m_faceData = [7, 7],
            m_edgeData = [],
            m_faceDataStriding = 1,
            m_edgeDataStriding = 0,
            m_aabb = new Ds3.hkAabb { m_min = new(0, 0, 0, 0), m_max = new(10, 0, 10, 0) },
            m_erosionRadius = 0.3f,
        };
        var graph = new Ds3.hkaiDirectedGraphExplicitCost
        {
            m_positions = [new(5, 0, 5, 0)],
            m_nodes = [new Ds3.hkaiDirectedGraphExplicitCostNode { m_startEdgeIndex = 0, m_numEdges = 0 }],
            m_edges = [],
            m_nodeData = [],
            m_edgeData = [],
            m_streamingSets = [],
        };
        var tree = new Ds3.hkcdStaticTreeDefaultTreeStorage6
        {
            m_domain = new Ds3.hkAabb { m_min = new(0, 0, 0, 0), m_max = new(10, 0, 10, 0) },
            m_nodes =
            [
                new Ds3.hkcdStaticTreeCodec3Axis6 { m_xyz_0 = 1, m_xyz_1 = 2, m_xyz_2 = 3, m_hiData = 0x80, m_loData = 1 },
                new Ds3.hkcdStaticTreeCodec3Axis6 { m_xyz_0 = 4, m_xyz_1 = 5, m_xyz_2 = 6, m_hiData = 0, m_loData = 0 },
                new Ds3.hkcdStaticTreeCodec3Axis6 { m_xyz_0 = 7, m_xyz_1 = 8, m_xyz_2 = 9, m_hiData = 0, m_loData = 1 },
            ],
        };
        var root = new Ds3.hkRootLevelContainer
        {
            m_namedVariants =
            [
                new Ds3.hkRootLevelContainerNamedVariant { m_name = "hkaiNavMesh", m_className = "hkaiNavMesh", m_variant = mesh },
                new Ds3.hkRootLevelContainerNamedVariant { m_name = "hkaiDirectedGraphExplicitCost", m_className = "hkaiDirectedGraphExplicitCost", m_variant = graph },
                new Ds3.hkRootLevelContainerNamedVariant
                {
                    m_name = "hkaiStaticTreeNavMeshQueryMediator", m_className = "hkaiStaticTreeNavMeshQueryMediator",
                    m_variant = new Ds3.hkaiStaticTreeNavMeshQueryMediator { m_navMesh = mesh, m_tree = new Ds3.hkcdStaticAabbTree { m_treePtr = tree } },
                },
            ],
        };
        using var stream = new MemoryStream();
        var bw = new BinaryWriterEx(false, stream);
        new Ds3.PackFileSerializer().Serialize(root, bw);
        bw.Finish();
        return stream.ToArray();
    }

    private static void Navmesh(bool xml)
    {
        var result = NavmeshPort.Convert(SyntheticDs3Navmesh(), xml);
        Expect(result.Faces == 2 && result.Edges == 6 && result.Vertices == 4 && result.GraphNodes == 1,
            $"counts {result.Faces}/{result.Edges}/{result.Vertices}/{result.GraphNodes}");
        var root = CollisionPort.Read(result.Bytes);
        var mesh = root.m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaiNavMesh>().Single();
        Expect(Math.Abs(mesh.m_edges[0].m_userEdgeCost - 1.5f) < 1e-3f, $"edge cost {mesh.m_edges[0].m_userEdgeCost}");
        Expect(mesh.m_edges[1].m_oppositeEdge == 3 && mesh.m_edges[1].m_oppositeFace == 1, "edge links");
        Expect(mesh.m_vertices[3] == new Vector4(10, 0, 10, 0) && mesh.m_faceData.SequenceEqual([7, 7]), "vertices or face data");
        var graph = root.m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaiDirectedGraphExplicitCost>().Single();
        Expect(graph.m_positions.Count == 1 && graph.m_positions[0] == new Vector4(5, 0, 5, 0), "graph positions");
        var mediator = root.m_namedVariants.Select(v => v.m_variant).OfType<HKLib.hk2018.hkaiStaticTreeNavMeshQueryMediator>().Single();
        var nodes = mediator.m_tree!.m_treePtr!.m_tree.m_nodes;
        Expect(nodes.Count == 3 && nodes[0].m_xyz[2] == 3 && nodes[0].m_hiData == 0x80 && nodes[2].m_loData == 1, "tree nodes");
        Expect(ReferenceEquals(mediator.m_navMesh, mesh) || mediator.m_navMesh?.m_faces.Count == 2, "mediator mesh");
    }

    private static void NavmeshBinder()
    {
        var ds3 = new BND4();
        ds3.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\FDP\data\Model\map\m40_00_00_00\navimesh\n40_00_00_00_000001.hkx", SyntheticDs3Navmesh()));
        var host = new BND4();
        host.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\GR\data\INTERROOT_win64\map\m45_00_00_00\navimesh\n45_00_00_00_000100.hkx", [1, 2, 3]));
        host.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 1, @"N:\GR\data\INTERROOT_win64\map\m45_00_00_00\navimesh\n45_00_00_00.compendium", [4, 5, 6]));
        var result = MapNavmesh.Convert(ds3, host, Ds3Id, ErId, xml: true);
        Expect(result.Converted == 1, result.Summary);
        var names = result.Binder.Files.Select(f => f.Name).ToList();
        Expect(names.Any(n => n.EndsWith(@"\n45_00_00_00_000001.hkx") && n.Contains("INTERROOT_win64")), string.Join(", ", names));
        Expect(names.Any(n => n.EndsWith(".compendium")) && !names.Any(n => n.EndsWith("000100.hkx")), "compendium kept, host navmesh left out");
    }

    private static FLVER2 SyntheticFlver(int version, string mtd, params (string Param, string Path)[] textures)
    {
        var flver = new FLVER2();
        flver.Header.Version = version;
        flver.Header.Unicode = true;
        flver.Header.BoundingBoxMin = new Vector3(0, 0, 0);
        flver.Header.BoundingBoxMax = new Vector3(10, 1, 10);
        flver.Nodes.Add(new FLVER.Node { Name = "root", ParentIndex = -1, FirstChildIndex = -1, NextSiblingIndex = -1, PreviousSiblingIndex = -1 });
        var material = new FLVER2.Material("mat", mtd, 0) { GXIndex = 0 };
        foreach (var (param, path) in textures)
            material.Textures.Add(new FLVER2.Texture(param, path, Vector2.One, 0, 0, 0, 0, 0));
        flver.Materials.Add(material);
        var gx = new FLVER2.GXList();
        gx.Add(new FLVER2.GXItem { ID = "GXMD", Unk04 = 162, Data = new byte[4] });
        flver.GXLists.Add(gx);
        // Elden Ring: the layout of the colosseum's map piece (m45_00_00_00_009999). DS3: two tangents, float UVs.
        bool er = version >= 0x2001A;
        var layout = er
            ? new FLVER2.BufferLayout
            {
                new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position),
                new FLVER.LayoutMember(FLVER.LayoutType.UByte4, FLVER.LayoutSemantic.Normal),
                new FLVER.LayoutMember(FLVER.LayoutType.UByte4, FLVER.LayoutSemantic.Tangent),
                new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor, 1),
                new FLVER.LayoutMember(FLVER.LayoutType.Short2, FLVER.LayoutSemantic.UV),
            }
            : new FLVER2.BufferLayout
            {
                new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position),
                new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Normal),
                new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Tangent),
                new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.Tangent, 1),
                new FLVER.LayoutMember(FLVER.LayoutType.Float2, FLVER.LayoutSemantic.UV),
            };
        flver.BufferLayouts.Add(layout);
        var mesh = new FLVER2.Mesh { MaterialIndex = 0, NodeIndex = 0 };
        foreach (var p in new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 0, 10) })
        {
            var v = new FLVER.Vertex();
            v.Positions.Add(p);
            v.Normals.Add(Vector3.UnitY);
            v.NormalWs.Add(127);
            v.Tangents.Add(new Vector4(1, 0, 0, 1));
            if (er)
                v.Colors.Add(new FLVER.VertexColor(1f, 1f, 1f, 1f));
            else
                v.Tangents.Add(new Vector4(1, 0, 0, 1));
            v.UVs.Add(new Vector3(p.X / 10, p.Z / 10, 0));
            mesh.Vertices.Add(v);
        }
        mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
        mesh.FaceSets.Add(new FLVER2.FaceSet(FLVER2.FaceSet.FSFlags.None, false, true, 0, [0, 1, 2]));
        flver.Meshes.Add(mesh);
        if (version >= 0x20017)
            flver.Skeletons = new FLVER2.SkeletonSet
            {
                BaseSkeleton = [new FLVER2.SkeletonSet.Bone(0) { ParentIndex = -1, FirstChildIndex = -1, NextSiblingIndex = -1, PreviousSiblingIndex = -1 }],
                AllSkeletons = [new FLVER2.SkeletonSet.Bone(0) { ParentIndex = -1, FirstChildIndex = -1, NextSiblingIndex = -1, PreviousSiblingIndex = -1 }],
            };
        return FLVER2.Read(flver.Write());
    }

    private static MATBIN SyntheticMatbin(string name)
    {
        var matbin = new MATBIN { ShaderPath = @"N:\GR\data\Material\mtd\M[ARSN].spx", SourcePath = $@"N:\GR\data\Material\matxml\{name}.matxml" };
        matbin.Samplers.Add(new MATBIN.Sampler { Type = "C_DetailBlend__snp_Texture2D_0_AlbedoMap_0", Path = @"N:\GR\data\Model\map\m45\tex\m45_00_wall_a.tif" });
        matbin.Samplers.Add(new MATBIN.Sampler { Type = "C_DetailBlend__snp_Texture2D_1_NormalMap_0", Path = @"N:\GR\data\Model\map\m45\tex\m45_00_wall_n.tif" });
        matbin.Samplers.Add(new MATBIN.Sampler { Type = "C_DetailBlend__snp_Texture2D_2_MetallicMap_0", Path = @"N:\GR\data\Model\map\m45\tex\m45_00_wall_m.tif" });
        return MATBIN.Read(matbin.Write());
    }

    private static void ModelEndToEnd()
    {
        const string Template = "M_test_opaque";
        var host = SyntheticFlver(0x2001A, $@"N:\GR\data\Material\matxml\{Template}.matxml");
        var matbins = new Dictionary<string, BinderFile>
        {
            [Template] = new(Binder.FileFlags.Flag1, 0, $@"N:\GR\data\INTERROOT_win64\material\matbin\Map_m45\{Template}.matbin", SyntheticMatbin(Template).Write()),
        };
        // A terrain blend material first (most of Elden Ring's legacy dungeon pieces): skipped for the plain one.
        var blend = SyntheticFlver(0x2001A, @"N:\GR\data\Material\mtd\Map_m10_00\matxml\m10_00_027.matxml");
        var blendMatbin = new MATBIN { ShaderPath = @"N:\GR\data\Material\sat\OutputData\SPX\M[AMSN_V][Mb2][Ov_N].spx", SourcePath = @"N:\GR\data\Material\mtd\Map_m10_00\matxml\m10_00_027.matxml" };
        blendMatbin.Samplers.Add(new MATBIN.Sampler { Type = "M_AMSN_V_Mb2_Ov_N__snp_Texture2D_2_GSBlendMap_AlbedoMap_1", Path = @"N:\GR\data\Asset\Environment\texture\AET210\AET210_030_a.tif" });
        blendMatbin.Samplers.Add(new MATBIN.Sampler { Type = "M_AMSN_V_Mb2_Ov_N__snp_Texture2D_5_GSBlendMap_NormalMap_0", Path = @"N:\GR\data\Asset\Environment\texture\AET210\AET210_896_n.tif" });
        matbins["m10_00_027"] = new(Binder.FileFlags.Flag1, 1, @"N:\GR\data\INTERROOT_win64\material\matbin\Map_m10_00\matxml\m10_00_027.matbin", MATBIN.Read(blendMatbin.Write()).Write());
        Expect(MapModelPort.Classify(MATBIN.Read(matbins["m10_00_027"].Bytes)) is null, "a terrain blend material is not a template");
        var port = MapModelPort.Create([blend, host], matbins, blend);
        Expect(port.TemplateSummary.Contains(Template) && !port.TemplateSummary.Contains("m10_00_027"), $"templates {port.TemplateSummary}");
        var ds3Flver = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN].mtd",
            ("g_DiffuseTexture", @"N:\FDP\data\Model\map\m40\tex\m40_00_stone_a.tga"),
            ("g_BumpmapTexture", @"N:\FDP\data\Model\map\m40\tex\m40_00_stone_n.tga"));
        // DS3 map pieces point meshes at nodes the file doesn't hold (a Maya node index) and carry NormalW 127;
        // Elden Ring map pieces sit on node 0 with NormalW 0 (JortPob).
        ds3Flver.Meshes[0].NodeIndex = 3;
        ds3Flver.Meshes[0].BoneIndices = [3];
        ds3Flver = FLVER2.Read(ds3Flver.Write());
        Expect(ds3Flver.Meshes[0].NodeIndex == 3 && ds3Flver.Meshes[0].Vertices.All(v => v.NormalW == 127), "DS3 test model keeps its bad node index");
        var ds3Bnd = new BND4();
        ds3Bnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\FDP\data\Model\map\m40_00_00_00\sib\m40_00_00_00_000100.flver", ds3Flver.Write()));
        var hostBnd = new BND4();
        hostBnd.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\GR\data\Model\map\m45_00_00_00\m45_00_00_00_001000\sib\m45_00_00_00_001000.flver", host.Write()));

        var binder = BND4.Read(port.ConvertBinder(ds3Bnd, hostBnd, Ds3Id, ErId, "000100").Write());
        var file = binder.Files.Single();
        Expect(ItemText.Leaf(file.Name) == "m45_00_00_00_000100.flver" && file.Name.Contains(@"m45_00_00_00_000100\sib"), $"binder entry {file.Name}");
        var flver = FLVER2.Read(file.Bytes);
        var issues = MapValidate.CheckFlver(flver).ToList();
        Expect(issues.Count == 0, string.Join("; ", issues));
        Expect(flver.Header.Version == 0x2001A, $"FLVER version {flver.Header.Version:X}");
        Expect(flver.Header.BoundingBoxMax == new Vector3(10, 1, 10), "bounding box from DS3");
        Expect(host.Header.BoundingBoxMax == new Vector3(10, 1, 10) && !ReferenceEquals(host.Header, flver.Header), "template header must not be shared");
        var material = flver.Materials.Single();
        Expect(material.Name.StartsWith("DKS_40_00_00_00_") && ModelPort.MatbinName(material.MTD) == material.Name, $"material {material.Name} / {material.MTD}");
        Expect(flver.Meshes[0].Vertices.All(v => v.Tangents.Count == 1 && v.Colors.Count == 1), "one tangent and a vertex color per vertex");
        Expect(flver.Meshes[0].NodeIndex == 0 && flver.Meshes[0].BoneIndices.Count == 0 && flver.Nodes.Count == 1, "every mesh on the root node, no per-mesh bone list");
        Expect(flver.Meshes[0].Vertices.All(v => v.NormalWs.All(w => w == 0)), "NormalW 0 on a static mesh, as JortPob writes it");
        var noNodes = SyntheticFlver(0x20014, @"N:\FDP\data\Material\mtd\map\M[ARSN].mtd");
        noNodes.Nodes.Clear();
        MapModelPort.MakeStatic(noNodes);
        Expect(noNodes.Nodes.Count == 1 && noNodes.Meshes[0].NodeIndex == 0, "a model without nodes gets a root");
        Expect(port.Textures.SetEquals(["m40_00_stone_a", "m40_00_stone_n"]), $"textures {string.Join(",", port.Textures)}");

        var newMatbin = port.MatbinFiles(100).Single();
        Expect(Path.GetFileNameWithoutExtension(ItemText.Leaf(newMatbin.Name)) == material.Name, $"matbin file {newMatbin.Name}");
        var samplers = MATBIN.Read(newMatbin.Bytes).Samplers.Select(s => s.Path).ToList();
        Expect(samplers.SequenceEqual(["m40_00_stone_a", "m40_00_stone_n", MapModelPort.BlackMetallic]), $"samplers {string.Join(",", samplers)} (bare texture names)");
        Expect(port.Fresh().Textures.Count == 0 && port.Fresh().TemplateSummary == port.TemplateSummary, "a fresh port keeps the templates, not the materials");

        // Elden Ring's sampler names, from real MATBINs.
        Expect(MapModelPort.Role("M_A__snp_Texture2D_0_AlbedoMap_0") == ("albedo", 0), "plain albedo");
        Expect(MapModelPort.Role("M_AMSN_V__Grass_snp_Texture2D_1_NormalMap_0") == ("normal", 0), "plain normal");
        Expect(MapModelPort.Role("M_AMSN_V_Mb3_Ov_N__snp_Texture2D_3_GSBlendMap_AlbedoMap_2") == ("blend", 2), "blend layer");
        Expect(MapModelPort.Role("M_Water_ocean4__snp_Texture2D_3_Mask1Map") is null, "mask");
        var assetOnly = new MATBIN { ShaderPath = @"N:\GR\data\Material\sat\OutputData\SPX\M[A].spx", SourcePath = @"N:\GR\data\Material\mtd\Map_m60_00\matxml\AEG006_030_ID001.matxml" };
        assetOnly.Samplers.Add(new MATBIN.Sampler { Type = "M_A__snp_Texture2D_0_AlbedoMap_0", Path = @"N:\GR\data\Other\SysTex\SYSTEX_Dummy.tif" });
        Expect(MapModelPort.Classify(assetOnly) == "opaque", "JortPob's albedo-only asset material is an opaque template");

        // The texture binder, laid out like JortPob's.
        // (Deflate here: Kraken needs the game's own Oodle library, which the self test doesn't have.)
        var bxf = MapTextures.BuildBinder([new TPF.Texture("m40_00_stone_a", 103, 0, ModelPort.BlackBc4Dds(), TPF.TPFPlatform.PC)],
            new DCX.DcxDfltCompressionInfo(DCX.DfltCompressionPreset.DCX_DFLT_11000_44_9));
        Expect(bxf.Version == "07D7R6" && bxf.Files.Select(f => f.Name).SequenceEqual(["m40_00_stone_a.tpf.dcx", "m40_00_stone_a_l.tpf.dcx"]), "texture binder entries");
        Expect(bxf.Files.All(f => DCX.Is(f.Bytes)), "texture binder entries are DCX compressed, as their names say");
        var inner = bxf.Files.Select(f => TPF.Read(f.Bytes).Textures.Single()).ToList();
        Expect(inner[0].Name == "m40_00_stone_a" && inner[1].Name == "m40_00_stone_a_l", "the low detail entry's texture is named with _l");
        Expect(inner.All(t => t.Format == 103 && t.Bytes.AsSpan().SequenceEqual(ModelPort.BlackBc4Dds())), "texture binder TPF keeps the format and pixels");
        Expect(MapTextures.EntryCompression.Type == DCX.Type.DCX_KRAK, "the game's builds compress the entries with Kraken");
        Expect(MapTextures.CommonBinder("m45") == "/map/m45/common/m45_0000.tpfbhd", "texture binder path");
    }

    private static void ValidateLayout()
    {
        var good = MsbPort.Convert(SyntheticDs3(), SyntheticHost(), Ds3Id, ErId).Msb;
        var report = new MapValidate.Report();
        MapValidate.CheckLayout(report, MSBE.Read(good.Write()), ErId);
        Expect(report.Fails == 0, report.ToString());

        var noStart = SyntheticDs3();
        noStart.Parts.Players.Clear();
        var bad = MsbPort.Convert(noStart, SyntheticHost(), Ds3Id, ErId).Msb;
        bad.Parts.MapPieces[0].ModelName = "m999999";
        report = new MapValidate.Report();
        MapValidate.CheckLayout(report, bad, ErId);
        Expect(report.Fails == 2, report.ToString());
    }

    private static void ValidateEvents()
    {
        var common = SyntheticCommon();
        RunEvents.AddTo(common, ErId, 45000990, Back);
        var issues = MapValidate.CheckRunEvents(EMEVD.Read(common.Write()), ErId).ToList();
        Expect(issues.Count == 0, string.Join("; ", issues));
        common.Events.RemoveAll(e => e.ID == RunEvents.LeaveEventId);
        Expect(MapValidate.CheckRunEvents(common, ErId).Any(i => i.Contains("missing")), "missing event not reported");
        var wrongHost = SyntheticCommon();
        RunEvents.AddTo(wrongHost, MapId.Parse("m45_01_00_00"), 45010990, Back);
        Expect(MapValidate.CheckRunEvents(wrongHost, ErId).Any(i => i.Contains("warps to")), "wrong warp target not reported");
    }

    private static void PerMapWarps()
    {
        var common = SyntheticCommon();
        RunEvents.Warp[] warps = [new(ErId, 45000990, 0), new(MapId.Parse("m50_00_00_00"), 50000990, 1)];
        RunEvents.AddWarps(common, warps);
        RunEvents.AddWarps(common, warps);
        common = EMEVD.Read(common.Write());
        var events = common.Events.Where(e => e.ID == RunEvents.WarpEventId(0) || e.ID == RunEvents.WarpEventId(1)).ToList();
        Expect(events.Count == 2, $"{events.Count} warp events");
        var warp = events[1].Instructions.Single(i => i.Bank == 2003 && i.ID == 14);
        Expect(warp.ArgData[0] == 50 && BitConverter.ToUInt32(warp.ArgData, 4) == 50000990 && warp.Layer is null, "second warp target");
        var wait = events[1].Instructions[0];
        Expect(BitConverter.ToUInt32(wait.ArgData, 4) == RunEvents.WarpRequestFlag(1), "waits on its own request flag");
        var inits = common.Events.Single(e => e.ID == 0).Instructions.Count(i => i.Bank == 2000 && BitConverter.ToUInt32(i.ArgData, 4) >= RunEvents.WarpEventId(0));
        Expect(inits == 2, $"{inits} constructor inits");
        Expect(RunEvents.WarpRequestFlag(0) / 10000 == RunEvents.FlagBlock / 10000 && RunEvents.WarpRequestFlag(0) % 10000 is >= 2000 and < 3000,
            "request flags must be in the block's temporary range");
    }

    private static void AutoStartRun()
    {
        var common = SyntheticCommon();
        RunEvents.AddTo(common, ErId, 45000990, Back, autoStart: true);
        common = EMEVD.Read(common.Write());
        var auto = common.Events.Single(e => e.ID == RunEvents.AutoStartEventId);
        Expect(auto.Instructions.Count == 3, $"{auto.Instructions.Count} instructions");
        var end = auto.Instructions[0];
        Expect(end.Bank == 1003 && end.ID == 2 && BitConverter.ToUInt32(end.ArgData, 4) == RunEvents.Ds3RunActive, "ends when the run is active");
        var set = auto.Instructions[2];
        Expect(set.Bank == 2003 && set.ID == 66 && BitConverter.ToUInt32(set.ArgData, 4) == RunEvents.StartDs3Request, "requests the start");
        int Inits(EMEVD c) => c.Events.Single(e => e.ID == 0).Instructions.Count(i => i.Bank == 2000 && BitConverter.ToUInt32(i.ArgData, 4) == RunEvents.AutoStartEventId);
        Expect(Inits(common) == 1, "started once from the constructor");
        RunEvents.AddTo(common, ErId, 45000990, Back);
        Expect(common.Events.All(e => e.ID != RunEvents.AutoStartEventId) && Inits(common) == 0, "a build without it removes it");
        Expect(MapValidate.CheckRunEvents(common, ErId).Count() == 0, "run events still valid");
    }

    // Every map ID Elden Ring 2.7.2's registry names (worldmsblist + _dlc02, read 2026-11-08) that single-player
    // never visits and the game ships no files for: the only IDs a slot may use besides the colosseums.
    private static readonly HashSet<string> RegisteredFileless =
        ["m11_70_00_00", "m11_72_00_00", "m19_70_00_00", "m70_51_54_00", "m80_50_50_00", "m88_50_52_00"];

    private static void NewIdSlots()
    {
        var slots = MapSlots.NewIdSlots();
        Expect(slots.Select(s => s.Ds3).SequenceEqual(MapSlots.Colosseums.Select(s => s.Ds3)), "same DS3 maps");
        Expect(slots.All(s => s.Template is { Area: 45 } && s.Host != s.Er && !s.Probe), "colosseum templates, own IDs");
        Expect(slots.All(s => RegisteredFileless.Contains(s.Er.ToString())), "every new ID is one the game's registry names: " + string.Join(" ", slots.Select(s => s.Er)));
        Expect(slots.Select(s => s.Er).Distinct().Count() == slots.Count, "one ID each");
        Expect(MapSlots.LegacyNewIdSlots().All(s => s.Er.Area >= 50), "the legacy m50-m52 IDs stay known for cleaning");
    }

    private static void DefaultSlots()
    {
        Expect(MapSlots.All.All(s => !s.Probe && s.Template is null && s.Er.Area == 45), "default slots are the colosseums only: " + string.Join(" ", MapSlots.All.Select(s => s.Er)));
        Expect(MapSlots.ForDs3(Ds3Id)?.Er == ErId, "ForDs3 must return the real slot");
        Expect(MapSlots.All.Select(s => s.Er).Distinct().Count() == MapSlots.All.Count, "slots must not share an Elden Ring map");
    }
}
