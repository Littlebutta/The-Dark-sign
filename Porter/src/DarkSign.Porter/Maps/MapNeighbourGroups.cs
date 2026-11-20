using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Draws a map's stand-ins for a neighbouring map only while that neighbour isn't loaded, the way DS3 swaps them,
/// with as little of Elden Ring's culling as that takes (the "world stand-ins" step, <see cref="MapPorter"/>).
/// <para>
/// Each ported map gets one draw group of its own (<see cref="Word"/>, bit = its place among the ported maps). A
/// stand-in for map N draws only with N's group. Every collision, and every connect collision on it, shows every
/// group except those of the maps standing on it keeps loaded: its own map, and the maps its connect collisions
/// name (Elden Ring loads a neighbour when the player stands on a connect collision; the 1.0.53x playtest's game
/// file log has Lothric Castle, the Grand Archives, Road of Sacrifices and Irithyll loaded that way, without a warp).
/// So standing anywhere in the High Wall away from the castle, the castle's stand-ins show; on the collisions that
/// load the castle, and anywhere in the castle itself, they don't, and the castle's real pieces stand there instead.
/// </para>
/// <para>
/// The group bits are those of <see cref="MapDrawGroups"/> (Smithbox's reading of the part struct, checked on vanilla
/// Stormveil): a part with draw groups is drawn while the collision the player stands on shows one of them. Every
/// other part keeps no groups and is drawn from everywhere, as before, and collisions keep every draw group, so this
/// can hide nothing but the stand-ins. Whether Elden Ring tests a loaded neighbour's parts against the player's
/// collision in the other map, as DS3 does, is unproven: if it doesn't, a map's stand-ins for its neighbour still
/// hide on its own collisions that load the neighbour, and the neighbour's stand-ins for it may still show.
/// </para>
/// </summary>
public static class MapNeighbourGroups
{
    /// <summary>The group word used: the last of the eight, which no ported part uses with DS3's culling off.</summary>
    public const int Word = 7;

    /// <summary>Each ported Elden Ring map's bit in <see cref="Word"/> (at most 32 maps; the rest get none).</summary>
    public static Dictionary<MapId, int> Assign(IEnumerable<MapId> maps) =>
        maps.Distinct().OrderBy(m => m.ToString()).Take(32).Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);

    /// <summary>The bits of the maps loaded while standing on a collision of <paramref name="own"/> that connects to <paramref name="connects"/>.</summary>
    public static uint LoadedBits(MapId own, IEnumerable<MapId> connects, IReadOnlyDictionary<MapId, int> bits)
    {
        uint loaded = 0;
        foreach (var m in connects.Prepend(own))
            if (bits.TryGetValue(m, out var b))
                loaded |= 1u << b;
        return loaded;
    }

    /// <summary>
    /// Writes the groups into one ported layout: <paramref name="standIns"/> (part name -> the neighbour it stands
    /// for) draw with their neighbour's bit, and every collision shows every bit but the loaded maps'. Returns the
    /// number of collisions written.
    /// </summary>
    public static int Apply(MSBE msb, MapId own, IReadOnlyDictionary<string, MapId> standIns, IReadOnlyDictionary<MapId, int> bits)
    {
        foreach (var piece in msb.Parts.MapPieces)
            if (standIns.TryGetValue(piece.Name, out var neighbour) && bits.TryGetValue(neighbour, out var bit) && piece.Unk1 is { } u)
            {
                var draw = new uint[MapDrawGroups.Words];
                draw[Word] = 1u << bit;
                MapDrawGroups.Set(u, ReadOnlySpan<uint>.Empty, draw);
            }
        var connects = msb.Parts.ConnectCollisions
            .GroupBy(c => c.CollisionName ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => new MapId(c.MapID[0], c.MapID[1] == 255 ? 0 : c.MapID[1], c.MapID[2] == 255 ? 0 : c.MapID[2], 0)).ToList(),
                StringComparer.Ordinal);
        int written = 0;
        foreach (var collision in msb.Parts.Collisions)
        {
            var loaded = LoadedBits(own, connects.GetValueOrDefault(collision.Name) ?? [], bits);
            Show(collision.Unk1, ~loaded);
            foreach (var connect in msb.Parts.ConnectCollisions.Where(c => c.CollisionName == collision.Name))
                Show(connect.Unk1, ~loaded);
            written++;
        }
        return written;
    }

    /// <summary>A collision shows every group, with <paramref name="lastWord"/> in <see cref="Word"/>; it keeps every draw group (always there).</summary>
    private static void Show(MSBE.Part.UnkStruct1? u, uint lastWord)
    {
        if (u is null)
            return;
        MapDrawGroups.SetAll(u);
        MapDrawGroups.Display(u)[Word] = lastWord;
        MapDrawGroups.AltDisplay(u)[Word] = lastWord;
    }

    /// <summary>Whether a stand-in drawn with <paramref name="bit"/> shows on a collision showing <paramref name="display"/> in <see cref="Word"/>.</summary>
    public static bool Shows(int bit, uint display) => (display & (1u << bit)) != 0;
}
