using System.Collections.Concurrent;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// No Elden Ring battle music from Elden Ring enemies standing in for DS3 ones. Elden Ring starts its battle music
/// from a character's NpcParam threatLv (<see cref="Enemies.ChrPort.MusicThreatLevel"/>). The ported DS3 characters'
/// rows get the quiet level, but a stand-in (an Elden Ring enemy or person placed where DS3 had a character the enemy
/// port didn't build) names Elden Ring's own row, threat level included, and Lothric Castle played Elden Ring's music
/// in Devin's 2026-11-20 playtest, the first one with the 15-map world. Changing Elden Ring's own rows would quiet
/// those enemies in Elden Ring's world too, so a stand-in whose row has another level is placed on a copy of the row
/// with the quiet level instead, at <see cref="RowBase"/> + the row's ID (the same ID on every PC, for co-op).
/// <para>The copies are written after the people armour step (<see cref="WriteParams"/>); if that fails, the parts go
/// back on Elden Ring's rows (<see cref="Revert"/>), so no part names a row that isn't there.</para>
/// <para>DS3's people get their own copies too (<see cref="ForPerson"/>, <see cref="ForPersonThink"/>). A DS3 person
/// (a c0000 human with a DS3 look) stands on an Elden Ring person's NpcParam and NpcThinkParam rows, and up to
/// 1.0.636 those carried Elden Ring's team and senses: at Firelink Shrine, where DS3 puts all 14 people it places on
/// team 26 (friendly NPC), the stand-ins for Horace, Orbeck and Patches were on Elden Ring's team 27 (hostile NPC:
/// Witch-Hunter Jerren, Sorceress Sellen), two on 2 (white phantom) and three on 0, and the Sword Master stood on
/// Vargram the Raging Wolf's invader AI, which hunts the player 40 m away and through "enemies ignore player"
/// (map-diag people, 2026-11-25). The copy at <see cref="PersonRowBase"/> + DS3's row ID takes DS3's teamType
/// (DS3 and Elden Ring number teams the same: DarkScript3's ds3-common and er-common "Team Type" enums), and the
/// think copy takes DS3's senses and territory the way a ported character's row does (<see cref="Enemies.AiPort.CopyDs3Think"/>).</para>
/// </summary>
public sealed class QuietStandIns
{
    /// <summary>The copies' NpcParam IDs: this + Elden Ring's row ID (Elden Ring's rows are all below 1e9).</summary>
    public const int RowBase = 1_000_000_000;

    private readonly PARAM _npc;
    private readonly long _quiet;
    private readonly ConcurrentDictionary<int, int> _copies = new();
    private readonly ConcurrentDictionary<int, long> _levels = new();
    private readonly Dictionary<int, (int From, int Team)> _people = new();
    private readonly Dictionary<int, (int From, int Ds3)> _thinks = new();

    /// <summary>The people copies' NpcParam / NpcThinkParam IDs: this + DS3's row ID (+ a multiple of
    /// <see cref="PersonStep"/> when one DS3 row stands on two Elden Ring rows).</summary>
    public const int PersonRowBase = 1_500_000_000;
    public const int PersonStep = 100_000_000;

    /// <summary>DS3's NpcThinkParam, for the people's think copies (null: they keep the stand-in's think row).</summary>
    public PARAM? Ds3Think { get; set; }

    private QuietStandIns(PARAM npc, long quiet)
    {
        _npc = npc;
        _quiet = quiet;
    }

    /// <summary>Null when Elden Ring's NpcParam (or its threatLv field) couldn't be read.</summary>
    public static QuietStandIns? Create(PARAM? erNpc) =>
        erNpc is not null && Enemies.ChrPort.MusicThreatLevel(erNpc) is { } quiet ? new QuietStandIns(erNpc, quiet) : null;

    /// <summary>The level that starts no music.</summary>
    public long QuietLevel => _quiet;

    /// <summary>Elden Ring's row -> its quiet copy, for every stand-in row with another level.</summary>
    public IReadOnlyDictionary<int, int> Copies => _copies;

    /// <summary>
    /// The row a stand-in is placed on: <paramref name="npcParam"/> when its level is the quiet one (or it can't be
    /// read), otherwise its quiet copy's ID.
    /// </summary>
    public int For(int npcParam)
    {
        if (npcParam <= 0 || npcParam >= RowBase || _npc[npcParam] is not { } row || Level(row) is not { } level || level == _quiet)
            return npcParam;
        _levels.TryAdd(npcParam, level);
        return _copies.GetOrAdd(npcParam, id => RowBase + id);
    }

