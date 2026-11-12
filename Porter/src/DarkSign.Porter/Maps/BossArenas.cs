using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The F1 menu's "Boss arenas" warps: one per DS3 boss fight in a ported map, so a fight (its phases, its effects,
/// the arena's fog and scripts) can be tested without walking there.
///
/// Every DS3 boss fight is started by the common event 20005800, called from the map's script with the fight's IDs
/// (DarkScript3's DS3 common_func; the same layout in m30, m31, m37, m40 and m45): the defeated flag, the fog wall,
/// the arena area, the entry flag (set when the fight begins; the boss's own events wait for it), the boss (an entity
/// or an entity group), the first-encounter flag (0: none) and the area the first encounter waits for. The event
/// ends at once when the defeated flag is on. With a first-encounter flag that is off, it waits for that flag (a talk
/// or a cutscene sets it in DS3) and then begins the fight; with the flag on, it waits for the player to use the fog
/// wall. So the warp turns the defeated flag and the first-encounter flag off before it warps (the map's events start
/// with the load), and once the player stands in the map it turns the first-encounter flag on, which begins the fight
/// in the arena without a fog wall, talk or cutscene, and sets the entry flag for fights that have no such flag.
///
/// Map events that end are not run again on a later load, so the warp also clears the event flags of the fight's own
/// events (DS3 numbers a fight's events from its defeated flag: 14500800 Friede, 14505811-14505813 her phases), so a
/// fight already won can be fought again through every phase.
/// </summary>
public static class BossArenas
{
    public const int Ds3BossFight = 20005800;
    public const string ListFile = "darksign_bosses.tsv";

    /// <summary>Request flags 2400+i (reset on load) and events 1400+i in the run's flag block (<see cref="RunEvents"/>).</summary>
    public const int MaxArenas = 100;
    public static uint RequestFlag(int index) => RunEvents.FlagBlock + 2400 + (uint)index;
    public static uint EventId(int index) => RunEvents.FlagBlock + 1400 + (uint)index;
    public static bool IsArenaEvent(long id) => id >= RunEvents.FlagBlock + 1400 && id < RunEvents.FlagBlock + 1400 + MaxArenas;

    /// <summary>The arrival points' map-local entity numbers: 9970 + the fight's number in the map.</summary>
    public const int FirstPointNumber = 9970;

    /// <summary>A DS3 boss fight, in DS3's IDs.</summary>
    public sealed record Fight(string Name, uint Defeated, uint Entry, uint FirstEncounter, int Area, int Boss,
        IReadOnlyList<uint> Events, Vector3 Point, string PointFrom);

    /// <summary>A fight placed in an Elden Ring map: its IDs moved to the slot and its arrival point.</summary>
    public sealed record Arena(MapId Ds3, MapId Er, string MapName, string Name, uint Defeated, uint Entry, uint FirstEncounter,
        IReadOnlyList<uint> Events, uint PointEntity)
    {
        public int Index { get; init; }
    }

