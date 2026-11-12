using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's culling ("draw groups") carried into Elden Ring's part struct.
///
/// Both games hide map parts the same way: each collision lists the groups that are shown while the player stands on
/// it, and a part is drawn only while one of its own groups is shown. DS3 names these piece.DrawGroups and
/// collision.DispGroups (Smithbox's DS3 MSB meta; measured on m31: a piece is drawn from a median 10 of the map's 93
/// collisions, and standing on one collision shows a median 655 of the map's 3,782 pieces).
///
/// Elden Ring keeps the groups in a 0xC8-byte struct that the porter's SoulsFormatsNEXT reads as three arrays,
/// "DisplayGroups" (8 words), "DrawGroups" (8) and "CollisionMask" (32), none of them understood. Smithbox's MSBE
/// (vawser/Smithbox, src/Andre/SoulsFormats/.../MSBE/PartsParam.cs, DisplayDataStruct; documented in
/// Smithbox.Data/Assets/MSB/ER/Meta/Part/Part.xml) decodes the same bytes as six 8-word arrays:
/// <list type="number">
/// <item>DisplayGroups: the groups shown while the player stands on this collision (SoulsFormatsNEXT DisplayGroups);</item>
/// <item>AltDisplayGroups (SoulsFormatsNEXT "DrawGroups");</item>
/// <item>AltDisplayGroupsMask (CollisionMask words 0-7);</item>
/// <item>DrawGroups: the part is drawn while any of these is shown (CollisionMask words 8-15);</item>
/// <item>AltDrawGroups (CollisionMask words 16-23);</item>
/// <item>AltDrawGroupsMask (CollisionMask words 24-31);</item>
/// </list>
/// then NoAltGroupMerge (Condition1) and UseAltGroups (Condition2): with UseAltGroups set, the game either swaps in the
/// alternate groups or merges them in through the masks, depending on a game state.
///
/// Checked on vanilla Stormveil (m10_00_00_00) with this reading: every one of its 223 map pieces that has draw groups
/// is drawn from at least one collision (a median 16 of 111). Read the other way, the way the "map-groups" B fallback
/// wrote DS3's masks up to 1.0.4xx (DS3 draw groups into the first array, display groups into the second), 105 of
/// Stormveil's pieces would never be drawn, and the ported pieces' real draw groups stayed empty, so B never culled.
///
/// Parts are written so that both readings of the alternate groups give the same result: the alternate arrays are
/// copies of the main ones and the masks keep every bit, with UseAltGroups set as 340 of Stormveil's 343 pieces have it.
/// </summary>
public static class MapDrawGroups
{
    public const int Words = 8;

    public static Span<uint> Display(MSBE.Part.UnkStruct1 u) => u.DisplayGroups.AsSpan(0, Words);
    public static Span<uint> AltDisplay(MSBE.Part.UnkStruct1 u) => u.DrawGroups.AsSpan(0, Words);
    public static Span<uint> AltDisplayMask(MSBE.Part.UnkStruct1 u) => u.CollisionMask.AsSpan(0, Words);
    public static Span<uint> Draw(MSBE.Part.UnkStruct1 u) => u.CollisionMask.AsSpan(Words, Words);
    public static Span<uint> AltDraw(MSBE.Part.UnkStruct1 u) => u.CollisionMask.AsSpan(2 * Words, Words);
    public static Span<uint> AltDrawMask(MSBE.Part.UnkStruct1 u) => u.CollisionMask.AsSpan(3 * Words, Words);

    /// <summary>
    /// Writes a part's groups: <paramref name="display"/> (what standing on this collision shows; DS3 DispGroups) and
    /// <paramref name="draw"/> (when this part is drawn; DS3 DrawGroups). A part with neither carries no masks at all,
    /// as JortPob's parts and the colosseums' own have them, and is drawn from everywhere.
    /// </summary>
    public static void Set(MSBE.Part.UnkStruct1? u, ReadOnlySpan<uint> display, ReadOnlySpan<uint> draw)
    {
        if (u is null)
            return;
        Clear(u);
        bool any = false;
        for (int i = 0; i < Words; i++)
        {
            uint d = i < display.Length ? display[i] : 0, w = i < draw.Length ? draw[i] : 0;
            Display(u)[i] = AltDisplay(u)[i] = d;
            Draw(u)[i] = AltDraw(u)[i] = w;
            any |= d != 0 || w != 0;
        }
        if (!any)
            return;
        AltDisplayMask(u).Fill(0xFFFFFFFF);
        AltDrawMask(u).Fill(0xFFFFFFFF);
        u.Condition1 = 0;  // NoAltGroupMerge
        u.Condition2 = 1;  // UseAltGroups, as vanilla parts with groups have it (the alternate groups equal the main ones)
    }