    /// <summary>
    /// The row a DS3 person standing on Elden Ring's row <paramref name="erRow"/> is placed on: a copy of it with DS3's
    /// team <paramref name="team"/> and the quiet threat level, at <see cref="PersonRowBase"/> + DS3's row.
    /// </summary>
    public int ForPerson(int erRow, int ds3Row, int team)
    {
        if (erRow <= 0 || erRow >= RowBase || ds3Row <= 0 || ds3Row >= PersonStep || team < 0 || _npc[erRow] is null)
            return For(erRow);
        lock (_people)
            return Claim(_people, ds3Row, (From: erRow, Team: team), v => v.From == erRow && v.Team == team);
    }

    /// <summary>
    /// The think row a DS3 person is placed on: a copy of the stand-in's row <paramref name="erThink"/> with DS3's
    /// senses from DS3's row <paramref name="ds3Think"/>; the stand-in's own row when DS3's can't be read.
    /// </summary>
    public int ForPersonThink(int erThink, int ds3Think)
    {
        if (Ds3Think?[ds3Think] is null || erThink <= 0 || erThink >= RowBase || ds3Think <= 0 || ds3Think >= PersonStep)
            return erThink;
        lock (_thinks)
            return Claim(_thinks, ds3Think, (From: erThink, Ds3: ds3Think), v => v.From == erThink);
    }

    private static int Claim<T>(Dictionary<int, T> rows, int ds3Row, T value, Func<T, bool> same)
    {
        for (int id = PersonRowBase + ds3Row; ; id += PersonStep)
        {
            if (!rows.TryGetValue(id, out var have))
            {
                rows[id] = value;
                return id;
            }
            if (same(have))
                return id;
            if (id > int.MaxValue - PersonStep)
                throw new InvalidDataException($"no free people copy ID for DS3 row {ds3Row}");
        }
    }

    /// <summary>Whether there is anything to write.</summary>
    public bool HasRows => !_copies.IsEmpty || _people.Count > 0 || _thinks.Count > 0;