    /// <summary>
    /// The boss fights the map's own script starts with <see cref="Ds3BossFight"/>, in script order. Fights whose area
    /// isn't in the layout are left out. <paramref name="name"/> names a boss entity (the placement's NpcParam name).
    /// </summary>
    public static List<Fight> Find(MSB3 msb, EMEVD mapScript, MapId ds3, Func<MSB3.Part.Enemy, string?>? name = null)
    {
        var calls = new List<int[]>();
        foreach (var ins in mapScript.Events.SelectMany(e => e.Instructions))
        {
            // DS3's InitializeCommonEvent (2000[06]): the event ID, then its arguments.
            if (ins.Bank != 2000 || ins.ID != 6 || ins.ArgData.Length < 36 || BitConverter.ToInt32(ins.ArgData, 0) != Ds3BossFight)
                continue;
            calls.Add(Enumerable.Range(1, 8).Select(k => BitConverter.ToInt32(ins.ArgData, k * 4)).ToArray());
        }
        var defeatedLocals = calls.Select(c => Local(ds3, c[0])).Where(l => l >= 0).Distinct().Order().ToList();
        var healthBars = HealthBars(mapScript).ToList();
        var fights = new List<Fight>();
        foreach (var c in calls)
        {
            var (defeated, area, entry, boss, first) = (c[0], c[2], c[3], c[5], c[6]);
            if (defeated <= 0 || entry <= 0 || fights.Any(f => f.Defeated == (uint)defeated))
                continue;
            var region = msb.Regions.GetEntries().FirstOrDefault(r => r.EntityID == area && area > 0);
            if (region is null)
                continue;
            var members = Members(msb, boss);
            var (point, from) = ArrivalPoint(msb, region);
            // The first main health bar's placement (Friede's first stage), else the first other bar's (the Champion's
            // Gravetender's Greatwolf: the Gravetender's own bar is passed to an event), else the lowest member; named
            // without its stage ("- First Stage").
            var named = healthBars.Where(h => members.Contains(h.Entity)).OrderBy(h => h.Slot)
                .Select(h => msb.Parts.Enemies.FirstOrDefault(e => e.EntityID == h.Entity))
                .Concat(msb.Parts.Enemies.Where(e => members.Contains(e.EntityID)).OrderBy(e => e.EntityID))
                .Select(e => e is null ? null : name?.Invoke(e)).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            if (named is not null && named.IndexOf(" - ", StringComparison.Ordinal) is > 0 and var dash)
                named = named[..dash];
            fights.Add(new Fight(named?.Trim() ?? $"Boss {defeated}", (uint)defeated, (uint)entry, first > 0 ? (uint)first : 0, area, boss,
                FightEvents(mapScript, ds3, defeated, defeatedLocals), point, from));
        }
        return fights;
    }

    /// <summary>
    /// The map events that belong to a fight: those whose map-local number lies from the defeated flag's (800) up to
    /// the next fight's or 50 more, in either of DS3's two event ranges (0800 and 5800): Friede 14500800-14500859 and
    /// 14505800-14505859, the Champion's Gravetender from 860.
    /// </summary>
    public static List<uint> FightEvents(EMEVD mapScript, MapId ds3, int defeated, IReadOnlyList<int> defeatedLocals)
    {
        var from = Local(ds3, defeated);
        if (from < 0)
            return [];
        var next = defeatedLocals.Where(l => l > from).DefaultIfEmpty(int.MaxValue).Min();
        var to = Math.Min(next, from + 50);
        return mapScript.Events.Select(e => e.ID).Where(id => ds3.OwnsDs3Id(id) && id != defeated)
            .Where(id => Local(ds3, id) is var l && (l >= from && l < to || l >= from + 5000 && l < to + 5000))
            .Select(id => (uint)id).Distinct().ToList();
    }

    /// <summary>A DS3 ID's map-local number (0-9999), or -1 for an ID of another map.</summary>
    private static int Local(MapId ds3, long id) =>
        id >= ds3.Ds3FlagBase && id < ds3.Ds3FlagBase + 10_000 ? (int)(id - ds3.Ds3FlagBase)
        : id >= ds3.Ds3EntityBase && id < ds3.Ds3EntityBase + 10_000 ? (int)(id - ds3.Ds3EntityBase) : -1;

    /// <summary>The boss entity itself, or every enemy in the boss entity group.</summary>
    public static HashSet<int> Members(MSB3 msb, int boss)
    {
        var members = new HashSet<int> { boss };
        foreach (var e in msb.Parts.Enemies)
            if (e.EntityID > 0 && e.EntityGroups.Contains(boss))
                members.Add(e.EntityID);
        return members;
    }

    // DS3's Display Boss Health Bar (2003[11]): state, entity, slot, name. Only bars given an entity directly.
    private static IEnumerable<(int Entity, int Slot)> HealthBars(EMEVD script)
    {
        foreach (var e in script.Events)
            for (int i = 0; i < e.Instructions.Count; i++)
            {
                var ins = e.Instructions[i];
                if (ins.Bank != 2003 || ins.ID != 11 || ins.ArgData.Length < 12 || ins.ArgData[0] != 1
                    || e.Parameters.Any(p => p.InstructionIndex == i && p.TargetStartByte == 4))
                    continue;
                yield return (BitConverter.ToInt32(ins.ArgData, 4), BitConverter.ToInt16(ins.ArgData, 8));
            }
    }

