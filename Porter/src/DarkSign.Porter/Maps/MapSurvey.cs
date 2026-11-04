using System.Text;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// `map-survey`: one report of everything the map porter assumes about both games' files, for each ported slot, so
/// the first run on a PC answers the open questions at once: which files exist under which paths, how binder
/// entries are named, which DS3 player point the run starts at, the host's collision format, the map materials and
/// texture folders Elden Ring uses, and where "Leave DS3 run" would warp to. Read-only; writes only the report.
/// </summary>
public static class MapSurvey
{
    public static int Run(PorterContext ctx, IEnumerable<string> args)
    {
        var only = args.Select(a => MapId.Parse(a).ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var r = new StringBuilder();

        foreach (var slot in MapSlots.All.Where(s => only.Count == 0 || only.Contains(s.Ds3.ToString())))
        {
            r.AppendLine($"=== {slot.Ds3} ({slot.Name}) -> {slot.Er}");
            Section(r, "DS3 layout", () => Ds3Layout(ds3, slot.Ds3));
            Section(r, "Elden Ring host layout", () => ErLayout(er, slot.Er));
            foreach (var (game, archives, id, isEr) in new[] { ("DS3", ds3, slot.Ds3, false), ("ER", er, slot.Er, true) })
            {
                Section(r, $"{game} files", () => Files(archives, id, isEr));
                foreach (var kind in new[] { "h", "l", "f" })
                {
                    var path = isEr ? MapCollisions.ErBinderPath(id, kind) : MapCollisions.Ds3BinderPath(id, kind);
                    Section(r, $"{game} collision {kind}", () => Binder(archives, path, split: true));
                }
                Section(r, $"{game} textures", () => Textures(archives, id.AreaFolder));
            }
            Section(r, "ER host collision template", () => CollisionTemplate(er, slot.Er));
            Section(r, "ER host map materials", () => HostMaterials(er, slot.Er));
            Section(r, "Event scripts", () => Events(er, slot.Er));
        }

        Section(r, "Cutscenes (remo)", () => Cutscenes(ds3, er, MapSlots.All.Where(s => only.Count == 0 || only.Contains(s.Ds3.ToString()))
            .Select(s => s.Ds3).Distinct().ToList()));

        var log = ctx.WorkPath("logs", $"map-survey-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(log, r.ToString());
        Console.WriteLine(r.ToString());
        Console.WriteLine($"report written to {log}");
        return 0;
    }

    private static void Section(StringBuilder r, string title, Func<IEnumerable<string>> lines)
    {
        r.AppendLine($"--- {title}");
        try
        {
            foreach (var line in lines())
                r.AppendLine("  " + line);
        }
        catch (Exception ex)
        {
            r.AppendLine($"  error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IEnumerable<string> Ds3Layout(GameArchives ds3, MapId id)
    {
        var path = MapPorter.MsbPath(id);
        if (!ds3.Contains(path))
        {
            yield return $"missing {path}";
            yield break;
        }
        var msb = MSB3.Read(ds3.Read(path));
        yield return $"parts: {msb.Parts.MapPieces.Count} map pieces, {msb.Parts.Collisions.Count} collisions, "
                     + $"{msb.Parts.Players.Count} players, {msb.Parts.Enemies.Count} enemies, {msb.Parts.Objects.Count} objects, "
                     + $"{msb.Parts.ConnectCollisions.Count} connect collisions";
        yield return $"models: {msb.Models.MapPieces.Count} map pieces, {msb.Models.Collisions.Count} collisions; "
                     + $"regions: {msb.Regions.GetEntries().Count}; events: {msb.Events.GetEntries().Count}";
        var start = MsbPort.ChooseStart(msb, id);
        yield return $"run start: {start?.Name ?? "(none)"} entity {start?.EntityID} at {start?.Position}";
        foreach (var p in msb.Parts.Players.Take(30))
            yield return $"player {p.Name} entity {p.EntityID} at {p.Position}";
        foreach (var m in msb.Models.MapPieces.Take(3).Concat<MSB3.Model>(msb.Models.Collisions.Take(3)))
            yield return $"model {m.Name} sib {m.SibPath}";
    }

    private static IEnumerable<string> ErLayout(GameArchives er, MapId id)
    {
        var path = MapPorter.MsbPath(id);
        if (!er.Contains(path))
        {
            yield return $"missing {path}";
            yield break;
        }
        var msb = MSBE.Read(er.Read(path));
        yield return $"parts: {msb.Parts.MapPieces.Count} map pieces, {msb.Parts.Collisions.Count} collisions, "
                     + $"{msb.Parts.Players.Count} players, {msb.Parts.Enemies.Count} enemies, {msb.Parts.Assets.Count} assets";
        foreach (var p in msb.Parts.Players.Take(10))
            yield return $"player {p.Name} entity {p.EntityID} at {p.Position}";
        foreach (var c in msb.Parts.Collisions.Take(3))
            yield return $"collision {c.Name} model {c.ModelName} hitfilter {c.HitFilterID} playregion {c.PlayRegionID}";
        foreach (var m in msb.Models.MapPieces.Take(3).Concat<MSBE.Model>(msb.Models.Collisions.Take(3)))
            yield return $"model {m.Name} sib {m.SibPath}";
    }

    private static IEnumerable<string> Files(GameArchives game, MapId id, bool isEr)
    {
        var prefix = isEr ? $"/map/{id.AreaFolder}/{id}/" : $"/map/{id}/";
        var files = game.Entries.Where(e => e.Path is not null && e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path!).OrderBy(p => p).ToList();
        yield return $"{files.Count} named files under {prefix}";
        foreach (var group in files.GroupBy(f => ItemText.Leaf(f).Split('.', 2).ElementAtOrDefault(1) ?? ""))
            yield return $"*.{group.Key}: {group.Count()} (e.g. {ItemText.Leaf(group.First())})";
        foreach (var extra in new[] { $"/map/{id.AreaFolder}/{id}/{id}.nvmhktbnd.dcx", $"/map/{id}/{id}.nvmbnd.dcx",
                     $"/event/{id}.emevd.dcx", $"/script/talk/{id}.talkesdbnd.dcx", MapPorter.MsbPath(id) })
            yield return $"{extra}: {(game.Contains(extra) ? "yes" : "no")}";
    }

    private static IEnumerable<string> Binder(GameArchives game, string bhd, bool split)
    {
        if (!game.Contains(bhd))
        {
            yield return $"missing {bhd}";
            yield break;
        }
        var bxf = BXF4.Read(game.Read(bhd), game.Read(bhd[..^3] + "bdt"));
        yield return $"{bhd}: {bxf.Files.Count} files, version {bxf.Version}, format {bxf.Format}, unicode {bxf.Unicode}";
        foreach (var f in bxf.Files.Take(4))
        {
            var compression = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes, out var c) is { } b ? $"{c.Type}, {b.Length} bytes" : "" : "no DCX";
            yield return $"[{f.ID}] {f.Name} flags {f.Flags} ({compression})";
        }
    }

    private static IEnumerable<string> Textures(GameArchives game, string area)
    {
        var binders = MapTextures.AreaBinders(game, area).ToList();
        if (binders.Count == 0)
            yield return $"no /map/{area}/{area}_000N.tpfbhd";
        foreach (var bhd in binders)
        {
            var bxf = BXF4.Read(game.Read(bhd), game.Read(bhd[..^3] + "bdt"));
            yield return $"{bhd}: {bxf.Files.Count} textures, e.g. {string.Join(", ", bxf.Files.Take(3).Select(f => f.Name))}";
            var first = bxf.Files.FirstOrDefault();
            if (first is not null)
            {
                var tpf = TPF.Read(DCX.Decompress(first.Bytes, out var c));
                yield return $"  tpf: platform {tpf.Platform}, encoding {tpf.Encoding}, flag2 {tpf.Flag2}, dcx {c.Type}, "
                             + $"texture {tpf.Textures.FirstOrDefault()?.Name} format {tpf.Textures.FirstOrDefault()?.Format}";
            }
        }
        var other = game.Entries.Where(e => e.Path is not null && e.Path.StartsWith($"/map/{area}/", StringComparison.OrdinalIgnoreCase)
                                            && e.Path.Contains(".tpf", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path!).Except(binders).Take(10);
        foreach (var path in other)
            yield return $"other texture file: {path}";
    }

    private static IEnumerable<string> CollisionTemplate(GameArchives er, MapId id)
    {
        var bhd = MapCollisions.ErBinderPath(id, "h");
        if (!er.Contains(bhd))
        {
            yield return $"missing {bhd}";
            yield break;
        }
        var bxf = BXF4.Read(er.Read(bhd), er.Read(bhd[..^3] + "bdt"));
        var compendium = bxf.Files.FirstOrDefault(f => f.Name.Contains(".compendium", StringComparison.OrdinalIgnoreCase));
        yield return $"compendium: {compendium?.Name ?? "(none)"}";
        var hkx = bxf.Files.FirstOrDefault(f => f.Name.EndsWith(".hkx.dcx", StringComparison.OrdinalIgnoreCase));
        if (hkx is null)
            yield break;
        var bytes = DCX.Decompress(hkx.Bytes);
        yield return $"{hkx.Name}: starts {System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length)).Replace('\0', '.')}";
        var comp = compendium is null ? null : DCX.Is(compendium.Bytes) ? DCX.Decompress(compendium.Bytes) : compendium.Bytes;
        var template = ErCollisionTemplate.FromFile(bytes, comp);
        yield return template is null
            ? "no compressed-mesh body found"
            : $"template: system '{template.SystemName}', shape flags {template.ShapeFlags}, dispatch {template.DispatchType}, "
              + $"materials {template.Materials().Count()}";
    }

    private static IEnumerable<string> HostMaterials(GameArchives er, MapId id)
    {
        var msb = MSBE.Read(er.Read(MapPorter.MsbPath(id)));
        foreach (var model in msb.Models.MapPieces.Take(3))
        {
            var path = MapPorter.ErMapBndPath(id, MapPorter.ModelNumber(model.Name));
            if (!er.Contains(path))
            {
                yield return $"missing {path}";
                continue;
            }
            var bnd = BND4.Read(er.Read(path));
            foreach (var f in bnd.Files)
                yield return $"{path}: [{f.ID}] {f.Name}";
            foreach (var flver in bnd.Files.Where(f => FLVER2.Is(f.Bytes)).Select(f => FLVER2.Read(f.Bytes)))
                foreach (var m in flver.Materials.Take(4))
                    yield return $"  material {m.Name}: {m.MTD} textures [{string.Join(", ", m.Textures.Select(t => $"{t.ParamName}={t.Path}"))}]";
        }
    }

    /// <summary>
    /// What the cutscene port will have to convert: each game's remo binders, the cutscenes the ported DS3 maps play
    /// (from their event scripts) and what's inside one binder of each game (Havok animation, camera, timeline).
    /// </summary>
    private static IEnumerable<string> Cutscenes(GameArchives ds3, GameArchives er, IReadOnlyList<MapId> maps)
    {
        foreach (var (name, game) in new[] { ("DS3", ds3), ("ER", er) })
        {
            var remo = game.Entries.Where(e => e.Path is not null && e.Path.StartsWith("/remo/", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Path!).OrderBy(p => p).ToList();
            yield return $"{name}: {remo.Count} named files under /remo/ (e.g. {string.Join(", ", remo.Take(6).Select(ItemText.Leaf))})";
        }

        var played = new SortedSet<int>();
        foreach (var id in maps)
        {
            var path = $"/event/{id}.emevd.dcx";
            if (!ds3.Contains(path))
                continue;
            var ids = EMEVD.Read(ds3.Read(path)).Events.SelectMany(e => e.Instructions)
                .Where(i => i.Bank == 2002 && i.ArgData.Length >= 4).Select(i => BitConverter.ToInt32(i.ArgData, 0)).Where(c => c > 0).Distinct().ToList();
            played.UnionWith(ids);
            yield return $"{id} plays {ids.Count} cutscenes: {string.Join(", ", ids.Take(20))}";
        }

        foreach (var (name, game, ids) in new[] { ("DS3", ds3, played.ToList()), ("ER", er, new List<int>()) })
        {
            var remo = game.Entries.Where(e => e.Path is not null && e.Path.EndsWith(".remobnd.dcx", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path!).ToList();
            var pick = remo.FirstOrDefault(p => ids.Any(i => ItemText.Leaf(p).Contains(i.ToString()))) ?? remo.FirstOrDefault();
            if (pick is null)
            {
                yield return $"{name}: no named .remobnd.dcx to look inside";
                continue;
            }
            yield return $"{name} sample {pick}:";
            var bnd = BND4.Read(game.Read(pick));
            yield return $"  BND4 {bnd.Version}, {bnd.Files.Count} files";
            foreach (var group in bnd.Files.GroupBy(f => Path.GetExtension(ItemText.Leaf(f.Name)).ToLowerInvariant()))
            {
                var first = group.First();
                var bytes = DCX.Is(first.Bytes) ? DCX.Decompress(first.Bytes) : first.Bytes;
                var head = string.Concat(bytes.Take(16).Select(b => b is >= 32 and < 127 ? (char)b : '.'));
                yield return $"  *{group.Key}: {group.Count()} (e.g. {ItemText.Leaf(first.Name)}, {bytes.Length} bytes, starts \"{head}\")";
            }
        }
    }

    private static IEnumerable<string> Events(GameArchives er, MapId host)
    {
        var scripts = new List<EMEVD>();
        foreach (var path in new[] { "/event/m11_10_00_00.emevd.dcx", "/event/common.emevd.dcx" })
            if (er.Contains(path))
                scripts.Add(EMEVD.Read(er.Read(path)));
            else
                yield return $"missing {path}";
        var back = RunEvents.FindReturnPoint(scripts);
        yield return $"leave-run warp target: {back?.Map} entity {back?.EntityId}";
        var hostEvents = $"/event/{host}.emevd.dcx";
        if (er.Contains(hostEvents))
        {
            var e = EMEVD.Read(er.Read(hostEvents));
            yield return $"{hostEvents}: {e.Events.Count} events, format {e.Format}";
        }
        var flagUse = scripts.SelectMany(s => s.Events).SelectMany(ev => ev.Instructions)
            .Count(i => i.ArgData.Length >= 4 && Enumerable.Range(0, i.ArgData.Length - 3)
                .Any(o => BitConverter.ToUInt32(i.ArgData, o) / 10000 == RunEvents.FlagBlock / 10000));
        yield return $"instructions already naming flags in {RunEvents.FlagBlock / 10000}xxxx: {flagUse}";
    }
}
