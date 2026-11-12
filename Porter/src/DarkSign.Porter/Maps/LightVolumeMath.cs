using System.Numerics;

namespace DarkSign.Porter.Maps;

/// <summary>
/// The arithmetic behind a ported map's baked light volume (<see cref="MapBakedLight"/>): a BC7 block encoder for
/// smooth data, and a field that spreads DS3's light probes over a regular grid of texels.
/// </summary>
public static class LightVolumeMath
{
    private static readonly int[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    private static void SetBits(byte[] block, ref int at, int value, int count)
    {
        for (int i = 0; i < count; i++, at++)
            if (((value >> i) & 1) != 0)
                block[at >> 3] |= (byte)(1 << (at & 7));
    }

    /// <summary>
    /// Encodes 16 RGBA texels (row by row, 4 x 4) as one BC7 mode 6 block: the two endpoints are the corners of the
    /// texels' bounding box (even values, both shared bits 0) and each texel takes the nearest of the 16 steps on the
    /// line between them. Light volumes change slowly from texel to texel, so one line per block loses little.
    /// </summary>
    public static byte[] EncodeBc7Block(ReadOnlySpan<byte> rgba)
    {
        // The line through the texels: their mean and main direction (power iteration on the covariance), so
        // channels that fall while others rise still lie on it. The endpoints are the texels' extremes along it.
        Span<double> mean = stackalloc double[4];
        for (int i = 0; i < 16; i++)
            for (int c = 0; c < 4; c++)
                mean[c] += rgba[i * 4 + c] / 16.0;
        Span<double> cov = stackalloc double[16];
        for (int i = 0; i < 16; i++)
            for (int a = 0; a < 4; a++)
                for (int b = 0; b < 4; b++)
                    cov[a * 4 + b] += (rgba[i * 4 + a] - mean[a]) * (rgba[i * 4 + b] - mean[b]);
        Span<double> axis = stackalloc double[4] { 1, 1, 1, 1 };
        Span<double> next = stackalloc double[4];
        for (int iter = 0; iter < 12; iter++)
        {
            double len = 0;
            for (int a = 0; a < 4; a++)
            {
                next[a] = 0;
                for (int b = 0; b < 4; b++)
                    next[a] += cov[a * 4 + b] * axis[b];
                len += next[a] * next[a];
            }
            if (len <= 1e-12)
                break;
            len = Math.Sqrt(len);
            for (int a = 0; a < 4; a++)
                axis[a] = next[a] / len;
        }
        double tMin = double.MaxValue, tMax = double.MinValue;
        for (int i = 0; i < 16; i++)
        {
            double t = 0;
            for (int c = 0; c < 4; c++)
                t += (rgba[i * 4 + c] - mean[c]) * axis[c];
            tMin = Math.Min(tMin, t);
            tMax = Math.Max(tMax, t);
        }
        // 7-bit endpoints with the shared bit 0: the colour is the 7 bits doubled.
        Span<int> e0 = stackalloc int[4];
        Span<int> e1 = stackalloc int[4];
        for (int c = 0; c < 4; c++)
        {
            e0[c] = Math.Clamp((int)Math.Round((mean[c] + tMin * axis[c]) / 2), 0, 127);
            e1[c] = Math.Clamp((int)Math.Round((mean[c] + tMax * axis[c]) / 2), 0, 127);
        }
        Span<int> indices = stackalloc int[16];
        double dd = 0;
        for (int c = 0; c < 4; c++)
            dd += (double)(e1[c] - e0[c]) * (e1[c] - e0[c]) * 4;
        for (int i = 0; i < 16; i++)
        {
            if (dd <= 0)
            {
                indices[i] = 0;
                continue;
            }
            double t = 0;
            for (int c = 0; c < 4; c++)
                t += (rgba[i * 4 + c] - e0[c] * 2.0) * (e1[c] - e0[c]) * 2;
            t /= dd;
            // The nearest of the 16 weights.
            int best = 0;
            double bestErr = double.MaxValue;
            for (int k = 0; k < 16; k++)
            {
                double err = Math.Abs(Weights4[k] / 64.0 - t);
                if (err < bestErr)
                {
                    bestErr = err;
                    best = k;
                }
            }
            indices[i] = best;
        }
        // The first texel's index has an implied top bit of 0: swap the endpoints when it would be 8 or more.
        if (indices[0] >= 8)
        {
            for (int c = 0; c < 4; c++)
                (e0[c], e1[c]) = (e1[c], e0[c]);
            for (int i = 0; i < 16; i++)
                indices[i] = 15 - indices[i];
        }
        var block = new byte[16];
        int at = 0;
        SetBits(block, ref at, 1 << 6, 7);
        for (int c = 0; c < 4; c++)
        {
            SetBits(block, ref at, e0[c], 7);
            SetBits(block, ref at, e1[c], 7);
        }
        SetBits(block, ref at, 0, 1);
        SetBits(block, ref at, 0, 1);
        SetBits(block, ref at, indices[0], 3);
        for (int i = 1; i < 16; i++)
            SetBits(block, ref at, indices[i], 4);
        return block;
    }

    /// <summary>
    /// Encodes a volume (x fastest, then y, then z; 4 bytes per texel) as BC7 slice by slice, the layout of Elden
    /// Ring's light volume textures: each slice its own 4 x 4 blocks, row by row. Width and height must be multiples of 4.
    /// </summary>
    public static byte[] EncodeBc7Volume(byte[] rgba, int w, int h, int d)
    {
        if (w % 4 != 0 || h % 4 != 0)
            throw new ArgumentException($"volume {w} x {h} is not made of whole 4 x 4 blocks");
        int bw = w / 4, bh = h / 4;
        var output = new byte[bw * bh * d * 16];
        Parallel.For(0, d, z =>
        {
            var texels = new byte[64];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    for (int py = 0; py < 4; py++)
                        for (int px = 0; px < 4; px++)
                        {
                            int src = (((z * h) + by * 4 + py) * w + bx * 4 + px) * 4;
                            Array.Copy(rgba, src, texels, (py * 4 + px) * 4, 4);
                        }
                    EncodeBc7Block(texels).CopyTo(output, ((z * bh + by) * bw + bx) * 16);
                }
        });
        return output;
    }