    public static long? Level(PARAM.Row row) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals("threatLv", StringComparison.OrdinalIgnoreCase)) is { Value: not null and not byte[] } cell
            ? Convert.ToInt64(cell.Value) : null;

    /// <summary>Adds the quiet copies to <paramref name="regulation"/>; returns the report line.</summary>
    public string WriteParams(ParamStore regulation)
    {
        var line = Write(regulation.Get("NpcParam") ?? throw new InvalidDataException("no NpcParam"));
        if (_people.Count > 0 || _thinks.Count > 0)
            line += Environment.NewLine + "  " + WritePeople(regulation.Get("NpcParam")!, regulation.Get("NpcThinkParam"));
        return line;
    }

    /// <summary>Adds the people copies (DS3's team, DS3's senses) to the mod's NpcParam and NpcThinkParam.</summary>
    public string WritePeople(PARAM npc, PARAM? think)
    {
        int rows = 0, thinks = 0;
        var teams = new SortedDictionary<string, int>();
        foreach (var (to, (from, team)) in _people.OrderBy(p => p.Key))
        {
            var source = npc[from] ?? throw new InvalidDataException($"Elden Ring row {from} is missing from the mod's regulation");
            var before = PeopleArmour.Cell(source, "teamType");
            if (npc[to] is null)
            {
                var row = Clone(source, to, npc.AppliedParamdef);
                if (!PeopleArmour.SetCell(row, "teamType", team) && PeopleArmour.Cell(row, "teamType") != team)
                    throw new InvalidDataException("NpcParam has no teamType");
                if (row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals("threatLv", StringComparison.OrdinalIgnoreCase)) is { Value: not null and not byte[] } threat)
                    threat.Value = Convert.ChangeType(_quiet, threat.Value.GetType());
                row.Name = $"[DS3 person] {source.Name}";
                npc.Rows.Add(row);
                rows++;
            }
            var key = $"{before}->{team}";
            teams[key] = teams.GetValueOrDefault(key) + 1;
        }
        npc.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        if (think is not null)
        {
            foreach (var (to, (from, ds3)) in _thinks.OrderBy(t => t.Key))
            {
                if (think[to] is not null)
                    continue;
                var source = think[from] ?? throw new InvalidDataException($"Elden Ring think row {from} is missing from the mod's regulation");
                var row = Clone(source, to, think.AppliedParamdef);
                if (Ds3Think?[ds3] is { } senses)
                {
                    Enemies.AiPort.CopyDs3Think(think, row, senses);
                    row.Name = $"[DS3 person] {senses.Name}";
                }
                think.Rows.Add(row);
                thinks++;
            }
            think.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        }
        else if (_thinks.Count > 0)
            throw new InvalidDataException("the mod's regulation has no NpcThinkParam");
        return $"people: {rows} NpcParam copies on DS3's teams (Elden Ring team -> DS3 team: {string.Join(", ", teams.Select(t => $"{t.Key} x{t.Value}"))}), "
               + $"{thinks} think copies with DS3's senses";
    }

    private static PARAM.Row Clone(PARAM.Row source, int id, PARAMDEF def)
    {
        var row = new PARAM.Row(id, source.Name ?? "", def);
        for (int i = 0; i < row.Cells.Count; i++)
            row.Cells[i].Value = source.Cells[i].Value is byte[] b ? b.Clone() : source.Cells[i].Value;
        return row;
    }

    /// <summary>Adds the quiet copies to the mod's NpcParam; returns the report line.</summary>
    public string Write(PARAM npc)
    {
        int added = 0, kept = 0;
        var missing = new List<int>();
        foreach (var (from, to) in _copies.OrderBy(c => c.Key))
        {
            if (npc[to] is not null)
            {
                kept++;
                continue;
            }
            if (npc[from] is not { } source)
            {
                missing.Add(from);
                continue;
            }
            var row = new PARAM.Row(to, source.Name ?? "", npc.AppliedParamdef);
            for (int i = 0; i < row.Cells.Count; i++)
                row.Cells[i].Value = source.Cells[i].Value is byte[] b ? b.Clone() : source.Cells[i].Value;
            var threat = row.Cells.First(c => c.Def.InternalName.Equals("threatLv", StringComparison.OrdinalIgnoreCase));
            threat.Value = Convert.ChangeType(_quiet, threat.Value!.GetType());
            row.Name = $"[DS3 stand-in] {source.Name}";
            npc.Rows.Add(row);
            added++;
        }
        npc.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        if (missing.Count > 0)
            throw new InvalidDataException($"Elden Ring rows {string.Join(", ", missing)} are missing from the mod's regulation");
        return $"stand-in battle music: {added} quiet NpcParam copies written (threat level {_quiet}){(kept > 0 ? $", {kept} already there" : "")}"
               + (_copies.Count > 0 ? "; rows: " + string.Join(", ", _copies.OrderBy(c => c.Key).Take(30).Select(c => $"{c.Key} {_npc[c.Key]?.Name} (level {_levels.GetValueOrDefault(c.Key)}) -> {c.Value}")) : "");
    }

    /// <summary>Puts every stand-in back on Elden Ring's own row (the copies couldn't be written). Returns how many.</summary>
    public int Revert(PorterContext ctx, IEnumerable<MapSlots.Slot> slots)
    {
        var back = _copies.ToDictionary(c => c.Value, c => c.Key);
        foreach (var (to, (from, _)) in _people)
            back[to] = from;
        var backThink = _thinks.ToDictionary(t => t.Key, t => t.Value.From);
        int reverted = 0;
        foreach (var slot in slots)
        {
            var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(slot.Er)));
            if (!File.Exists(path))
                continue;
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
            int here = Revert(msb, back);
            foreach (var e in msb.Parts.Enemies.Where(e => backThink.ContainsKey(e.ThinkParamID)))
            {
                e.ThinkParamID = backThink[e.ThinkParamID];
                here++;
            }
            if (here > 0)
                File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
            reverted += here;
        }
        return reverted;
    }

    public static int Revert(MSBE msb, IReadOnlyDictionary<int, int> back)
    {
        int here = 0;
        foreach (var e in msb.Parts.Enemies.Where(e => back.ContainsKey(e.NPCParamID)))
        {
            e.NPCParamID = back[e.NPCParamID];
            here++;
        }
        return here;
    }
}