    /// <summary>
    /// Where the warp lands: a DS3 player point inside the arena area (FromSoftware placed those on the floor, like
    /// the cutscene point 4502810 in Friede's arena), else the area's centre a little above its bottom (DS3's boss
    /// area boxes start below the floor: Friede's at y 26.0 under a floor at 27.4).
    /// </summary>
    public static (Vector3 Point, string From) ArrivalPoint(MSB3 msb, MSB3.Region area)
    {
        var inside = msb.Parts.Players.FirstOrDefault(p => Contains(area, p.Position));
        if (inside is not null)
            return (inside.Position, $"player point {inside.Name}");
        var lift = area.Shape switch
        {
            MSB.Shape.Box b => Math.Min(b.Height / 2, 1.5f),
            MSB.Shape.Cylinder c => Math.Min(c.Height / 2, 1.5f),
            _ => 0.5f,
        };
        return (area.Position + new Vector3(0, lift, 0), $"area {area.Name} + {lift:0.##} m");
    }

    /// <summary>Whether a point lies in a DS3 region (box, cylinder or sphere; boxes and cylinders rest on their position).</summary>
    public static bool Contains(MSB3.Region area, Vector3 p)
    {
        var d = p - area.Position;
        // Rotate into the region's frame (DS3 regions turn about Y; degrees).
        var yaw = -area.Rotation.Y * MathF.PI / 180f;
        var local = new Vector3(d.X * MathF.Cos(yaw) + d.Z * MathF.Sin(yaw), d.Y, -d.X * MathF.Sin(yaw) + d.Z * MathF.Cos(yaw));
        return area.Shape switch
        {
            MSB.Shape.Box b => Math.Abs(local.X) <= b.Width / 2 && Math.Abs(local.Z) <= b.Depth / 2 && local.Y >= 0 && local.Y <= b.Height,
            MSB.Shape.Cylinder c => local.X * local.X + local.Z * local.Z <= c.Radius * c.Radius && local.Y >= 0 && local.Y <= c.Height,
            MSB.Shape.Sphere s => d.LengthSquared() <= s.Radius * s.Radius,
            _ => false,
        };
    }

    /// <summary>
    /// Adds an arrival point (a player part copied from the layout's run start) for each fight whose boss was placed
    /// in the layout, and returns those fights in the slot's IDs. A fight whose boss isn't in the layout (the
    /// Champion Gundyr of the Untended Graves state, left out) gets no warp.
    /// </summary>
    public static List<Arena> Place(MSBE msb, MSB3 ds3Msb, IEnumerable<Fight> fights, MapSlots.Slot slot, List<string> notes)
    {
        var (ds3, er) = (slot.Ds3, slot.Er);
        var template = msb.Parts.Players.FirstOrDefault(p => p.EntityID == (uint)MsbPort.StartEntityId(er)) ?? msb.Parts.Players.FirstOrDefault();
        var placed = new HashSet<uint>(msb.Parts.Enemies.Where(e => e.EntityID > 0).Select(e => e.EntityID));
        var taken = new HashSet<uint>(msb.Parts.GetEntries().Select(p => p.EntityID).Concat(msb.Regions.GetEntries().Select(r => (uint)r.EntityID)));
        var arenas = new List<Arena>();
        int number = 0;
        foreach (var f in fights)
        {
            var members = Members(ds3Msb, f.Boss).Select(m => (uint)ds3.MoveEntity(m, er)).ToList();
            if (!members.Any(placed.Contains))
            {
                notes.Add($"boss arenas: {f.Name} ({f.Defeated}) left out: its boss isn't in the layout");
                continue;
            }
            if (template is null)
            {
                notes.Add("boss arenas: the layout has no player point to copy");
                break;
            }
            uint entity;
            do
                entity = (uint)(er.EntityBase + FirstPointNumber + number++);
            while (taken.Contains(entity) && number < 30);
            if (taken.Contains(entity))
                break;
            taken.Add(entity);
            var part = (MSBE.Part.Player)template.DeepCopy();
            part.Name = $"c0000_{entity % 10_000:D4}";
            part.InstanceID = (int)(entity % 10_000);
            part.EntityID = entity;
            part.Position = f.Point;
            msb.Parts.Players.Add(part);
            uint Move(uint id) => id == 0 ? 0 : (uint)ds3.MoveEntity((int)id, er);
            arenas.Add(new Arena(ds3, er, slot.Name, f.Name, Move(f.Defeated), Move(f.Entry), Move(f.FirstEncounter),
                f.Events.Select(Move).ToList(), entity));
            notes.Add($"boss arenas: {f.Name} lands at {f.Point.X:0.#} {f.Point.Y:0.#} {f.Point.Z:0.#} ({f.PointFrom}), entity {entity}; "
                      + $"flags: defeated {Move(f.Defeated)}, entry {Move(f.Entry)}, first encounter {Move(f.FirstEncounter)}; {f.Events.Count} fight events reset");
        }
        return arenas;
    }

