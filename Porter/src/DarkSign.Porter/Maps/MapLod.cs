using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's own lower detail models. DS3 (like Elden Ring) keeps up to three triangle lists in each mesh of a map model:
/// full detail (face set flags None), LodLevel1 and LodLevel2, each also in a motion blur variant. The game draws the
/// one its PartsDrawParam row picks by distance (<see cref="MapDrawParams.LodRow"/>). Up to 1.0.417 the port kept the
/// lists but never let the game switch to them.
/// <para>
/// A mesh that has no list for the level the game asks for is believed to draw nothing at that distance (FLVER editors
/// offer to copy the full detail list into the LOD slots for this reason), so a model only switches when every one of
/// its meshes can: <see cref="Complete"/> gives a mesh that lacks a level a copy of the next more detailed list
/// (DS3 models with lower detail lists sometimes leave small meshes without them), and models with no lower detail
/// list anywhere keep the row that never switches, at no extra memory.
/// </para>
/// </summary>
public static class MapLod
{
    private const FLVER2.FaceSet.FSFlags Lod1 = FLVER2.FaceSet.FSFlags.LodLevel1, Lod2 = FLVER2.FaceSet.FSFlags.LodLevel2,
        Blur = FLVER2.FaceSet.FSFlags.MotionBlur;

    /// <summary>What <see cref="Complete"/> found and did.</summary>
    public readonly record struct Result(bool HasLod, int Copies, long FullTriangles, long Lod1Triangles, long Lod2Triangles);

    /// <summary>
    /// Whether the FLVER has lower detail lists; if it has, every mesh is given every level (missing levels as copies
    /// of the next more detailed list), so the whole model can switch.
    /// </summary>
    public static Result Complete(FLVER2 flver)
    {
        bool any = flver.Meshes.Any(m => m.FaceSets.Any(f => (f.Flags & (Lod1 | Lod2)) != 0));
        if (!any)
            return default;
        int copies = 0;
        long full = 0, l1 = 0, l2 = 0;
        foreach (var mesh in flver.Meshes)
        {
            foreach (var blur in new[] { (FLVER2.FaceSet.FSFlags)0, Blur })
            {
                var top = mesh.FaceSets.FirstOrDefault(f => f.Flags == blur);
                if (top is null)
                    continue;   // no motion blur variant at all: nothing to complete
                var first = mesh.FaceSets.FirstOrDefault(f => f.Flags == (blur | Lod1));
                if (first is null)
                {
                    first = CopyAs(top, blur | Lod1);
                    mesh.FaceSets.Add(first);
                    copies++;
                }
                if (!mesh.FaceSets.Any(f => f.Flags == (blur | Lod2)))
                {
                    mesh.FaceSets.Add(CopyAs(first, blur | Lod2));
                    copies++;
                }
            }
            bool restarts = mesh.Vertices.Count < 0xFFFF;
            foreach (var set in mesh.FaceSets)
            {
                long n = set.TriangleStrip ? set.Triangulate(restarts).Count / 3 : set.Indices.Count / 3;
                if (set.Flags == 0) full += n;
                else if (set.Flags == Lod1) l1 += n;
                else if (set.Flags == Lod2) l2 += n;
            }
        }
        return new Result(true, copies, full, l1, l2);
    }

    private static FLVER2.FaceSet CopyAs(FLVER2.FaceSet f, FLVER2.FaceSet.FSFlags flags) =>
        new(flags, f.TriangleStrip, f.CullBackfaces, f.Unk06, [.. f.Indices]);

    /// <summary>Running totals for the port log (RULE 4).</summary>
    public sealed class Stats
    {
        private long _models, _withLod, _copies, _full, _l1, _l2;

        public void Add(bool hasLod, Result r)
        {
            Interlocked.Increment(ref _models);
            if (!hasLod)
                return;
            Interlocked.Increment(ref _withLod);
            Interlocked.Add(ref _copies, r.Copies);
            Interlocked.Add(ref _full, r.FullTriangles);
            Interlocked.Add(ref _l1, r.Lod1Triangles);
            Interlocked.Add(ref _l2, r.Lod2Triangles);
        }

        public string Summary(MapDrawParams.Lod? lod) =>
            $"lower detail: {_withLod} of {_models} model files carry DS3's lower detail lists ({_full:N0} triangles at full detail, "
            + $"{_l1:N0} at level 1 ({Percent(_l1)}), {_l2:N0} at level 2 ({Percent(_l2)}); {_copies} lists copied into meshes that lacked a level); "
            + (lod is null ? "not used (--no-lod): every piece is drawn at full detail at any distance"
                : $"their pieces switch to them by distance ({lod}), the rest are drawn at full detail");

        private string Percent(long n) => _full == 0 ? "-" : $"{100.0 * n / _full:F0}% of full";
    }
}
