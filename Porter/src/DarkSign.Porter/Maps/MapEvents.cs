using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Ports the event scripts of every ported map: each DS3 map script (/event/mAA_BB_00_00.emevd) is converted with
/// <see cref="EventPort"/> and written as its slot's script, in the format of the slot's own Elden Ring script (and
/// with its link to common_func, which InitializeCommonEvent needs). The DS3 common events those scripts start
/// (DS3's common_func.emevd: bonfires, item pickups, NPC summons, boss fog...) are converted too, along with every
/// common event they start in turn, and added to Elden Ring's common_func.emevd under new event IDs
/// (<see cref="FlagMap"/>). The flag and event numbers given out are saved in darksign_flags.tsv (reused by --only runs).
/// </summary>
public static class MapEvents
{
    public const string CommonFunc = "/event/common_func.emevd.dcx";
    public const string FlagFile = "darksign_flags.tsv";
    public static string ScriptPath(MapId id) => $"/event/{id}.emevd.dcx";

    public sealed record Result(Dictionary<MapId, EMEVD> Scripts, EMEVD CommonFunc, string Summary);

    /// <param name="slots">The ported slots. A probe slot gets its own copy of its DS3 map's script.</param>
    /// <param name="ds3Scripts">Each DS3 map's script, by DS3 map ID.</param>
    /// <param name="ds3Common">DS3's common_func.emevd, or null.</param>
    /// <param name="erTemplate">The Elden Ring script a slot's script takes its format and links from.</param>
    /// <param name="erCommon">Elden Ring's common_func.emevd; ported DS3 common events from an earlier run are replaced.</param>
    public static Result Convert(Emedf ds3Defs, Emedf erDefs, FlagMap flags, IReadOnlyList<MapSlots.Slot> slots,
        IReadOnlyDictionary<MapId, EMEVD> ds3Scripts, EMEVD? ds3Common, Func<MapSlots.Slot, EMEVD> erTemplate, EMEVD erCommon,
        ActionButtons? buttons = null, Func<long, long?>? spEffects = null)
    {
        var main = slots.Where(s => !s.Probe).Select(s => (s.Ds3, s.Er)).ToList();
        var summary = new List<string>();
        var scripts = new Dictionary<MapId, EMEVD>();
        EventPort? commonPort = null;

        EventPort NewPort(IReadOnlyList<(MapId, MapId)> maps)
        {
            var port = new EventPort(ds3Defs, erDefs, maps, flags) { Buttons = buttons, SpEffects = spEffects };
            if (ds3Common is not null)
                port.Learn(ds3Common, common: true);
            foreach (var script in ds3Scripts.Values)
                port.Learn(script);
            // A DS3 button row's "invalid" flag (a fog wall's: the boss's defeated flag) moves like the scripts' flags.
            if (buttons is not null)
                buttons.FlagMover ??= v => port.Move(v, EventPort.Kind.Flag);
            return port;
        }

        foreach (var slot in slots)
        {
            if (!ds3Scripts.TryGetValue(slot.Ds3, out var ds3Script))
            {
                summary.Add($"{slot.Er}: DS3 has no script for {slot.Ds3}; left empty");
                continue;
            }
            // This slot's DS3 map moves to this slot (a probe slot's copy included); the others to their own.
            var maps = new List<(MapId, MapId)> { (slot.Ds3, slot.Er) };
            maps.AddRange(main.Where(m => m.Ds3 != slot.Ds3));
            var port = NewPort(maps);
            if (!slot.Probe)
                commonPort ??= port;
            var template = erTemplate(slot);
            var script = port.Convert(ds3Script, template.Format, id => id is 0 or 50 || !slot.Ds3.OwnsDs3Id(id) ? id : slot.Ds3.MoveEntity((int)id, slot.Er));
            script.Compression = template.Compression;
            script.LinkedFileOffsets = [.. template.LinkedFileOffsets];
            script.StringData = (byte[])template.StringData.Clone();
            foreach (var id in new long[] { 0, 50 })
                if (script.Events.All(e => e.ID != id))
                    script.Events.Insert(id == 0 ? 0 : Math.Min(1, script.Events.Count), new EMEVD.Event(id, EMEVD.Event.RestBehaviorType.Default));
            scripts[slot.Er] = script;
            summary.Add($"{slot.Er} (from {slot.Ds3}): {port.Stats}");
        }

        // The DS3 common events the maps start, and the ones those start.
        var common = new EMEVD(erCommon.Format) { Compression = erCommon.Compression };
        erCommon.Events.RemoveAll(e => FlagMap.IsEventSlot(e.ID));
        if (ds3Common is not null && commonPort is not null)
        {
            var byId = ds3Common.Events.GroupBy(e => e.ID).ToDictionary(g => g.Key, g => g.First());
            var needed = new HashSet<long>();
            var queue = new Queue<long>(ds3Scripts.Where(s => slots.Any(sl => sl.Ds3 == s.Key)).SelectMany(s => CommonCalls(s.Value)));
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!byId.TryGetValue(id, out var e) || !needed.Add(id))
                    continue;
                foreach (var next in CommonCalls(e))
                    queue.Enqueue(next);
            }
            var source = new EMEVD(ds3Common.Format);
            source.Events.AddRange(ds3Common.Events.Where(e => needed.Contains(e.ID)));
            var before = commonPort.Stats.ToString();
            var converted = commonPort.Convert(source, erCommon.Format, flags.Event);
            erCommon.Events.AddRange(converted.Events);

