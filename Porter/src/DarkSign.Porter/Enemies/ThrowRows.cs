using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// Backstabs, ripostes and grabs for ported characters (ThrowParam). Both games find a throw by the attacker's and the
/// defender's character IDs (AtkChrId, DefChrId; 0 is the player) and the attack's ThrowTypeID: Elden Ring has
/// 1,597 rows of the player backstabbing or riposting a character (DefChrId = that character) and 189 of a character
/// grabbing the player; DS3 440 and 82 (both games' ThrowParam dumped 2026-11-23). A ported character has a new ID
/// (c7xxx) that no row named, so up to 1.0.58x the player could not backstab or riposte any ported DS3 enemy, and a
/// DS3 grab (Pus of Man, Iudex Gundyr's second phase, hollows, rats) found no row and did nothing.
/// <list type="bullet">
/// <item>Every row of the character's Elden Ring template, with the template's ID replaced by the new one: the
/// player's backstab and riposte work as on the template, with Elden Ring's player animations and the template's
/// reaction slots (which a ported character's animation binder fills with DS3's moves).</item>
/// <item>Every DS3 row of the DS3 character grabbing the player (AtkChrId = the DS3 character, DefChrId 0), for the
/// grab attacks the ported AtkParam rows name by DS3's ThrowTypeID, with the new ID as attacker and DS3's attack
/// animation. The player's side (defAnimId: DS3's 70xxx victim animations, which Elden Ring's player doesn't have
/// under those numbers) is taken from a grab row of the template when it has one, else Elden Ring's most common
/// victim animation, so the player is held and released rather than standing still. A DS3 grab already covered by a
/// template row of the same ThrowTypeID is left to it.</item>
/// </list>
/// Rows go at <see cref="Base"/> + new character ID * 100 + n. DS3's own victim animations for the player are not
/// ported yet; the grab plays Elden Ring's.
/// </summary>
public static class ThrowRows
{
    public const int Base = 950_000_000;

    public sealed record Result(int Copied, int Ds3Grabs, int Characters);