    /// <summary>
    /// Spreads values known at scattered points (DS3's light probes) over a grid of texels: each texel near points
    /// takes their inverse-distance-weighted mean (within <paramref name="radius"/>), every other texel the value of
    /// the nearest texel that has one (so the open air above a courtyard gets the courtyard's light and the inside of
    /// a cliff gets whatever lies closest). Returns one float[] of <paramref name="channels"/> values per texel, x
    /// fastest, then y, then z.
    /// </summary>
    public static float[] Spread(IReadOnlyList<(Vector3 Position, float[] Values)> points, Vector3 origin, Vector3 texel,
        int w, int h, int d, int channels, float radius)
    {
        var field = new float[w * h * d * channels];
        var known = new bool[w * h * d];
        // A hash of the points by cell of the search radius.
        var cells = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / radius), (int)MathF.Floor(p.Y / radius), (int)MathF.Floor(p.Z / radius));
        for (int i = 0; i < points.Count; i++)
        {
            var key = Cell(points[i].Position);
            if (!cells.TryGetValue(key, out var list))
                cells[key] = list = [];
            list.Add(i);
        }
        float soft = 0.25f * texel.LengthSquared() / 3;
        Parallel.For(0, d, z =>
        {
            var acc = new double[channels];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * texel;
                    var (cx, cy, cz) = Cell(p);
                    Array.Clear(acc);
                    double total = 0;
                    for (int ix = cx - 1; ix <= cx + 1; ix++)
                        for (int iy = cy - 1; iy <= cy + 1; iy++)
                            for (int iz = cz - 1; iz <= cz + 1; iz++)
                            {
                                if (!cells.TryGetValue((ix, iy, iz), out var list))
                                    continue;
                                foreach (var i in list)
                                {
                                    float dist = Vector3.Distance(points[i].Position, p);
                                    if (dist > radius)
                                        continue;
                                    // Inverse square distance, softened so a texel on top of a probe doesn't divide by zero.
                                    double wgt = 1.0 / (dist * dist + soft);
                                    // Fade to zero at the radius, so a texel doesn't jump as a probe leaves its reach.
                                    double fade = 1 - dist / radius;
                                    wgt *= fade * fade;
                                    total += wgt;
                                    var v = points[i].Values;
                                    for (int c = 0; c < channels; c++)
                                        acc[c] += wgt * v[c];
                                }
                            }
                    if (total <= 0)
                        continue;
                    int t = (z * h + y) * w + x;
                    known[t] = true;
                    for (int c = 0; c < channels; c++)
                        field[t * channels + c] = (float)(acc[c] / total);
                }
        });
        FillFromNearest(field, known, w, h, d, channels);
        return field;
    }

    /// <summary>
    /// Gives every texel without a value the value of the nearest texel with one (two sweeps of a chamfer distance
    /// transform that carry the source texel along). A grid with no known texel stays all zero.
    /// </summary>
    public static void FillFromNearest(float[] field, bool[] known, int w, int h, int d, int channels)
    {
        int n = w * h * d;
        var source = new int[n];
        var dist = new float[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            source[i] = known[i] ? i : -1;
            dist[i] = known[i] ? 0 : float.MaxValue;
            any |= known[i];
        }
        if (!any)
            return;
        // The 13 neighbours that come before a texel in x-y-z order; the backward sweep uses them mirrored.
        var offsets = new List<(int dx, int dy, int dz)>();
        for (int dz = -1; dz <= 0; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (dz < 0 || dy < 0 || (dy == 0 && dx < 0))
                        offsets.Add((dx, dy, dz));
        void Visit(int x, int y, int z, int sign)
        {
            int i = (z * h + y) * w + x;
            foreach (var (ox, oy, oz) in offsets)
            {
                int nx = x + ox * sign, ny = y + oy * sign, nz = z + oz * sign;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= w || ny >= h || nz >= d)
                    continue;
                int j = (nz * h + ny) * w + nx;
                if (source[j] < 0)
                    continue;
                int s = source[j];
                int sx = s % w, sy = s / w % h, sz = s / (w * h);
                float dd = (sx - x) * (sx - x) + (sy - y) * (sy - y) + (sz - z) * (sz - z);
                if (dd < dist[i])
                {
                    dist[i] = dd;
                    source[i] = s;
                }
            }
        }
        for (int z = 0; z < d; z++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Visit(x, y, z, 1);
        for (int z = d - 1; z >= 0; z--)
            for (int y = h - 1; y >= 0; y--)
                for (int x = w - 1; x >= 0; x--)
                    Visit(x, y, z, -1);
        for (int i = 0; i < n; i++)
            if (!known[i] && source[i] >= 0)
                Array.Copy(field, source[i] * channels, field, i * channels, channels);
    }
}
