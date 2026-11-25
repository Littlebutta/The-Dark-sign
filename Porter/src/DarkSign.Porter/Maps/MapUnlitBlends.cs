using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's see-through unlit layers (M[A]_al_NoLight): the cloud bands, haze and far mountain rings that stand around
/// a DS3 sky dome and hide where the dome meets the scenery. Up to 1.0.52x every one was left out ("no Elden Ring
/// material for them yet"), in every map that has them:
/// <list type="bullet">
/// <item>Cemetery of Ash / Firelink (m40): the dome's own horizon cloud band (m009000, sky_50: a ring 6 to 9 km out,
/// vertex alpha 1 at the bottom fading to 0 at 2.4 km up, the cloud layer that hides the foot of the castle in the
/// distance), the far mountain range below the horizon (m900300, mountain_04_382: a ring 7.7 km out whose texture
/// alpha cuts the peaks out) and the cloud sea under the cliffs (kumo_40).</item>
/// <item>High Wall (m30): two rings of sky_02 around the dome (1.1 to 1.9 km and 1.6 to 3.3 km out, alpha 1 through
/// their middle) and a faint haze (sky_05, alpha at most 0.11), so the far view showed only the dome's own skyline.</item>
/// <item>Undead Settlement (m31): the cloud layer under the settlement (m008501, cloud_03).</item>
/// </list>
/// Measured 2026-11-20 with SoulsFormats on the DS3 files in Work\extract\DS3\map. Their albedo is BC1 (no alpha) or
/// an alpha cut-out (mountain_04_382), and DS3 fades them by the vertex colour alpha (DS3 writes red 1, green 0,
/// blue 1 and the fade in alpha). Elden Ring's unlit map shader blends when its MATBIN says so: vanilla m15_00_865 is
/// M[A]_NoLight with g_BlendMode 2 and no depth write (<see cref="MapModelPort.UnlitBlendParams"/>). The ported
/// mesh keeps DS3's alpha in its vertex colour, as overlays did (<see cref="MapOverlays"/>); since it is unproven that
/// Elden Ring's shader reads that channel, triangles whose three corners are all under <see cref="Cut"/> (at most half
/// visible in DS3) are left out, so a layer can't stand solid where DS3 has faded it away.
/// Layers cut out by opacity masks (M[A]_al_m_NoLight_Opacity, the Painted World's and High Wall's cloud cards) and
/// additive ones (M[A]_add) stay out (<see cref="MapModelPort.Ds3Kind"/>).
/// </summary>
public static class MapUnlitBlends
{
    /// <summary>A triangle whose three corners are all below this vertex alpha is left out.</summary>
    public const float Cut = 0.5f;

    /// <summary>What <see cref="Apply"/> did, for the port log.</summary>
    public sealed class Stats
    {
        public int Meshes, Triangles, TrianglesCut, Empty;
        public readonly List<string> Textures = [];

        public void Add(Stats other)
        {
            Interlocked.Add(ref Meshes, other.Meshes);
            Interlocked.Add(ref Triangles, other.Triangles);
            Interlocked.Add(ref TrianglesCut, other.TrianglesCut);
            Interlocked.Add(ref Empty, other.Empty);
            lock (Textures)
                Textures.AddRange(other.Textures);
        }

        public string Summary =>
            $"see-through sky layers: {Meshes} unlit blended meshes (M[A]_al_NoLight: cloud bands, haze, far mountain rings) drawn "
            + $"with their DS3 fade, {TrianglesCut:N0} of {Triangles:N0} triangles left out where DS3 has them under half visible"
            + (Empty > 0 ? $", {Empty} meshes wholly faded out" : "")
            + (Textures.Count > 0 ? " (" + string.Join(", ", Textures.Distinct().OrderBy(t => t, StringComparer.Ordinal).Take(12)) + ")" : "");
    }

    /// <summary>
    /// Readies every see-through unlit mesh of a DS3 FLVER (still with DS3's materials and vertex colours): leaves out
    /// its wholly faded triangles and returns the meshes whose vertex alpha the ported mesh keeps.
    /// </summary>
    /// <param name="only">The meshes to treat; null for every see-through unlit mesh. Since PR "sky fades" only the ones
    /// <see cref="MapSkyFades"/> can't bake (UVs over <see cref="MapSkyFades.MaxSpan"/> tiles) come here.</param>
    public static HashSet<FLVER2.Mesh> Apply(FLVER2 flver, Stats stats, IReadOnlySet<FLVER2.Mesh>? only = null)
    {
        var keepAlpha = new HashSet<FLVER2.Mesh>();
        foreach (var mesh in flver.Meshes.ToList())
        {
            if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count || mesh.Vertices.Count == 0)
                continue;
            var material = flver.Materials[mesh.MaterialIndex];
            if (MapModelPort.Ds3Kind(material) != "nolight-blend" || (only is not null && !only.Contains(mesh)))
                continue;
            var under = mesh.Vertices.Select(v => MapOverlays.Alpha(v) < Cut).ToArray();
            var (cut, total) = MapOverlays.Keep(mesh, tri => !tri.All(i => under[i]));
            stats.Meshes++;
            stats.Triangles += total;
            stats.TrianglesCut += cut;
            if (MapModelPort.Ds3Texture(material, "albedo") is { } albedo)
                stats.Textures.Add(Path.GetFileNameWithoutExtension(ItemText.Leaf(albedo.Path)));
            if (mesh.Vertices.Count == 0 || mesh.FaceSets.All(f => f.Indices.Count == 0))
            {
                flver.Meshes.Remove(mesh);
                stats.Empty++;
            }
            else
                keepAlpha.Add(mesh);
        }
        return keepAlpha;
    }
}
