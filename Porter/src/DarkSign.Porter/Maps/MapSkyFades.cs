using System.Collections.Concurrent;
using System.Numerics;
using BCnEncoder.Shared;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's see-through sky layers drawn with DS3's fade baked into the texture's alpha. The layers are the cloud bands,
/// haze and far mountain rings around a DS3 sky dome (M[A]_al_NoLight), its additive sun glows (M[A]_add,
/// M[A]_Add_NoLight) and its soft cloud cards (M[A]_al_m_NoLight_Opacity).
/// <para>
/// <b>Why (measured 2026-11-25 on DS3 m30 m009000 and m45 m091020 and on Elden Ring's allmaterial.matbinbnd):</b>
/// DS3 draws these layers with shaders that multiply in a per-vertex fade. That is the vertex colour alpha on the
/// al_NoLight and add meshes (red 1, green 0, blue 1, alpha = fade), and UV channel 3 on the Opacity cards. Their
/// albedos are BC1 with no alpha. Elden Ring's unlit map shader M[A]_NoLight blends only by its albedo's alpha.
/// Vanilla m15_00_865 is M[A]_NoLight with g_BlendMode 2 and an albedo with alpha. It has no vertex-colour input
/// (#258's lookup). So every layer drew as a solid sheet of its RGB. Up to the 1.0.62x builds the port cut the
/// triangles DS3 shows under half visible (<see cref="MapUnlitBlends"/>), which left hard polygon edges.
/// The 2026-11-24 sky audit (reports/sky-audit) saw these on 1.0.628:
/// <list type="bullet">
/// <item>High Wall / Undead Settlement / Lothric Castle: a dark grey disc overhead. On High Wall that is the sky_02
/// ring 2.0 to 3.3 km up, which DS3 shows at alpha 0 to 0.5 (mean 0.2).</item>
/// <item>The Kiln's grey triangle and the Ringed City's flat blue and pink panels.</item>
/// <item>The High Wall horizon seam: its sky_05 haze (alpha at most 0.11) was cut away whole.</item>
/// </list>
/// Baking didn't help either (1.0.636 still drew High Wall's layers solid): no vanilla model draws M[A]_NoLight blended
/// at all. Since 2026-11-25 the cloud bands, haze and glows are drawn by Elden Ring's own DS3-style sky material
/// instead (map\matxml\M[A]_al_NoLight on GXFlver_Col, the shader vanilla effect model s83200 uses with DS3's sky
/// layout; see <see cref="MapModelPort.LegacyBlendMatbin"/>), which multiplies in DS3's vertex fade as DS3 did. They
/// aren't baked then; only the Opacity cards (UV fade and masks) still are, onto that material.
/// </para>
/// <para>
/// <b>What it does:</b> each such mesh gets a texture of its own that covers the mesh's UV range once. Its RGB is the
/// DS3 albedo at DS3's tiling, and its alpha is albedo alpha × the mesh's fade, rasterised in UV space. The Opacity
/// cards also multiply in both opacity masks, at rest (DS3 scrolls them). The mesh's UVs are moved to 0..1 over that
/// texture and its tiling becomes 1 x 1. The layer then draws through the unlit template, blended for al_NoLight and
/// Opacity (<see cref="MapModelPort.UnlitBlendParams"/>) and added for add (<see cref="MapModelPort.UnlitAddParams"/>).
/// Nothing is cut, so the fade stays as soft as DS3's. A mesh whose UVs span more than <see cref="MaxSpan"/> tiles
/// can't be covered by one texture. It keeps the old cut rule (al_NoLight) or is left out (the others), and the log
/// counts it.
/// </para>
/// </summary>
public static class MapSkyFades
{
    /// <summary>The derived textures' names: DS3 albedo + this + a hash of what they're made from.</summary>
    public const string Suffix = "_dkfade_";

