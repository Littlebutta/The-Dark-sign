using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's terrain blends (M[ARSN]_2m/_3m/_4m, MultiBlend) store each vertex normal as four half floats (layout type
/// Half4), where every other DS3 map mesh uses four bytes. SoulsFormats reads a Half4 normal as unsigned shorts
/// scaled to -1..1, so every terrain blend came out of the reader with a normal that points nowhere near its surface:
/// lit from the wrong side, the High Wall's earth between its paving stones, the ground around Iudex Gundyr and every
/// other DS3 ground painted with several textures drew several times darker than the stones on it (Devin, 2026-11-19).
/// <para>
/// Measured on m40's m000080 (the 4m ground at Gundyr's circle, 734 vertices) and m000051 (15,544 triangles), read
/// with SoulsFormats: the normals as read average length 0.87 and point down (Y -0.53) on ground whose triangles face
/// up. Taking the same 16 bits as half floats gives unit length on every vertex, and the normal lies within 60 degrees
/// of its triangle's own facing on 1,187 of 1,187 and 15,479 of 15,544 triangles (the rest are smoothed corners).
/// The one-byte normals of the plain meshes beside them were already right.
/// </para>
/// <para>
/// So before a DS3 map model is converted, each Half4 normal is turned back into the bits SoulsFormats read
/// ((x + 1) * 32767, exact for every 16-bit value) and those bits are read as a half float. The converted mesh is
/// written in its Elden Ring template's own layout, so nothing writes Half4 back.
/// </para>
/// </summary>
public static class Ds3HalfNormals
{
    /// <summary>What <see cref="Fix"/> did, for the port log.</summary>
    public sealed class Stats
    {
        public int Meshes, Vertices, Unreadable;

        public void Add(Stats other)
        {
            Interlocked.Add(ref Meshes, other.Meshes);
            Interlocked.Add(ref Vertices, other.Vertices);
            Interlocked.Add(ref Unreadable, other.Unreadable);
        }

        public string Summary =>
            $"half-float normals: {Meshes} meshes ({Vertices:N0} vertices, DS3's terrain blends) read as half floats"
            + (Unreadable > 0 ? $", {Unreadable:N0} vertices with no usable normal kept as read" : "");
    }

    /// <summary>True when one of the mesh's vertex buffers stores its normal as Half4.</summary>
    public static bool HasHalfNormals(FLVER2 flver, FLVER2.Mesh mesh) =>
        mesh.VertexBuffers.Any(b => b.LayoutIndex >= 0 && b.LayoutIndex < flver.BufferLayouts.Count
            && flver.BufferLayouts[b.LayoutIndex].Any(m => m.Semantic == FLVER.LayoutSemantic.Normal && m.Type == FLVER.LayoutType.Half4));

    /// <summary>The bits SoulsFormats read as an unsigned-short normal component, taken as a half float.</summary>
    public static float AsHalf(float readAsUShortNorm)
    {
        int bits = (int)MathF.Round(readAsUShortNorm * 32767f + 32767f);
        return (float)BitConverter.UInt16BitsToHalf((ushort)Math.Clamp(bits, 0, ushort.MaxValue));
    }

    /// <summary>What SoulsFormats reads for a half float component (the inverse of <see cref="AsHalf"/>), for tests.</summary>
    public static float AsUShortNorm(float value) =>
        (BitConverter.HalfToUInt16Bits((Half)value) - 32767) / 32767f;

    /// <summary>Reads every Half4 normal of a DS3 model as half floats. Call once, on a model as SoulsFormats read it.</summary>
    public static void Fix(FLVER2 flver, Stats stats)
    {
        foreach (var mesh in flver.Meshes)
        {
            if (!HasHalfNormals(flver, mesh))
                continue;
            stats.Meshes++;
            foreach (var v in mesh.Vertices)
            {
                if (v.Normals.Count == 0)
                    continue;
                var n = v.Normals[0];
                var h = new Vector3(AsHalf(n.X), AsHalf(n.Y), AsHalf(n.Z));
                float length = h.Length();
                if (!float.IsFinite(length) || length < 0.1f)
                {
                    stats.Unreadable++;
                    continue;
                }
                v.Normals[0] = h / length;
                stats.Vertices++;
            }
        }
    }
}