    /// <summary>Every group shown and every group drawn: the part is drawn from everywhere and a collision shows everything.</summary>
    public static void SetAll(MSBE.Part.UnkStruct1? u)
    {
        if (u is null)
            return;
        Array.Fill(u.DisplayGroups, 0xFFFFFFFF);
        Array.Fill(u.DrawGroups, 0xFFFFFFFF);
        Array.Fill(u.CollisionMask, 0xFFFFFFFF);
    }

    public static void Clear(MSBE.Part.UnkStruct1? u)
    {
        if (u is null)
            return;
        Array.Clear(u.DisplayGroups);
        Array.Clear(u.DrawGroups);
        Array.Clear(u.CollisionMask);
        u.Condition1 = 0;
        u.Condition2 = 0;
    }

    /// <summary>Whether a part with these draw groups is drawn while the player stands on a collision showing <paramref name="display"/>.</summary>
    public static bool DrawnFrom(ReadOnlySpan<uint> draw, ReadOnlySpan<uint> display)
    {
        if (IsEmpty(draw))
            return true;
        for (int i = 0; i < Math.Min(draw.Length, display.Length); i++)
            if ((draw[i] & display[i]) != 0)
                return true;
        return false;
    }

    public static bool IsEmpty(ReadOnlySpan<uint> groups)
    {
        foreach (var g in groups)
            if (g != 0)
                return false;
        return true;
    }

    /// <summary>
    /// How much of a DS3 map one collision shows, by DS3's own groups: the port log's "draw groups" line, the measure
    /// of how much less the game draws with culling on (RULE 4). Counts and vertices are of the parts given.
    /// </summary>
    public static string Summary(MSB3 ds3, IReadOnlyCollection<MSB3.Part.MapPiece> pieces, Func<string, long>? verticesOf = null)
    {
        var collisions = ds3.Parts.Collisions;
        if (collisions.Count == 0 || pieces.Count == 0)
            return "draw groups: no collisions or pieces to measure";
        long total = verticesOf is null ? 0 : pieces.Sum(p => verticesOf(p.ModelName));
        var shown = new List<int>();
        var shownVertices = new List<long>();
        foreach (var c in collisions)
        {
            int n = 0;
            long v = 0;
            foreach (var p in pieces)
                if (DrawnFrom(p.DrawGroups, c.DispGroups))
                {
                    n++;
                    if (verticesOf is not null)
                        v += verticesOf(p.ModelName);
                }
            shown.Add(n);
            shownVertices.Add(v);
        }
        shown.Sort();
        shownVertices.Sort();
        int never = pieces.Count(p => !IsEmpty(p.DrawGroups) && !collisions.Any(c => DrawnFrom(p.DrawGroups, c.DispGroups)));
        int everywhere = pieces.Count(p => IsEmpty(p.DrawGroups));
        var line = $"draw groups (DS3's culling): standing on one of {collisions.Count} collisions shows a median {shown[shown.Count / 2]} "
                   + $"of {pieces.Count} pieces ({100.0 * shown[shown.Count / 2] / pieces.Count:F0}%), at most {shown[^1]} ({100.0 * shown[^1] / pieces.Count:F0}%)";
        if (verticesOf is not null && total > 0)
            line += $"; vertices drawn median {shownVertices[shownVertices.Count / 2]:N0} of {total:N0} ({100.0 * shownVertices[shownVertices.Count / 2] / total:F0}%), "
                    + $"at most {shownVertices[^1]:N0} ({100.0 * shownVertices[^1] / total:F0}%)";
        line += $"; {everywhere} pieces have no group (drawn from everywhere), {never} are shown by no collision";
        return line;
    }
}