    /// <summary>The most tiles of UV range one baked texture covers on either axis.</summary>
    public const float MaxSpan = 8f;

    /// <summary>The fade raster's size (the fade is a vertex interpolation, so it is smooth).</summary>
    public const int FadeSize = 256;

    /// <summary>The DS3 kinds whose fade is baked.</summary>
    public static bool Bakes(string kind) => kind is "nolight-blend" or "nolight-add" or "nolight-opacity";

    /// <summary>How one baked texture is made, from DS3 textures read when the area's textures are written.</summary>
    public sealed record Recipe(string Kind, string Albedo, Vector2 AlbedoScale, string? Mask1, Vector2 Mask1Scale,
        string? Mask2, Vector2 Mask2Scale, Vector2 UvMin, Vector2 UvSpan, byte[] Fade);

    /// <summary>Every baked texture any map port asked for, by name (the names hash their recipe, so maps share them).</summary>
    public static readonly ConcurrentDictionary<string, Recipe> Recipes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="Apply"/> did, for the port log.</summary>
    public sealed class Stats
    {
        public int Meshes, Baked, Empty, TooWide, VertexFaded, Cards;
        public readonly SortedDictionary<string, int> Kinds = new(StringComparer.Ordinal);
        public readonly List<string> Textures = [];

        public void Add(Stats other)
        {
            lock (this)
            {
                Meshes += other.Meshes;
                Baked += other.Baked;
                Empty += other.Empty;
                TooWide += other.TooWide;
                VertexFaded += other.VertexFaded;
                Cards += other.Cards;
                foreach (var (k, n) in other.Kinds)
                    Kinds[k] = Kinds.GetValueOrDefault(k) + n;
                Textures.AddRange(other.Textures);
            }
        }

        public string Summary =>
            $"sky fades: {Meshes} see-through sky meshes ({string.Join(", ", Kinds.Select(k => $"{k.Key} {k.Value}"))}); "
            + (VertexFaded > 0 ? $"{VertexFaded} drawn by Elden Ring's legacy sky material ({MapModelPort.LegacyBlendMatbin}) with DS3's own vertex fade, " : "")
            + $"{Baked} drawn with DS3's fade baked into a texture of their own, {Empty} wholly faded out in DS3 and left out"
            + (Cards > 0 ? $", {Cards} soft cloud cards (M[A]_al_m_NoLight_Opacity) left out" : "")
            + (TooWide > 0 ? $", {TooWide} with UVs over {MaxSpan:0} tiles (al_NoLight keeps the half-visible cut, others left out)" : "")
            + (Textures.Count > 0 ? " (" + string.Join(", ", Textures.Distinct().OrderBy(t => t, StringComparer.Ordinal).Take(16)) + ")" : "");
    }

    /// <summary>
    /// The fade DS3 multiplies into a vertex of a layer of this kind: the vertex colour alpha, or for an Opacity card
    /// the first value of its UByte4Norm UV member (m45 m091020: UV3 = fade, fade, 0, 0 after three float UVs), at
    /// <paramref name="fadeUv"/> in the vertex's UV list.
    /// </summary>
    public static float Fade(FLVER.Vertex v, string kind, int fadeUv) =>
        kind == "nolight-opacity" ? (fadeUv >= 0 && v.UVs.Count > fadeUv ? Math.Clamp(v.UVs[fadeUv].X, 0f, 1f) : 1f)
        : Math.Clamp(MapOverlays.Alpha(v), 0f, 1f);

    /// <summary>Where a mesh's UByte4Norm UV member starts in its vertices' UV lists (SoulsFormats lists UVs in layout order), or -1.</summary>
    public static int FadeUvIndex(FLVER2 flver, FLVER2.Mesh mesh)
    {
        int index = 0;
        foreach (var member in mesh.VertexBuffers.Where(b => b.LayoutIndex >= 0 && b.LayoutIndex < flver.BufferLayouts.Count)
                     .SelectMany(b => flver.BufferLayouts[b.LayoutIndex]).Where(m => m.Semantic == FLVER.LayoutSemantic.UV))
        {
            if (member.Type == FLVER.LayoutType.UByte4Norm)
                return index;
            index += MapModelPort.UvCount(member.Type);
        }
        return -1;
    }

