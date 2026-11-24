using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Where a death in a ported DS3 map sends the player: the DS3 bonfire they last rested at, or, until they rest at one,
/// the bonfire nearest to where they came into the map. That is what DS3 does: resting and bonfire travel set the
/// respawn point, and a new game starts with one.
/// <para>
/// Why it was Elden Ring's Chapel of Anticipation (release 1.0.626, probe 22: died to Gundyr in m45_00, came back in
/// the Chapel): Elden Ring respawns the player at its saved return point, which only two things change, resting at a
/// grace and the script line "Set Player Respawn Point" (2003[23]). Every way into a DS3 map is a script warp
/// (the run start, the F1 map, boss and cutscene warps, the bonfire menu's Travel: all Warp Player, 2003[14], in
/// common.emevd), and a script warp leaves the return point where it was: the last Elden Ring grace, or for a
/// character that never rested at one, the Chapel where the game starts. So a death before the first DS3 rest
/// always left the DS3 world, and bonfire Travel never moved the respawn point either (DS3's warp does).
/// </para>
/// <para>
/// The fix, for every ported map:
/// </para>
/// <list type="bullet">
/// <item>every Warp Player into a ported map in common.emevd first turns <see cref="FollowsFlag"/> off ("the respawn
/// point hasn't been set since this warp");</item>
/// <item>each ported map's script gets an event, started on every load: when the flag is off and the player is inside
/// this map, it sets the respawn point to the nearest of the map's bonfire standing points (980+n; the map's start
/// point where DS3 put no bonfire) and turns the flag on. Travel lands on a bonfire's point, so its nearest is the
/// bonfire travelled to; a first arrival gets the bonfire closest to where it landed.</item>
/// </list>
/// Once the flag is on, nothing here touches the respawn point again: a rest at another bonfire (the engine's own
/// grace rest) stays the respawn point through quits and reloads, as in DS3. DS3's own "Set Player Respawn Point"
/// lines are converted with the rest of its scripts (<see cref="EventPort"/>).
/// <para>
/// EMEVD can't compare distances, so "nearest" is found in rings: the first standing point within
/// <see cref="Rings"/>[0] metres of the player wins, then the next ring, and so on; past the last ring the map's first
/// bonfire. A condition group can't be emptied once it holds a condition, so each event checks at most seven points
/// (AND groups 1-7) and starts the next one.
/// </para>
/// </summary>
public static class BonfireRespawn
{
    /// <summary>Saved (0-999 of the run block): on once a ported map has set the respawn point since the last scripted warp in.</summary>
    public const uint FollowsFlag = RunEvents.FlagBlock + 101;

    /// <summary>The rings "nearest" is found in, in metres.</summary>
    public static readonly float[] Rings = [10f, 40f, 120f, 300f, 800f];

    /// <summary>First map-local event number tried (the flame events start at <see cref="Bonfires.FirstEvent"/>).</summary>
    public const int FirstEvent = 9900;

    public const int PointsPerEvent = 7;
    private const uint Player = 10000;
    private const byte On = 1, Off = 0, FlagType = 0, End = 0, Inside = 1, Fail = 0;

    private static EMEVD.Instruction SetFlag(byte state) => RunEvents.Make(2003, 66, FlagType, FollowsFlag, state);
    private static EMEVD.Instruction SetRespawn(uint point) => RunEvents.Make(2003, 23, point);

