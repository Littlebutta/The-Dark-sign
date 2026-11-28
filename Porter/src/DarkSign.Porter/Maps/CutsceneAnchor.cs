using System.Numerics;
using System.Text.RegularExpressions;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Where a cut of a DS3 cutscene sits in its map. Both games animate a cut around its own origin and give the origin's
/// place in the map on the cut's "offset.sib" event (resource kind 265: a translation and a rotation in radians;
/// Elden Ring's s10_00_0010 has one per cut, read 2026-11-28). DS3 measures that offset from a collision the cut casts
/// (h34_01_00_00_995000 in the Twin Princes' intro), not from the map's origin: its cut files animate the collision at
/// minus the offset, and where the map places that collision away from the origin, the cut moves with it.
/// <para>
/// Most DS3 maps place every collision with the map's own place in DS3's world (Catacombs: 842.8, -340.4, -1406.2,
/// turned 75.4 degrees; Farron, Irithyll, the Cathedral, the Kiln likewise). The port kept the offset as it was and
/// left the collision out of the cast (Elden Ring has no such part), so those cutscenes played hundreds of metres
/// from their place: the Catacombs' goblet scene (s38_00_0000) stood on a bare cliff (1.0.737 playtest, 2026-11-28).
/// Maps placed at the origin (High Wall, the Painted World) were right. The Twin Princes' intro (s34_01_0000) is
/// measured from h995000 at (-17.5, -3.6, -6.3): its cuts played 18.6 m to the side. Checked against DS3's own
/// data: with the collision's place, the Abyss Watchers' intro origin is at height -248.2, the Watchers' own
/// (-248.3); without it, at -11.6.
/// </para>
/// The fix for every cutscene: each cut's offset is moved by the place of the collision its Havok file animates.
/// Elden Ring's own way to name a reference collision (kind 237 "HitPartsSpecified") isn't used: the DS3 collisions
/// cutscenes cast are not parts of the ported map.
/// </summary>
public static partial class CutsceneAnchor
{
    public const int OffsetKind = 265;

    [GeneratedRegex(@"^h(\d\d)_(\d\d)_(\d\d)_(\d\d)_(\d{6})$", RegexOptions.IgnoreCase)]
    private static partial Regex CollisionCastRegex();

    /// <summary>"h995000" for the cast name "h34_01_00_00_995000" when that is a collision of <paramref name="map"/>; else null.</summary>
    public static string? CollisionPart(string castName, MapId map)
    {
        var m = CollisionCastRegex().Match(castName);
        return m.Success && int.Parse(m.Groups[1].Value) == map.Area && int.Parse(m.Groups[2].Value) == map.Block
               && int.Parse(m.Groups[3].Value) == map.Region && int.Parse(m.Groups[4].Value) == map.Index
            ? "h" + m.Groups[5].Value : null;
    }

    /// <summary>A lookup of where a DS3 map places the collisions its cutscenes cast (position, rotation in degrees).</summary>
    public static Func<string, (Vector3 Position, Vector3 Rotation)?> Placed(MSB3 msb, MapId map)
    {
        var byName = msb.Parts.Collisions.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().Position, g.First().Rotation), StringComparer.OrdinalIgnoreCase);
        return cast => CollisionPart(cast, map) is { } part && byName.TryGetValue(part, out var at) ? at : null;
    }

    /// <summary>The names of the things each cut's Havok file animates (its skeletons), by cut folder ("cut0010").</summary>
    public static Dictionary<string, List<string>> PartsOfCuts(BND4 ds3, IReadOnlyDictionary<string, string>? cutFiles)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ds3.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = f.Name.Split('\\');
            if (parts.Length < 2)
                continue;
            string cut = parts[^2], leaf = Path.GetFileNameWithoutExtension(parts[^1]);
            // The file the cut plays, where the timeline says which (DS3 ships standby files no cut plays).
            if (cutFiles is not null && cutFiles.TryGetValue(cut, out var plays) && !plays.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                continue;
            if (result.ContainsKey(cut))
                continue;
            var data = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
            result[cut] = HKX.Read(data, HKX.HKXVariation.HKXDS3, false).DataSection.Objects.OfType<HKX.HKASkeleton>()
                .Select(s => s.Name.GetString()).ToList();
        }
        return result;
    }

    /// <summary>
    /// Moves each cut's offset by the place of the first placed collision the cut animates.
    /// </summary>
    /// <param name="partsOfCut">Cut name -> the part names its Havok file animates.</param>
    /// <param name="placed">Cast name -> where the DS3 map places it, or null (not a collision of this map).</param>
    /// <param name="moved">Collects "h34_01_00_00_995000 at -17.5 -3.6 -6.3" per collision that moved a cut.</param>
    /// <returns>Cuts whose offset moved.</returns>
    public static int Apply(MQB mqb, IReadOnlyDictionary<string, List<string>> partsOfCut,
        Func<string, (Vector3 Position, Vector3 Rotation)?> placed, ISet<string>? moved = null)
    {
        int cuts = 0;
        foreach (var cut in mqb.Cuts)
        {
            if (!partsOfCut.TryGetValue(cut.Name, out var parts))
                continue;
            var anchor = parts.Select(p => (Name: p, At: placed(p))).FirstOrDefault(p => p.At is not null);
            if (anchor.At is not { } at || (at.Position.LengthSquared() < 1e-8f && at.Rotation.LengthSquared() < 1e-8f))
                continue;
            // A collision turned about the vertical turns the cut with it; DS3's maps tilt none (a tilted one moves the cut only).
            bool upright = MathF.Abs(at.Rotation.X) < 0.01f && MathF.Abs(at.Rotation.Z) < 0.01f;
            float turn = upright ? at.Rotation.Y * MathF.PI / 180f : 0f;
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn);
            bool any = false;
            foreach (var ev in cut.Timelines.SelectMany(t => t.Events))
            {
                if (ev.ResourceIndex < 0 || ev.ResourceIndex >= mqb.Resources.Count || mqb.Resources[ev.ResourceIndex].Unk48 != OffsetKind)
                    continue;
                foreach (var t in ev.Transforms)
                {
                    t.Translation = at.Position + Vector3.Transform(t.Translation, rotation);
                    t.Rotation = new Vector3(t.Rotation.X, t.Rotation.Y + turn, t.Rotation.Z);
                    any = true;
                }
            }
            if (!any)
                continue;
            cuts++;
            moved?.Add(FormattableString.Invariant($"{anchor.Name} at {at.Position.X:0.#} {at.Position.Y:0.#} {at.Position.Z:0.#}{(upright ? "" : " (tilted: moved only)")}"));
        }
        return cuts;
    }
}