    // ---- The warp events in common.emevd ----

    private const byte On = 1, Off = 0, EventFlagType = 0;

    /// <summary>
    /// Adds (or replaces) one warp event per arena in common.emevd and starts each from the constructor. Each waits
    /// for its request flag, resets the fight's flags, warps, waits for the map and then begins the fight.
    /// </summary>
    public static void AddWarps(EMEVD common, IReadOnlyList<Arena> arenas)
    {
        common.Events.RemoveAll(e => IsArenaEvent(e.ID));
        var constructor = common.Events.FirstOrDefault(e => e.ID == 0);
        if (constructor is null)
        {
            constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default);
            common.Events.Insert(0, constructor);
        }
        constructor.Instructions.RemoveAll(i => i.Bank == 2000 && i.ID == 0 && i.ArgData.Length >= 8 && IsArenaEvent(BitConverter.ToUInt32(i.ArgData, 4)));
        for (int i = 0; i < arenas.Count; i++)
        {
            var a = arenas[i];
            if (a.Index < 0 || a.Index >= MaxArenas)
                throw new ArgumentOutOfRangeException(nameof(arenas), $"boss arena index {a.Index}");
            var e = new EMEVD.Event(EventId(a.Index), EMEVD.Event.RestBehaviorType.Default);
            var m = a.Er;
            e.Instructions.Add(RunEvents.Make(1003, 0, On, EventFlagType, RequestFlag(a.Index)));
            e.Instructions.Add(SetFlag(RequestFlag(a.Index), Off));
            e.Instructions.Add(SetFlag(RunEvents.Ds3RunActive, On));
            // Before the warp: the map's events read these when the map loads.
            e.Instructions.Add(SetFlag(a.Defeated, Off));
            e.Instructions.Add(SetFlag(a.Entry, Off));
            if (a.FirstEncounter > 0)
                e.Instructions.Add(SetFlag(a.FirstEncounter, Off));
            foreach (var id in a.Events)
                e.Instructions.Add(SetFlag(id, Off));
            e.Instructions.Add(RunEvents.Make(2003, 14, (byte)m.Area, (byte)m.Block, (byte)m.Region, (byte)m.Index, a.PointEntity, 0));
            // After it: once the player stands in the map and its events have started, begin the fight.
            e.Instructions.Add(RunEvents.Make(1001, 0, 3f));
            e.Instructions.Add(RunEvents.Make(3, 8, (sbyte)0, On, (sbyte)m.Area, (sbyte)m.Block, (sbyte)m.Region, (sbyte)m.Index));
            e.Instructions.Add(RunEvents.Make(1001, 0, 2f));
            if (a.FirstEncounter > 0)
                e.Instructions.Add(SetFlag(a.FirstEncounter, On));
            e.Instructions.Add(SetFlag(a.Entry, On));
            e.Instructions.Add(RunEvents.Make(1000, 4, (byte)1));
            common.Events.Add(e);
            constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, EventId(a.Index), 0u));
        }
    }

    private static EMEVD.Instruction SetFlag(uint flag, byte state) => RunEvents.Make(2003, 66, EventFlagType, flag, state);

    /// <summary>darksign_bosses.tsv: one line per arena, read by the F1 menu's "Boss arenas" list.</summary>
    public static IEnumerable<string> ListLines(IEnumerable<Arena> arenas) =>
        new[] { "# DS3 map\tElden Ring slot\tmap name\tboss\twarp request flag (written by DarkSignPorter port-maps)" }
            .Concat(arenas.Select(a => $"{a.Ds3}\t{a.Er}\t{a.MapName}\t{a.Name}\t{RequestFlag(a.Index)}"));
}