    /// <param name="ported">(template character ID, new character ID, DS3 character ID) of each fully ported character.</param>
    public static Result Port(PARAM er, PARAM? ds3, IEnumerable<(int Template, int New, int Ds3)> ported)
    {
        er.Rows.RemoveAll(r => r.ID >= Base && r.ID < Base + 10_000 * 100);
        var erRows = er.Rows.ToList();
        int defaultVictim = erRows.Where(r => Int(r, "AtkChrId") > 0 && Int(r, "DefChrId") == 0)
            .GroupBy(r => Int(r, "defAnimId")).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(-1);
        int copied = 0, grabs = 0, chars = 0;
        foreach (var (template, chr, ds3Chr) in ported.Distinct())
        {
            int n = 0;
            var added = new List<PARAM.Row>();
            PARAM.Row New(PARAM.Row from)
            {
                var row = new PARAM.Row(Base + chr * 100 + n++, from.Name ?? "", er.AppliedParamdef);
                for (int i = 0; i < row.Cells.Count; i++)
                    row.Cells[i].Value = from.Cells[i].Value is byte[] b ? b.Clone() : from.Cells[i].Value;
                return row;
            }
            var templateRows = erRows.Where(r => Int(r, "AtkChrId") == template || Int(r, "DefChrId") == template).ToList();
            foreach (var source in templateRows)
            {
                if (n >= 100)
                    break;
                var row = New(source);
                if (Int(row, "AtkChrId") == template)
                    Set(row, "AtkChrId", chr);
                if (Int(row, "DefChrId") == template)
                    Set(row, "DefChrId", chr);
                row.Name = $"[DS3 port] {source.Name}";
                added.Add(row);
                copied++;
            }
            var templateGrabTypes = templateRows.Where(r => Int(r, "AtkChrId") == template && Int(r, "DefChrId") == 0).Select(r => Int(r, "throwTypeId")).ToHashSet();
            int victim = templateRows.Where(r => Int(r, "AtkChrId") == template && Int(r, "DefChrId") == 0).Select(r => Int(r, "defAnimId")).FirstOrDefault(defaultVictim);
            if (ds3 is not null)
                foreach (var source in ds3.Rows.Where(r => Int(r, "AtkChrId") == ds3Chr && Int(r, "DefChrId") == 0 && !templateGrabTypes.Contains(Int(r, "throwTypeId"))))
                {
                    if (n >= 100)
                        break;
                    var row = new PARAM.Row(Base + chr * 100 + n++, $"[DS3] {source.Name}", er.AppliedParamdef);
                    var shape = templateRows.FirstOrDefault(r => Int(r, "AtkChrId") == template && Int(r, "DefChrId") == 0)
                                ?? erRows.First(r => Int(r, "AtkChrId") > 0 && Int(r, "DefChrId") == 0);
                    for (int i = 0; i < row.Cells.Count; i++)
                        row.Cells[i].Value = shape.Cells[i].Value is byte[] b ? b.Clone() : shape.Cells[i].Value;
                    // DS3's reach and angles (same names in both games, case aside) and its attack animation.
                    foreach (var field in new[] { "Dist", "DiffAngMin", "DiffAngMax", "UpperYrange", "LowerYrange", "DiffAngMyToDef", "throwTypeId", "atkAnimId", "escHp", "selfEscCycleTime", "throwType" })
                        if (Cell(source, field) is { Value: not null and not byte[] } from && Cell(row, field) is { } to)
                            try { to.Value = Convert.ChangeType(from.Value, to.Value!.GetType()); }
                            catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException) { }
                    Set(row, "AtkChrId", chr);
                    Set(row, "DefChrId", 0);
                    if (victim > 0)
                        Set(row, "defAnimId", victim);
                    added.Add(row);
                    grabs++;
                }
            if (added.Count > 0)
                chars++;
            er.Rows.AddRange(added);
        }
        er.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return new Result(copied, grabs, chars);
    }

    /// <summary>Offline checks on small params built in memory (part of enemy-selftest).</summary>
    public static void SelfTest(Action<string, bool> check)
    {
        static PARAM Param(params string[] names)
        {
            var def = new PARAMDEF();
            foreach (var name in names)
                def.Fields.Add(new PARAMDEF.Field(def, PARAMDEF.DefType.s32, name));
            var param = new PARAM { Rows = [] };
            param.ApplyParamdef(def);
            return param;
        }
        static void Row(PARAM p, int id, int atk, int def, int type, int atkAnim, int defAnim)
        {
            var row = new PARAM.Row(id, "", p.AppliedParamdef);
            row["AtkChrId"].Value = atk; row["DefChrId"].Value = def; row["throwTypeId"].Value = type;
            row["atkAnimId"].Value = atkAnim; row["defAnimId"].Value = defAnim; row["Dist"].Value = 1;
            p.Rows.Add(row);
        }
        string[] erFields = ["AtkChrId", "DefChrId", "Dist", "throwTypeId", "atkAnimId", "defAnimId"];
        var er = Param(erFields);
        Row(er, 10301000, 0, 3010, 20, 40090, 11010);    // the player backstabs the template
        Row(er, 10301001, 3010, 0, 300, 3300, 70120);    // the template grabs the player (type 300)
        Row(er, 10500000, 0, 5000, 20, 40090, 11010);    // another character's row
        var ds3 = Param(["AtkChrId", "DefChrId", "Dist", "throwTypeId", "atkAnimId", "defAnimId"]);
        Row(ds3, 510900, 1090, 0, 4100, 4100, 70810);    // Pus of Man grabs the player (type 4100)
        Row(ds3, 109000, 0, 1090, 0, 30400, 11000);      // the player backstabs it (DS3's row: not used)
        var result = Port(er, ds3, [(3010, 7120, 1090)]);
        var mine = er.Rows.Where(r => r.ID >= Base).ToList();
        check("throws: the template's backstab and grab rows are copied under the new character ID",
            result.Copied == 2 && mine.Any(r => (int)r["DefChrId"].Value == 7120 && (int)r["AtkChrId"].Value == 0 && (int)r["defAnimId"].Value == 11010)
            && mine.Any(r => (int)r["AtkChrId"].Value == 7120 && (int)r["throwTypeId"].Value == 300));
        check("throws: DS3's own grab comes along with its type and attack animation, the player held with the template's victim animation",
            result.Ds3Grabs == 1 && mine.Any(r => (int)r["AtkChrId"].Value == 7120 && (int)r["throwTypeId"].Value == 4100 && (int)r["atkAnimId"].Value == 4100 && (int)r["defAnimId"].Value == 70120));
        check("throws: other characters' rows are untouched and a rerun replaces ours", er.Rows.Count(r => r.ID < Base) == 3
            && Port(er, ds3, [(3010, 7120, 1090)]).Copied == 2 && er.Rows.Count(r => r.ID >= Base) == 3);
    }

    private static PARAM.Cell? Cell(PARAM.Row row, string name) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int Int(PARAM.Row row, string name) =>
        Cell(row, name)?.Value is { } v and not byte[] ? Convert.ToInt32(v) : -1;

    private static void Set(PARAM.Row row, string name, int value)
    {
        if (Cell(row, name) is { Value: { } v } cell)
            try { cell.Value = Convert.ChangeType(value, v.GetType()); }
            catch (Exception e) when (e is InvalidCastException or OverflowException) { }
    }
}
