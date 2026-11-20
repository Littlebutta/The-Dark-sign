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
/// back on Elden Ring's rows (<see cref="Revert"/>), so no part names a row that isn't there. Off with
/// DARKSIGN_ENEMY_THREAT=template (launcher fallback "map-music" C, which keeps the templates' levels too).</para>
/// </summary>
public sealed class QuietStandIns
{
    /// <summary>The copies' NpcParam IDs: this + Elden Ring's row ID (Elden Ring's rows are all below 1e9).</summary>
    public const int RowBase = 1_000_000_000;

    public static bool Enabled => !Enemies.ChrPort.KeepTemplateThreat;

    private readonly PARAM _npc;
    private readonly long _quiet;
    private readonly ConcurrentDictionary<int, int> _copies = new();
    private readonly ConcurrentDictionary<int, long> _levels = new();

    private QuietStandIns(PARAM npc, long quiet)
    {
        _npc = npc;
        _quiet = quiet;
    }

    /// <summary>Null when it is off, or Elden Ring's NpcParam (or its threatLv field) couldn't be read.</summary>
    public static QuietStandIns? Create(PARAM? erNpc) =>
        Enabled && erNpc is not null && Enemies.ChrPort.MusicThreatLevel(erNpc) is { } quiet ? new QuietStandIns(erNpc, quiet) : null;

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

    public static long? Level(PARAM.Row row) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals("threatLv", StringComparison.OrdinalIgnoreCase)) is { Value: not null and not byte[] } cell
            ? Convert.ToInt64(cell.Value) : null;

    /// <summary>Adds the quiet copies to <paramref name="regulation"/>; returns the report line.</summary>
    public string WriteParams(ParamStore regulation) =>
        Write(regulation.Get("NpcParam") ?? throw new InvalidDataException("no NpcParam"));

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
        int reverted = 0;
        foreach (var slot in slots)
        {
            var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(slot.Er)));
            if (!File.Exists(path))
                continue;
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
            int here = Revert(msb, back);
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