            // A common event that couldn't convert is started by nothing.
            var portedIds = converted.Events.Select(e => e.ID).ToHashSet();
            var droppedIds = needed.Select(flags.Event).Where(id => !portedIds.Contains(id)).ToHashSet();
            int disabled = EventPort.DisableCalls(erCommon, droppedIds) + scripts.Values.Sum(s => EventPort.DisableCalls(s, droppedIds));
            summary.Add($"common_func: {converted.Events.Count} of {needed.Count} DS3 common events ported "
                        + $"({droppedIds.Count} left out, {disabled} lines that start them disabled)");
        }
        else
            summary.Add("common_func: DS3 common events not ported (no DS3 common_func.emevd or no map script)");
        summary.Add($"flags: {flags.Flags.Count} DS3 global flags and {flags.Events.Count} common events have Elden Ring numbers");
        return new Result(scripts, erCommon, string.Join("\n  ", summary));
    }

    /// <summary>The DS3 common events a script starts (InitializeCommonEvent: event ID first in DS3).</summary>
    public static IEnumerable<long> CommonCalls(EMEVD script) => script.Events.SelectMany(CommonCalls);

    private static IEnumerable<long> CommonCalls(EMEVD.Event e) =>
        e.Instructions.Where(i => i.Bank == 2000 && i.ID == 6 && i.ArgData.Length >= 4).Select(i => (long)BitConverter.ToUInt32(i.ArgData, 0));

    /// <summary>Reads both games' scripts, converts them and writes the slot scripts, common_func and the flag table.</summary>
    public static string Port(PorterContext ctx, GameArchives ds3, GameArchives er, IReadOnlyList<MapSlots.Slot> slots)
    {
        var ds3Defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        var erDefs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("er-common")));
        var flagPath = ctx.ModPath(FlagFile);
        var flags = FlagMap.Load(flagPath);
        var (ds3Scripts, ds3Common) = ReadDs3(ds3, slots);

        var templates = new Dictionary<MapId, (EMEVD Script, DCX.CompressionInfo Compression)>();
        (EMEVD Script, DCX.CompressionInfo Compression) Template(MapSlots.Slot slot)
        {
            var host = er.Contains(ScriptPath(slot.Er)) ? slot.Er : slot.Host;
            if (!templates.TryGetValue(host, out var t))
            {
                var (bytes, compression) = er.ReadWithCompression(ScriptPath(host));
                templates[host] = t = (EMEVD.Read(bytes), compression);
            }
            return t;
        }

        var modCommon = ctx.ModPath(MapPorter.ModParts(CommonFunc));
        var (vanillaCommon, commonCompression) = er.ReadWithCompression(CommonFunc);
        // Build on the mod's copy when another step already changed it (earlier ported events are replaced).
        var erCommon = EMEVD.Read(File.Exists(modCommon) ? DCX.Decompress(File.ReadAllBytes(modCommon)) : vanillaCommon);

        // Action button prompts need ActionButtonParam rows; without the regulation they stay unconverted.
        ParamStore? regulation = null;
        ActionButtons? buttons = null;
        string buttonNote;
        try
        {
            regulation = ActionButtons.LoadRegulation(ctx);
            var param = regulation.Get("ActionButtonParam") ?? throw new InvalidDataException("no ActionButtonParam");
            var taken = ActionButtons.TakenRows(param);
            buttons = new ActionButtons(taken.Contains);
            buttonNote = "";
        }
        catch (Exception ex)
        {
            buttonNote = $"action buttons: not converted ({ex.Message}); events that wait for them are left out";
        }

        // SpEffects: a DS3 ID the enemy port gave a row (700,000,000 + the ID) is pointed at that row.
        Func<long, long?>? spEffects = null;
        string spNote = "";
        try
        {
            var sp = (regulation ?? ActionButtons.LoadRegulation(ctx)).Get("SpEffectParam") ?? throw new InvalidDataException("no SpEffectParam");
            spEffects = id => Enemies.ChrEffects.SpEffectId((int)Math.Min(id, int.MaxValue)) is > 0 and var n && sp[n] is not null ? n : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            spNote = $"speffects: script SpEffect IDs kept at their DS3 IDs ({ex.Message})";
        }
        var result = Convert(ds3Defs, erDefs, flags, slots, ds3Scripts, ds3Common, s => Template(s).Script, erCommon, buttons, spEffects);
        // Boss kits go through the same buttons, so their action button rows are written with the maps'.
        (EMEVD Common, DCX.CompressionInfo Compression, string Note)? kits = null;
        string KitNote(Exception ex) => $"boss kits: not added ({ex.Message}); bosses outside their DS3 map run no map scripts";
        string kitNote = "";
        try
        {
            kits = PortKits(ctx, ds3, er, ds3Defs, erDefs, flags, buttons, spEffects);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            kitNote = KitNote(ex);
        }
        if (buttons is not null && regulation is not null)
        {
            try
            {
                buttonNote = buttons.Write(ctx, ds3, er, regulation);
            }
            catch (Exception ex)
            {
                // Scripts that point at rows which were never written must not ship: fall back to leaving them out.
                buttonNote = $"action buttons: FAILED to write rows ({ex.Message}); converting again without them";
                flags = FlagMap.Load(flagPath);
                result = Convert(ds3Defs, erDefs, flags, slots, ds3Scripts, ds3Common, s => Template(s).Script, erCommon, null, spEffects);
                try
                {
                    kits = PortKits(ctx, ds3, er, ds3Defs, erDefs, flags, null, spEffects);
                }
                catch (Exception kitEx) when (kitEx is not OutOfMemoryException)
                {
                    kits = null;
                    kitNote = KitNote(kitEx);
                }
            }
        }
        if (kits is { } k)
        {
            File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts("/event/common.emevd.dcx")), DCX.Compress(k.Common.Write(), k.Compression));
            kitNote = k.Note;
        }
        foreach (var slot in slots)
            if (result.Scripts.TryGetValue(slot.Er, out var script))
                File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts(ScriptPath(slot.Er))), DCX.Compress(script.Write(), Template(slot).Compression));
        File.WriteAllBytes(modCommon, DCX.Compress(result.CommonFunc.Write(), commonCompression));
        flags.Save(flagPath);
        return "events:\n  " + result.Summary + (buttonNote.Length > 0 ? "\n  " + buttonNote : "") + (spNote.Length > 0 ? "\n  " + spNote : "")
               + (kitNote.Length > 0 ? "\n  " + kitNote : "");
    }

    /// <summary>The DS3 scripts of these slots' maps, and DS3's common_func (null when missing).</summary>
    public static (Dictionary<MapId, EMEVD> Maps, EMEVD? Common) ReadDs3(GameArchives ds3, IEnumerable<MapSlots.Slot> slots)
    {
        var scripts = new Dictionary<MapId, EMEVD>();
        foreach (var id in slots.Select(s => s.Ds3).Distinct())
            if (ds3.Contains(ScriptPath(id)))
                scripts[id] = EMEVD.Read(ds3.Read(ScriptPath(id)));
        return (scripts, ds3.Contains(CommonFunc) ? EMEVD.Read(ds3.Read(CommonFunc)) : null);
    }

    /// <summary>
    /// The DS3 entities one DS3 map's script (and the DS3 common events it starts) warps the player to or makes a
    /// respawn point, for <see cref="RegionPort.AddWarpPoints"/>.
    /// </summary>
    public static SortedSet<int> WarpPoints(PorterContext ctx, GameArchives ds3, MapId ds3Map)
    {
        if (!ds3.Contains(ScriptPath(ds3Map)))
            return [];
        var defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        var script = EMEVD.Read(ds3.Read(ScriptPath(ds3Map)));
        var common = ds3.Contains(CommonFunc) ? EMEVD.Read(ds3.Read(CommonFunc)) : null;
        var port = new EventPort(defs, defs, [], new FlagMap());
        if (common is not null)
            port.Learn(common, common: true);
        port.Learn(script);
        return port.WarpPoints([script]);
    }

    /// <summary>
    /// The DS3 SpEffect IDs the ported maps' scripts (and DS3's common events) set, clear or check, for the enemy
    /// port to give rows, so <see cref="Port"/> can point the scripts at them.
    /// </summary>
    public static SortedSet<int> ScriptSpEffects(PorterContext ctx, GameArchives ds3, IReadOnlyList<MapSlots.Slot> slots)
    {
        var defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        var (scripts, common) = ReadDs3(ds3, slots);
        var port = new EventPort(defs, defs, [], new FlagMap());
        if (common is not null)
            port.Learn(common, common: true);
        foreach (var s in scripts.Values)
            port.Learn(s);
        var ids = port.ScriptSpEffects(common is null ? scripts.Values : scripts.Values.Append(common));
        // Boss kits run in every map, so their SpEffects (Pontiff's 10636 and 10637) need rows whichever maps ported.
        var kits = new EMEVD(EMEVD.Game.DarkSouls3);
        kits.Events.AddRange(Kits(ctx, ds3).SelectMany(k => k.Events));
        ids.UnionWith(port.ScriptSpEffects([kits]));
        return ids;
    }

    private static List<BossKits.Kit>? _kits;
    private static readonly List<string> _kitNotes = [];

    /// <summary>Every DS3 boss's kit (<see cref="BossKits"/>), read once per run.</summary>
    public static List<BossKits.Kit> Kits(PorterContext ctx, GameArchives ds3)
    {
        if (_kits is not null)
            return _kits;
        var defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        _kitNotes.Clear();
        return _kits = BossKits.Plan(ds3, defs, null, _kitNotes);
    }

    /// <summary>
    /// The boss kits in Elden Ring's common.emevd (the mod's copy when another step changed it), and
    /// darksign_boss_kits.tsv for the F1 spawner. Returns the script to write and a summary.
    /// </summary>
    private static (EMEVD Common, DCX.CompressionInfo Compression, string Note) PortKits(PorterContext ctx, GameArchives ds3,
        GameArchives er, Emedf ds3Defs, Emedf erDefs, FlagMap flags, ActionButtons? buttons, Func<long, long?>? spEffects)
    {
        const string Common = "/event/common.emevd.dcx";
        var modCommon = ctx.ModPath(MapPorter.ModParts(Common));
        var (vanilla, compression) = er.ReadWithCompression(Common);
        var common = EMEVD.Read(File.Exists(modCommon) ? DCX.Decompress(File.ReadAllBytes(modCommon)) : vanilla);
        var kits = Kits(ctx, ds3);
        var note = BossKits.AddTo(common, kits, ds3Defs, erDefs, flags, spEffects, buttons);
        // The F1 spawner's table: only kits whose every character the enemy port ported can be spawned whole.
        var lines = new List<string>();
        var mapPath = ctx.ModPath(EnemyPort.ChrMap.FileName);
        if (File.Exists(mapPath))
        {
            var map = EnemyPort.ChrMap.Parse(File.ReadAllLines(mapPath));
            EnemyPort.ErEnemy? Ported(BossKits.Character c)
            {
                var ds3Enemy = new EnemyPort.Ds3Enemy(c.Model, c.NpcParam, "", true, false);
                var er = map.FindNpc(ds3Enemy) ?? map.Find(ds3Enemy);
                return er is null ? null : er with { ThinkParam = map.Think(c.ThinkParam, c.Model) ?? er.ThinkParam };
            }
            lines = BossKits.Lines(kits, Ported);
        }
        File.WriteAllLines(ctx.ModPath(BossKits.FileName), lines);
        var kitNotes = _kitNotes.Count > 0 ? "\n    " + string.Join("\n    ", _kitNotes) : "";
        return (common, compression, $"{note}; {Math.Max(0, lines.Count - 1)} spawnable from F1 ({BossKits.FileName}){kitNotes}");
    }
}
