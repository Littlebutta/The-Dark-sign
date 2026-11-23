using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// DS3's lock-on cameras (LockCamParam): how far back and how wide the camera sits while the player is locked on to
/// a boss (Prince Lothric 5250: 5.4 m back, field of view 43). Two places name a row: a character's NpcParam
/// (lockCamParamId) and the scripts' "Change Camera" (boss arenas). Up to 1.0.57x neither was ported: ported bosses
/// used their Elden Ring template's camera, and 13 of the 16 rows the scripts select don't exist in Elden Ring
/// (census 2026-11-22), the other three are Elden Ring's own.
/// <para>
/// Every DS3 row is copied to <see cref="Base"/> + its ID. The two layouts hold the same values in the same order
/// under different names (Paramdex: DS3 CamDistTarget / FovYChange / MeleeAttack..., Elden Ring camDistTarget /
/// camFovY / close...; 19 value fields), and Elden Ring adds lockTgtKeepTime and chrTransChaseRateForNormal, which
/// keep the values of its row 0. Off with DARKSIGN_LOCK_CAMS=0 (launcher fallback enemy-model R).
/// </para>
/// </summary>
public static class LockCams
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_LOCK_CAMS") != "0";

    public const int Base = 900_000_000;

    public const string TableFile = "darksign_lock_cams.tsv";

    /// <summary>The Elden Ring row of a DS3 row (0 and less stay: the default camera).</summary>
    public static int Id(int ds3) => ds3 > 0 && ds3 < int.MaxValue - Base ? Base + ds3 : ds3;

    /// <summary>Copies every DS3 row into Elden Ring's LockCamParam and returns how many.</summary>
    public static int Port(PARAM er, PARAM ds3)
    {
        er.Rows.RemoveAll(r => r.ID >= Base);
        var template = er[0] ?? er.Rows.First();
        var ds3Values = ValueCells(ds3.AppliedParamdef);
        var erValues = ValueCells(er.AppliedParamdef);
        int n = Math.Min(ds3Values.Count, erValues.Count), added = 0;
        foreach (var source in ds3.Rows.Where(r => r.ID > 0))
        {
            var row = new PARAM.Row(Id(source.ID), $"[DS3] {source.Name}", er.AppliedParamdef);
            for (int i = 0; i < row.Cells.Count; i++)
                row.Cells[i].Value = template.Cells[i].Value is byte[] b ? b.Clone() : template.Cells[i].Value;
            for (int i = 0; i < n; i++)
            {
                var from = source.Cells[ds3Values[i]];
                var to = row.Cells[erValues[i]];
                if (from.Value is null or byte[] || to.Value is null)
                    continue;
                try { to.Value = Convert.ChangeType(from.Value, to.Value.GetType()); }
                catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException) { }
            }
            er.Rows.Add(row);
            added++;
        }
        er.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return added;
    }

    /// <summary>The indices of a def's value fields (not padding), in order.</summary>
    internal static List<int> ValueCells(PARAMDEF def) =>
        [.. def.Fields.Select((f, i) => (f, i)).Where(t => t.f.DisplayType is not (PARAMDEF.DefType.dummy8) && !t.f.InternalName.StartsWith("pad", StringComparison.OrdinalIgnoreCase)).Select(t => t.i)];
}
