using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's blend masks: the grey texture ("g_BlendMaskTexture") a two-layer map material (M[ARSN]_m, _l_m, _l_p_m:
/// GXFlver_ColDifSpcBumpMulMask) mixes its two layers by, per pixel, on a UV set of its own (the last one).
/// <para>
/// Elden Ring ships no such shader: its material list still holds MATBINs that name it (map\matxml\M[ARSN]_m and the
/// like), but none of its 22 shader archives has a GXFlver_ColDifSpcBumpMulMask program, and a mesh given one of
/// those MATBINs is not drawn (Road of Sacrifices' floor, 2026-11-26). Its own layered map shaders keep every layer
/// on one UV set. So a ported two-layer mesh is still cut into one mesh per layer (<see cref="MapOverlays"/>), and
/// this class gives the cut what DS3's shader reads: the mask's value where each vertex lies.
/// </para>
/// <para>
/// A mask is noise far finer than the mesh that carries it (a tile is a few metres, with detail down to
/// centimetres), and a cut can only follow what the vertices sample. So each mesh reads the mask at the detail its
/// triangles can carry (<see cref="Mask.LevelFor"/>), and each level is shifted so the share of the surface above
/// one half stays what it is in the full mask: patches keep DS3's coverage and are as large as the mesh's triangles.
/// </para>
/// </summary>
public sealed class MapMasks
{
    private static readonly ConditionalWeakTable<GameArchives, MapMasks> PerGame = new();

    /// <summary>The masks of one DS3 install (shared by every map of a port).</summary>
    public static MapMasks For(GameArchives ds3) => PerGame.GetValue(ds3, g => new MapMasks(g));

    private readonly GameArchives _ds3;
    private readonly Dictionary<string, Dictionary<string, BinderFile>> _areas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Mask?>> _masks = new(StringComparer.OrdinalIgnoreCase);

    private MapMasks(GameArchives ds3) => _ds3 = ds3;

    /// <summary>The mask a DS3 texture path names, or null when DS3's map texture binders don't hold it.</summary>
    public Mask? Find(string texturePath)
    {
        var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(texturePath));
        if (name.Length == 0)
            return null;
        return _masks.GetOrAdd(name, n => new Lazy<Mask?>(() => Load(n))).Value;
    }

    /// <summary>
    /// As <see cref="Find(string)"/>, for a piece of one DS3 map: a model may name a texture without its area
    /// ("o331303_wall_mask_01" for m33's "m33_00_o331303_wall_mask_01"), which is then looked up in the map's own.
    /// </summary>
    public Mask? Find(string texturePath, MapId map)
    {
        if (Find(texturePath) is { } found)
            return found;
        var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(texturePath));
        var id = map.ToString();
        return name.Length == 0 || id.Length < 6 ? null : Find(id[..6] + "_" + name);
    }

    private Mask? Load(string name)
    {
        // DS3 map textures are named after the area they come from ("m33_00_...").
        if (name.Length < 3 || name[0] is not ('m' or 'M') || !char.IsDigit(name[1]) || !char.IsDigit(name[2]))
            return null;
        Dictionary<string, BinderFile> area;
        lock (_areas)
        {
            var folder = "m" + name[1..3];
            if (!_areas.TryGetValue(folder, out area!))
            {
                area = new Dictionary<string, BinderFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var bhd in MapTextures.AreaBinders(_ds3, folder))
                    foreach (var f in BXF4.Read(_ds3.Read(bhd), _ds3.Read(bhd[..^3] + "bdt")).Files)
                        area.TryAdd(MapTextures.TextureNameOf(f.Name), f);
                _areas[folder] = area;
            }
        }
        if (!area.TryGetValue(name, out var file))
            return null;
        try
        {
            var tex = TPF.Read(DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes) : file.Bytes).Textures.FirstOrDefault();
            if (tex is null)
                return null;
            var (pixels, w, h) = MapSkyFades.Decode(tex.Bytes);
            if (pixels is null || w <= 0 || h <= 0)
                return null;
            var grey = new float[w * h];
            for (int i = 0; i < grey.Length; i++)
                grey[i] = pixels[i].r / 255f;
            return new Mask(grey, w, h);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>One blend mask: its grey values at every detail level, each keeping the full mask's coverage.</summary>
    public sealed class Mask
    {
        /// <summary>No level is coarser than this many texels across: a patch pattern is still there.</summary>
        public const int CoarsestSize = 8;

        private readonly List<(float[] Grey, int Width, int Height)> _levels = [];

        /// <summary>The share of the full mask at one half or more (the second layer's share of a surface).</summary>
        public float Coverage { get; }

        public Mask(float[] grey, int width, int height)
        {
            Coverage = grey.Count(g => g >= 0.5f) / (float)grey.Length;
            _levels.Add((grey, width, height));
            while (width > CoarsestSize && height > CoarsestSize && width % 2 == 0 && height % 2 == 0)
            {
                int w = width / 2, h = height / 2;
                var half = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        half[y * w + x] = (grey[2 * y * width + 2 * x] + grey[2 * y * width + 2 * x + 1]
                                           + grey[(2 * y + 1) * width + 2 * x] + grey[(2 * y + 1) * width + 2 * x + 1]) / 4;
                // Averaging pulls every value toward the mean, so fewer (or more) texels pass one half; the level
                // is shifted so the same share passes as in the full mask.
                if (Coverage is > 0 and < 1)
                {
                    var sorted = (float[])half.Clone();
                    Array.Sort(sorted);
                    // Between the last texel that stays under and the first that passes.
                    int pass = Math.Clamp((int)MathF.Round((1 - Coverage) * sorted.Length), 1, sorted.Length - 1);
                    float at = (sorted[pass - 1] + sorted[pass]) / 2;
                    for (int i = 0; i < half.Length; i++)
                        half[i] = Math.Clamp(half[i] - at + 0.5f, 0, 1);
                }
                _levels.Add((half, w, h));
                (grey, width, height) = (half, w, h);
            }
        }

        public int Levels => _levels.Count;

        /// <summary>The finest level whose texels are at least <paramref name="step"/> wide (in UV: 1 = one tile).</summary>
        public int LevelFor(float step)
        {
            int level = 0;
            while (level + 1 < _levels.Count && 1f / _levels[level].Width < step)
                level++;
            return level;
        }

        /// <summary>The mask at a UV (bilinear, tiling), 0..1.</summary>
        public float Sample(Vector2 uv, int level)
        {
            var (p, w, h) = _levels[Math.Clamp(level, 0, _levels.Count - 1)];
            float fx = (uv.X - MathF.Floor(uv.X)) * w - 0.5f, fy = (uv.Y - MathF.Floor(uv.Y)) * h - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            float At(int x, int y) => p[(((y % h) + h) % h) * w + (((x % w) + w) % w)];
            return float.Lerp(float.Lerp(At(x0, y0), At(x0 + 1, y0), tx), float.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), tx), ty);
        }
    }
}