    /// <summary>
    /// Bakes every see-through sky mesh of a DS3 FLVER (still with DS3's materials, vertex colours and UVs). Each
    /// baked mesh gets its own copy of its material, naming its derived texture. Returns the al_NoLight meshes too
    /// wide to bake, which are left to <see cref="MapUnlitBlends"/>.
    /// </summary>
    /// <param name="vertexFade">The kinds drawn by <see cref="MapModelPort.LegacyBlendMatbin"/>, which multiplies in the
    /// vertex colour alpha as DS3 does: those meshes aren't baked. They keep DS3's texture and fade, their UVs take the
    /// material's tiling (set to 1 x 1), and they're added to <paramref name="keepFade"/>. Null bakes every kind.</param>
    public static HashSet<FLVER2.Mesh> Apply(FLVER2 flver, Stats stats, Func<string, bool>? vertexFade = null,
        HashSet<FLVER2.Mesh>? keepFade = null)
    {
        var tooWide = new HashSet<FLVER2.Mesh>();
        var tiled = new HashSet<FLVER2.Material>();
        foreach (var mesh in flver.Meshes.ToList())
        {
            if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= flver.Materials.Count || mesh.Vertices.Count == 0)
                continue;
            var material = flver.Materials[mesh.MaterialIndex];
            var kind = MapModelPort.Ds3Kind(material);
            if (!Bakes(kind))
                continue;
            stats.Meshes++;
            stats.Kinds[kind] = stats.Kinds.GetValueOrDefault(kind) + 1;
            // DS3's soft cloud cards (M[A]_al_m_NoLight_Opacity: Painted World m091020-22 are 500-1300 m wide and up to
            // 416 m tall; kumo_50 cards on High Wall, Lothric Castle, Cathedral, Irithyll, Dungeon) are drawn by DS3's
            // Fujir_Nolight_OpacityBlend shader, which Elden Ring doesn't have. Baked (#284) and drawn by the legacy
            // sky material they showed as solid cloud walls with hard outlines (Devin, 1.0.69x Painted World, "cloud
            // tower"). Left out on every map, as before #284.
            if (kind == "nolight-opacity")
            {
                flver.Meshes.Remove(mesh);
                stats.Cards++;
                stats.Textures.Add(MapModelPort.Ds3Texture(material, "albedo") is { } cardTex ? Path.GetFileNameWithoutExtension(ItemText.Leaf(cardTex.Path)) : "?");
                continue;
            }
            var albedoTexture = MapModelPort.Ds3Texture(material, "albedo");
            int fadeUv = FadeUvIndex(flver, mesh);
            var fades = mesh.Vertices.Select(v => Fade(v, kind, fadeUv)).ToArray();
            if (albedoTexture is null || fades.All(f => f < 1f / 255))
            {
                flver.Meshes.Remove(mesh);
                stats.Empty++;
                continue;
            }
            if (vertexFade?.Invoke(kind) == true && kind != "nolight-opacity")
            {
                stats.VertexFaded++;
                stats.Textures.Add(Path.GetFileNameWithoutExtension(ItemText.Leaf(albedoTexture.Path)));
                keepFade?.Add(mesh);
                // DS3's tiling into the UVs: the legacy sampler's scale isn't known to apply (vanilla leaves it at 1).
                var tiling = albedoTexture.TilingScale.X > 0 && albedoTexture.TilingScale.Y > 0 ? albedoTexture.TilingScale : Vector2.One;
                if (tiling != Vector2.One)
                    foreach (var v in mesh.Vertices)
                        if (v.UVs.Count > 0)
                            v.UVs[0] = new Vector3(v.UVs[0].X * tiling.X, v.UVs[0].Y * tiling.Y, v.UVs[0].Z);
                tiled.Add(material);
                continue;
            }

            // DS3's float UVs are in short units on some meshes (the Opacity cards' 1..2047), as Conform reads them.
            var uvs = mesh.Vertices.Select(v => v.UVs.Count > 0 ? new Vector2(v.UVs[0].X, v.UVs[0].Y) : Vector2.Zero).ToArray();
            if (uvs.Max(u => MathF.Max(MathF.Abs(u.X), MathF.Abs(u.Y))) > 15.99f)
                for (int i = 0; i < uvs.Length; i++)
                    uvs[i] /= 2048f;
            var min = new Vector2(uvs.Min(u => u.X), uvs.Min(u => u.Y));
            var max = new Vector2(uvs.Max(u => u.X), uvs.Max(u => u.Y));
            var span = Vector2.Max(max - min, new Vector2(1e-3f));
            if (span.X > MaxSpan || span.Y > MaxSpan)
            {
                stats.TooWide++;
                if (kind == "nolight-blend")
                    tooWide.Add(mesh);
                else
                    flver.Meshes.Remove(mesh);
                continue;
            }

            var fade = Rasterise(mesh, uvs, fades, min, span);
            static string Name(FLVER2.Texture? t) => t is null ? "" : Path.GetFileNameWithoutExtension(ItemText.Leaf(t.Path));
            static Vector2 Scale(FLVER2.Texture? t) => t is null || t.TilingScale.X <= 0 || t.TilingScale.Y <= 0 ? Vector2.One : t.TilingScale;
            var mask1 = kind == "nolight-opacity" ? material.Textures.FirstOrDefault(t => t.ParamName.Contains("OpacityTexture1", StringComparison.OrdinalIgnoreCase) && t.Path.Length > 0) : null;
            var mask2 = kind == "nolight-opacity" ? material.Textures.FirstOrDefault(t => t.ParamName.Contains("OpacityTexture2", StringComparison.OrdinalIgnoreCase) && t.Path.Length > 0) : null;
            var recipe = new Recipe(kind, Name(albedoTexture), Scale(albedoTexture), mask1 is null ? null : Name(mask1), Scale(mask1),
                mask2 is null ? null : Name(mask2), Scale(mask2), min, span, fade);
            var name = NameFor(recipe);
            Recipes.TryAdd(name, recipe);
            stats.Baked++;
            stats.Textures.Add(recipe.Albedo);

            // The mesh's UVs over its own texture, once.
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var v = mesh.Vertices[i];
                var uv = (uvs[i] - min) / span;
                if (v.UVs.Count == 0)
                    v.UVs.Add(Vector3.Zero);
                v.UVs[0] = new Vector3(uv.X, uv.Y, 0);
            }
            // Its own material: the DS3 one with the baked texture in the albedo slot, tiling 1 x 1, no other textures.
            var copyName = material.Name + Suffix + name[^8..];
            int index = flver.Materials.FindIndex(m => m.Name == copyName);
            if (index < 0)
            {
                flver.Materials.Add(new FLVER2.Material
                {
                    Name = copyName, MTD = material.MTD, GXIndex = material.GXIndex, Index = material.Index,
                    Textures = [new FLVER2.Texture(albedoTexture.ParamName, name + ".tif", Vector2.One, albedoTexture.TilingTypeU,
                        albedoTexture.TilingTypeV, albedoTexture.Unk14, albedoTexture.Unk18, albedoTexture.Unk1C)],
                });
                index = flver.Materials.Count - 1;
            }
            mesh.MaterialIndex = index;
        }
        foreach (var material in tiled)
            if (MapModelPort.Ds3Texture(material, "albedo") is { } t)
                t.TilingScale = Vector2.One;
        return tooWide;
    }

    /// <summary>A recipe's texture name: its albedo + <see cref="Suffix"/> + 8 hex digits of everything it is made from.</summary>
    public static string NameFor(Recipe r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
        {
            w.Write(r.Kind); w.Write(r.Albedo); w.Write(r.Mask1 ?? ""); w.Write(r.Mask2 ?? "");
            foreach (var f in new[] { r.AlbedoScale.X, r.AlbedoScale.Y, r.Mask1Scale.X, r.Mask1Scale.Y, r.Mask2Scale.X, r.Mask2Scale.Y,
                         r.UvMin.X, r.UvMin.Y, r.UvSpan.X, r.UvSpan.Y })
                w.Write(f);
            w.Write(r.Fade);
        }
        var hash = System.Security.Cryptography.SHA256.HashData(ms.ToArray());
        return r.Albedo + Suffix + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>
    /// The mesh's fade over its UV range, <see cref="FadeSize"/> square: each triangle filled by its corners' fades
    /// (the highest where triangles overlap), then the gaps between triangles filled from their neighbours, so the
    /// texture's filtering at a triangle's edge doesn't pull in zero.
    /// </summary>
    public static byte[] Rasterise(FLVER2.Mesh mesh, Vector2[] uvs, float[] fades, Vector2 min, Vector2 span)
    {
        const int N = FadeSize;
        var value = new float[N * N];
        var covered = new bool[N * N];
        bool restarts = mesh.Vertices.Count < 0xFFFF;
        foreach (var set in mesh.FaceSets.Where(s => s.Flags == FLVER2.FaceSet.FSFlags.None).DefaultIfEmpty(mesh.FaceSets.FirstOrDefault()))
        {
            if (set is null)
                continue;
            var tris = set.Triangulate(restarts);
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= uvs.Length || b >= uvs.Length || c >= uvs.Length)
                    continue;
                Vector2 P(int i) => (uvs[i] - min) / span * N - new Vector2(0.5f);
                Fill(P(a), P(b), P(c), fades[a], fades[b], fades[c], value, covered, N);
            }
        }
        // Grow the covered area outwards a few texels (nearest neighbour), so edges keep their fade under filtering.
        for (int pass = 0; pass < 4; pass++)
        {
            var next = (bool[])covered.Clone();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    if (covered[y * N + x])
                        continue;
                    float best = -1;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= N || yy >= N || !covered[yy * N + xx])
                                continue;
                            best = MathF.Max(best, value[yy * N + xx]);
                        }
                    if (best >= 0)
                    {
                        value[y * N + x] = best;
                        next[y * N + x] = true;
                    }
                }
            covered = next;
        }
        return value.Select(f => (byte)Math.Clamp(MathF.Round(f * 255f), 0, 255)).ToArray();
    }

    private static void Fill(Vector2 p0, Vector2 p1, Vector2 p2, float f0, float f1, float f2, float[] value, bool[] covered, int n)
    {
        float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        int x1 = Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        int y1 = Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        if (MathF.Abs(area) < 1e-6f)
        {
            // A sliver: its corners still mark their texels.
            foreach (var (p, f) in new[] { (p0, f0), (p1, f1), (p2, f2) })
            {
                int x = Math.Clamp((int)MathF.Round(p.X), 0, n - 1), y = Math.Clamp((int)MathF.Round(p.Y), 0, n - 1);
                value[y * n + x] = covered[y * n + x] ? MathF.Max(value[y * n + x], f) : f;
                covered[y * n + x] = true;
            }
            return;
        }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x, y);
                float w0 = ((p1.X - p.X) * (p2.Y - p.Y) - (p2.X - p.X) * (p1.Y - p.Y)) / area;
                float w1 = ((p2.X - p.X) * (p0.Y - p.Y) - (p0.X - p.X) * (p2.Y - p.Y)) / area;
                float w2 = 1 - w0 - w1;
                const float Eps = -0.02f;
                if (w0 < Eps || w1 < Eps || w2 < Eps)
                    continue;
                float f = Math.Clamp(w0 * f0 + w1 * f1 + w2 * f2, 0f, 1f);
                int i = y * n + x;
                value[i] = covered[i] ? MathF.Max(value[i], f) : f;
                covered[i] = true;
            }
    }

    /// <summary>
    /// The largest baked texture: 2048 x 1024 (2 MB as BC7 plus mips). The layers are soft clouds and haze seen
    /// kilometres away, and a map can have 20 of them (Archdragon Peak's cloud cards). DS3's own 4096 panoramas
    /// (m45 kumo_30 at 4 x 1 tiling) would otherwise bake at 16 MB each.
    /// </summary>
    public const int MaxWidth = 2048, MaxHeight = 1024, MaxPixels = 2 << 20;

    /// <summary>
    /// Makes a recipe's texture from its DS3 textures. Returns null when the albedo can't be read, and the caller
    /// then writes its usual stand-in.
    /// </summary>
    public static TPF.Texture? Build(string name, Recipe r, Func<string, TPF.Texture?> find)
    {
        var albedoTex = find(r.Albedo);
        if (albedoTex is null)
            return null;
        var (albedo, aw, ah) = Decode(albedoTex.Bytes);
        if (albedo is null)
            return null;
        (ColorRgba32[] P, int W, int H)? Mask(string? m)
        {
            if (m is null || find(m) is not { } t)
                return null;
            var (p, w, h) = Decode(t.Bytes);
            return p is null ? null : (p, w, h);
        }
        var m1 = Mask(r.Mask1);
        var m2 = Mask(r.Mask2);

        // As many texels per tile as DS3's albedo has, over the mesh's UV range, within the size limits.
        float wantW = aw * r.UvSpan.X * r.AlbedoScale.X, wantH = ah * r.UvSpan.Y * r.AlbedoScale.Y;
        float shrink = MathF.Min(1f, MathF.Min(MaxWidth / MathF.Max(wantW, 1), MaxHeight / MathF.Max(wantH, 1)));
        shrink = MathF.Min(shrink, MathF.Sqrt(MaxPixels / MathF.Max(wantW * wantH * shrink * shrink, 1)) * shrink);
        int w = Math.Max(4, (int)MathF.Round(wantW * shrink / 4) * 4);
        int h = Math.Max(4, (int)MathF.Round(wantH * shrink / 4) * 4);

        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var uv = r.UvMin + new Vector2((x + 0.5f) / w, (y + 0.5f) / h) * r.UvSpan;
                var c = Sample(albedo, aw, ah, uv * r.AlbedoScale);
                float a = c.W * SampleFade(r.Fade, (uv - r.UvMin) / r.UvSpan);
                if (m1 is { } mm1)
                    a *= Sample(mm1.P, mm1.W, mm1.H, uv * r.Mask1Scale).X;
                if (m2 is { } mm2)
                    a *= Sample(mm2.P, mm2.W, mm2.H, uv * r.Mask2Scale).X;
                int i = (y * w + x) * 4;
                rgba[i] = (byte)Math.Clamp(MathF.Round(c.X * 255), 0, 255);
                rgba[i + 1] = (byte)Math.Clamp(MathF.Round(c.Y * 255), 0, 255);
                rgba[i + 2] = (byte)Math.Clamp(MathF.Round(c.Z * 255), 0, 255);
                rgba[i + 3] = (byte)Math.Clamp(MathF.Round(a * 255), 0, 255);
            }
        // BC7 keeps a smooth alpha; sRGB when DS3's albedo was (BC1_UNORM_SRGB 72 and the like).
        int dxgi = IsSrgb(albedoTex.Bytes) ? 99 : 98;
        var dds = WeaponTextures.Encode(rgba, w, h, CompressionFormat.Bc7, dxgi);
        return new TPF.Texture(name, TpfAlphaFormat, 0, dds, TPF.TPFPlatform.PC);
    }

    /// <summary>
    /// The TPF format byte of a baked texture: 102, Elden Ring's own byte for BC7 colour textures with alpha
    /// (c3000_em_a, DXGI 99; read from Elden Ring's chr, effect and menu texture binders 2026-11-25). The game takes
    /// the byte, not the DDS header, as what the texture holds: up to 1.0.632 the bakes were BC3 under byte 0
    /// (Elden Ring's BC1 without alpha, c3000_am_a) and drew solid wherever they weren't fully clear, under every
    /// unlit shader tried (M[A]_NoLight, S[A]_Soft_NoLight, S[A]_NoLight, M[DistantClouds]_001): the High Wall
    /// ring's hard edge and the white sheet of cloud cards. Elden Ring's sample has no BC3 at all (no DXGI 77/78),
    /// and BC1 with one-bit alpha is byte 1 (c3000_Thorn_a), so the byte means "alpha used", hence BC7 at 102.
    /// </summary>
    public const byte TpfAlphaFormat = 102;

    /// <summary>Whether a DDS is a DX10 sRGB block format.</summary>
    public static bool IsSrgb(byte[] dds)
    {
        if (dds.Length < 148 || System.Text.Encoding.ASCII.GetString(dds, 84, 4) != "DX10")
            return false;
        return BitConverter.ToInt32(dds, 128) is 29 or 72 or 75 or 78 or 91 or 93 or 99;
    }

    /// <summary>Decodes a DDS (an sRGB DX10 format is read as its UNORM twin; only the numbers matter here).</summary>
    public static (ColorRgba32[]? Pixels, int Width, int Height) Decode(byte[] dds)
    {
        try
        {
            var bytes = (byte[])dds.Clone();
            if (bytes.Length >= 148 && System.Text.Encoding.ASCII.GetString(bytes, 84, 4) == "DX10")
            {
                int dxgi = BitConverter.ToInt32(bytes, 128);
                int unorm = dxgi switch { 72 => 71, 75 => 74, 78 => 77, 99 => 98, 29 => 28, 91 => 87, 93 => 88, _ => dxgi };
                BitConverter.GetBytes(unorm).CopyTo(bytes, 128);
            }
            var (p, w, h) = WeaponTextures.Decode(bytes);
            return (p, w, h);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, 0, 0);
        }
    }

    /// <summary>Bilinear, wrapping (DS3's sky textures tile), as 0..1 floats.</summary>
    public static Vector4 Sample(ColorRgba32[] p, int w, int h, Vector2 uv)
    {
        float fx = (uv.X - MathF.Floor(uv.X)) * w - 0.5f, fy = (uv.Y - MathF.Floor(uv.Y)) * h - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        Vector4 At(int x, int y)
        {
            x = ((x % w) + w) % w;
            y = ((y % h) + h) % h;
            var c = p[y * w + x];
            return new Vector4(c.r, c.g, c.b, c.a) / 255f;
        }
        return Vector4.Lerp(Vector4.Lerp(At(x0, y0), At(x0 + 1, y0), tx), Vector4.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), tx), ty);
    }

    /// <summary>Bilinear, clamped, over the fade raster (0..1 across the mesh's UV range).</summary>
    public static float SampleFade(byte[] fade, Vector2 t)
    {
        const int N = FadeSize;
        float fx = Math.Clamp(t.X, 0, 1) * N - 0.5f, fy = Math.Clamp(t.Y, 0, 1) * N - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        float At(int x, int y) => fade[Math.Clamp(y, 0, N - 1) * N + Math.Clamp(x, 0, N - 1)] / 255f;
        return float.Lerp(float.Lerp(At(x0, y0), At(x0 + 1, y0), tx), float.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), tx), ty);
    }
}
