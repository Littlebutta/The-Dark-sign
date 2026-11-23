using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// No Elden Ring field music on a ported DS3 map. Elden Ring picks a map's field music and ambience from the map's
/// MapDefaultInfoParam row (row ID = the map's area and block, m45_00_00_00 -> 45000000): BgmPlaceInfo is the music
/// area, EnvPlaceInfo the ambience area. Row 0 has music area 0, the value of every Limgrave, Weeping Peninsula and
/// Stormhill tile. The map IDs a DS3 map takes beside the colosseums (m19_70 for the Painted World, m38_10-15, m45_03-05)
/// have no row in Elden Ring, and Devin heard Limgrave's music in Friede's boss room (2026-11-20 playtest, read from
/// the mod's and vanilla regulation on his PC: m19_70 has no row, no Sound region in either map file, every enemy
/// there at the quiet threat level, no Elden Ring music line in its scripts). Lothric Castle (m45_03) playing Elden
/// Ring music the same day is the same gap.
/// <para>Every ported map that is built from a template (<see cref="MapSlots.Slot.Template"/>) gets the template's
/// music and ambience areas: the colosseum the slot's files come from, whose own row the colosseum slots already play
/// DS3's music over without Elden Ring field music. A map with no row gets one, a copy of row 0 (what the game gave
/// it so far: fast travel, weather and draw settings stay as they were) with only those two fields changed. A map
/// that has a row (m11_70, m11_72) keeps it with the same two fields changed.</para>
/// </summary>
public static class MapSoundPlace
{
    public const string ParamName = "MapDefaultInfoParam";

    /// <summary>The fields taken from the template's row: the field music area and the ambience area.</summary>
    public static readonly string[] Fields = ["BgmPlaceInfo", "EnvPlaceInfo"];

    /// <summary>A map's MapDefaultInfoParam row: area and block (m19_70_00_00 -> 19700000).</summary>
    public static int RowId(MapId map) => map.Area * 1_000_000 + map.Block * 10_000;

    /// <summary>Gives every templated slot the template's music and ambience areas. Returns the report line.</summary>
    public static string Apply(PARAM param, IEnumerable<MapSlots.Slot> slots)
    {
        var fallback = param[0];
        var changes = new List<string>();
        var problems = new List<string>();
        int added = 0, changed = 0, same = 0;
        foreach (var slot in slots.Where(s => !s.Probe && s.Template is not null).GroupBy(s => s.Er).Select(g => g.First()))
        {
            int to = RowId(slot.Er), from = RowId(slot.Template!.Value);
            if (to == from)
                continue;
            if (param[from] is not { } source)
            {
                problems.Add($"{slot.Er}: the template {slot.Template} has no row {from}");
                continue;
            }
            var row = param[to];
            bool isNew = row is null;
            string was;
            if (row is null)
            {
                if ((fallback ?? source) is not { } basis)
                    continue;
                row = Copy(param, basis, to, $"[DS3] {slot.Name}");
                param.Rows.Add(row);
                was = $"no row (row {basis.ID}: {Describe(basis)})";
            }
            else
                was = Describe(row);
            bool differs = false;
            foreach (var field in Fields)
            {
                var cell = row[field];
                var value = source[field].Value;
                if (!Equals(cell.Value, value))
                {
                    cell.Value = value;
                    differs = true;
                }
            }
            if (isNew)
                added++;
            else if (differs)
                changed++;
            else
                same++;
            changes.Add($"{slot.Er} {slot.Name}: {was} -> {Describe(row)} (as {slot.Template})");
        }
        param.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        if (problems.Count > 0 && changes.Count == 0)
            throw new InvalidDataException(string.Join("; ", problems));
        return $"map music areas: {added} {ParamName} rows added, {changed} changed, {same} already right; Elden Ring field music and ambience of each ported map are its template colosseum's"
               + (changes.Count > 0 ? "\n  " + string.Join("\n  ", changes) : "")
               + (problems.Count > 0 ? "\n  WARN " + string.Join("\n  WARN ", problems) : "");
    }

    private static string Describe(PARAM.Row row) => string.Join(", ", Fields.Select(f => $"{f} {row[f].Value}"));

    private static PARAM.Row Copy(PARAM param, PARAM.Row source, int id, string name)
    {
        var row = new PARAM.Row(id, name, param.AppliedParamdef);
        for (int i = 0; i < row.Cells.Count; i++)
            row.Cells[i].Value = source.Cells[i].Value is byte[] b ? b.Clone() : source.Cells[i].Value;
        return row;
    }
}