    /// <summary>
    /// Adds the respawn events to a ported map's script and starts the first from event 0.
    /// </summary>
    /// <param name="points">The map's bonfire standing points in bonfire order (the first is the last resort), or its
    /// start point alone.</param>
    /// <returns>Events added (0 when there are no points).</returns>
    public static int AddEvents(EMEVD script, MapId er, IReadOnlyList<uint> points)
    {
        if (points.Count == 0)
            return 0;
        var constructor = script.Events.FirstOrDefault(e => e.ID == 0);
        if (constructor is null)
            script.Events.Insert(0, constructor = new EMEVD.Event(0, EMEVD.Event.RestBehaviorType.Default));
        var ids = script.Events.Select(e => e.ID).ToHashSet();
        long next = er.EntityBase + FirstEvent;
        long NewId()
        {
            while (!ids.Add(next))
                next++;
            return next;
        }

        // One step per ring and group of seven points; a single point needs no search.
        var steps = points.Count == 1 ? new List<(float Ring, uint[] Points)>()
            : Rings.SelectMany(r => points.Chunk(PointsPerEvent).Select(c => (Ring: r, Points: c))).ToList();
        var events = new List<EMEVD.Event>();
        var arrival = new EMEVD.Event(NewId(), EMEVD.Event.RestBehaviorType.Default);
        arrival.Instructions.Add(RunEvents.Make(1003, 2, End, On, FlagType, FollowsFlag));          // END IF the flag is on
        arrival.Instructions.Add(RunEvents.Make(1001, 0, 1f));                                         // the player is placed after the load
        arrival.Instructions.Add(RunEvents.Make(1003, 8, End, Off, (sbyte)er.Area, (sbyte)er.Block, (sbyte)er.Region, (sbyte)er.Index)); // END IF outside this map
        events.Add(arrival);
        var current = arrival;
        foreach (var step in steps)
        {
            var e = new EMEVD.Event(NewId(), EMEVD.Event.RestBehaviorType.Default);
            current.Instructions.Add(RunEvents.Make(2000, 0, 0, (uint)e.ID, 0u));
            for (int i = 0; i < step.Points.Length; i++)
            {
                var group = (sbyte)(i + 1);
                e.Instructions.Add(RunEvents.Make(3, 3, group, Inside, Player, step.Points[i], step.Ring, 1)); // IF player within ring of the point
                e.Instructions.Add(RunEvents.Make(1000, 1, (byte)3, Fail, group));                         // SKIP 3 IF not
                e.Instructions.Add(SetRespawn(step.Points[i]));
                e.Instructions.Add(SetFlag(On));
                e.Instructions.Add(RunEvents.Make(1000, 4, End));
            }
            events.Add(e);
            current = e;
        }
        current.Instructions.Add(SetRespawn(points[0]));
        current.Instructions.Add(SetFlag(On));
        script.Events.AddRange(events);
        constructor.Instructions.Add(RunEvents.Make(2000, 0, 0, (uint)arrival.ID, 0u));
        return events.Count;
    }

    /// <summary>
    /// Turns <see cref="FollowsFlag"/> off right before every Warp Player into one of <paramref name="maps"/>, so the
    /// map warped to sets the respawn point. Runs again safely: a warp already preceded by the line gets no second one.
    /// </summary>
    /// <returns>Warps covered.</returns>
    public static int ClearBeforeWarps(EMEVD common, IReadOnlySet<MapId> maps)
    {
        int count = 0;
        foreach (var e in common.Events)
            for (int at = 0; at < e.Instructions.Count; at++)
            {
                var i = e.Instructions[at];
                if (i.Bank != 2003 || i.ID != 14 || i.ArgData.Length < 8)
                    continue;
                var a = i.ArgData;
                // A target filled in from an event parameter isn't known here.
                if (e.Parameters.Any(p => p.InstructionIndex == at && p.TargetStartByte < 4))
                    continue;
                if (!maps.Contains(new MapId(a[0], a[1], a[2], a[3])))
                    continue;
                count++;
                if (at > 0 && IsClear(e.Instructions[at - 1]))
                    continue;
                e.Instructions.Insert(at, SetFlag(Off));
                foreach (var p in e.Parameters.Where(p => p.InstructionIndex >= at))
                    p.InstructionIndex++;
                at++;
            }
        return count;
    }

    private static bool IsClear(EMEVD.Instruction i) =>
        i.Bank == 2003 && i.ID == 66 && i.ArgData.Length >= 9 && i.ArgData[0] == FlagType
        && BitConverter.ToUInt32(i.ArgData, 4) == FollowsFlag && i.ArgData[8] == Off;
}
