using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// DS3 character materials that are see-through: their DS3 material file (MTD) blends them (g_BlendMode 2 alpha,
/// 3 modulate, 4 additive) instead of drawing them solid. Up to 1.0.567 every plain ported material was written with
/// g_BlendMode 0 and, unless its MTD's name said "_e"/"_al"/"_alp", no alpha cut: the Dancer of the Boreal Valley's
/// veil, waist cloth and shoulder cloth (c5270[Veil].mtd / c5270[Veil]_cloth.mtd, g_BlendMode 2, no depth write) drew
/// as one solid white sheet. The 2026-11-21 survey of DS3's 165 character models found 14 such MTDs used 26 times
/// (blend 2: C[ARSN]_al, _al_Decal, _al_hair, the Ghost, Veil and P[ARSN]_al ones; 3: c6232_modulate; 4: C[ARSN]_Add,
/// C[ARSN]_Add_Cloth_Decal), so every one of those characters (c1480/c1490 ghosts, c2030/c2040, c2170/c2230 veils,
/// c5180, c5270, c6020, c6090, c6100, c6120/c6121, c6232, c6240, c6250, c6310, c6330/c6331, c1391) had the same fault.
/// <para>The blend mode and alpha cut now come from DS3's own MTD (/mtd/allmaterialbnd.mtdbnd.dcx), not its name.
/// A blended material is a copy of Elden Ring's plain see-through character material (shader C[AMSN]_Alpha: the same
/// albedo / metallic / normal samplers as the plain C[AMSN], g_BlendMode 2; vanilla C[c2100]_BD_MeshDecal,
/// C[c2180]_Eyebrow_Transparence), found once per run with the GX list its character draws it with, and given DS3's
/// blend mode (modulate becomes alpha: Elden Ring's characters never modulate) and an alpha cut of at least 1, so
/// fully clear texels are not drawn at all. A DS3 MTD that cuts by alpha (g_AlphaRef above 0 with blend 0:
/// C[ARSN]_e_Decal's 128) is cut out whatever its name.</para>
/// Fallback enemy-model O: DARKSIGN_ENEMY_BLEND=0 draws them solid, as up to 1.0.567.
/// </summary>
public static class ChrBlend
{
    public const string MtdBinder = "/mtd/allmaterialbnd.mtdbnd.dcx";

    /// <summary>The plain see-through shader of Elden Ring's characters.</summary>
    public const string AlphaShader = "C[AMSN]_Alpha";

    /// <summary>How DS3 draws one material: its MTD's g_BlendMode and the first value of its g_AlphaRef.</summary>
    public readonly record struct Ds3Draw(int BlendMode, int AlphaRef)
    {
        /// <summary>Drawn see-through (alpha, modulate or additive blending).</summary>
        public bool Blended => BlendMode is 2 or 3 or 4 or 5 or 6 or 7 or 8;

        /// <summary>The blend mode its Elden Ring copy gets: modulate (3) becomes alpha (2), additive (4) stays.</summary>
        public int ErBlendMode => BlendMode == 4 ? 4 : 2;

        /// <summary>Cut out by alpha while drawn solid.</summary>
        public bool AlphaTested => !Blended && (BlendMode == 1 || AlphaRef > 0);
    }

    /// <summary>The key both a FLVER's MTD path and an MTD binder entry are looked up by: leaf, no extension, no spaces, lower case.</summary>
    public static string Key(string mtd) =>
        Path.GetFileNameWithoutExtension(ItemText.Leaf(mtd.Replace('/', '\\'))).Replace(" ", "").ToLowerInvariant();

    private static readonly object Lock = new();
    private static IReadOnlyDictionary<string, Ds3Draw>? _mtds;
    private static bool _searched;
    private static ChrGlow.Template? _template;

    /// <summary>DS3's character MTDs by <see cref="Key"/>, read once per run (empty when blending is off or the binder can't be read).</summary>
    public static IReadOnlyDictionary<string, Ds3Draw> Ds3Mtds(GameArchives ds3)
    {
        lock (Lock)
        {
            if (_mtds is not null)
                return _mtds;
            var table = new Dictionary<string, Ds3Draw>(StringComparer.Ordinal);
            try
            {
                foreach (var f in BND4.Read(ds3.Read(MtdBinder)).Files)
                {
                    MTD mtd;
                    try { mtd = MTD.Read(f.Bytes); }
                    catch (Exception e) when (e is not OutOfMemoryException) { continue; }
                    table.TryAdd(Key(f.Name), Read(mtd));
                }
                Console.WriteLine($"enemies: {table.Count} DS3 MTDs read, {table.Values.Count(d => d.Blended)} of them blended");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"enemies: DS3 MTDs not read ({ChrPort.Describe(e)}); see-through DS3 materials are drawn solid");
            }
            return _mtds = table;
        }
    }

    public static Ds3Draw Read(MTD mtd)
    {
        int blend = 0, alphaRef = 0;
        foreach (var p in mtd.Params)
            if (p.Name == "g_BlendMode" && p.Value is int b)
                blend = b;
            else if (p.Name == "g_AlphaRef" && p.Value is int[] { Length: > 0 } r)
                alphaRef = r[0];
        return new Ds3Draw(blend, alphaRef);
    }

    /// <summary>How a MATBIN ranks as the see-through template, or null when it isn't one the port can fill.</summary>
    public static int? Score(MATBIN matbin)
    {
        var shader = Path.GetFileNameWithoutExtension(ItemText.Leaf(matbin.ShaderPath ?? ""));
        if (!shader.Equals(AlphaShader, StringComparison.OrdinalIgnoreCase))
            return null;
        var kinds = matbin.Samplers.Select(s => ModelPort.SlotKind(s.Type)).ToList();
        if (kinds.Any(k => k is null) || !kinds.Contains("albedo"))
            return null;
        return (kinds.Contains("normal") ? 2 : 0) + (kinds.Contains("metallic") ? 1 : 0);
    }

    /// <summary>Elden Ring's see-through character material, found once per run; null when there is none.</summary>
    public static ChrGlow.Template? Find(GameArchives er)
    {
        lock (Lock)
        {
            if (_searched)
                return _template;
            _searched = true;
            try
            {
                var candidates = new List<(string Name, MATBIN Matbin, int Score)>();
                foreach (var binder in Explore.MaterialBinders.Where(er.Contains))
                    foreach (var f in BND4.Read(er.Read(binder)).Files)
                    {
                        var name = Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name));
                        if (!name.StartsWith("C[c", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var matbin = MATBIN.Read(f.Bytes);
                        if (Score(matbin) is { } score)
                            candidates.Add((name, matbin, score));
                    }
                foreach (var (name, matbin, _) in candidates.DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(c => c.Score).ThenBy(c => c.Name, StringComparer.Ordinal))
                {
                    int close = name.IndexOf(']');
                    if (close < 3 || !int.TryParse(name[3..close], out int chr))
                        continue;
                    var path = $"/chr/c{chr:D4}.chrbnd.dcx";
                    if (!er.Contains(path))
                        continue;
                    foreach (var f in BND4.Read(er.Read(path)).Files.Where(f => FLVER2.Is(f.Bytes)))
                    {
                        var flver = FLVER2.Read(f.Bytes);
                        var mat = flver.Materials.FirstOrDefault(m => ModelPort.MatbinName(m.MTD).Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (mat is null || mat.GXIndex < 0 || mat.GXIndex >= flver.GXLists.Count)
                            continue;
                        _template = new ChrGlow.Template(name, AlphaShader, matbin.Samplers.Select(s => s.Type).ToList(), mat, flver.GXLists[mat.GXIndex]);
                        Console.WriteLine($"enemies: see-through DS3 materials use Elden Ring's {name} (samplers {string.Join(", ", _template.Samplers)}; {candidates.Count} candidates)");
                        return _template;
                    }
                }
                Console.WriteLine($"enemies: no see-through Elden Ring character material found ({candidates.Count} candidates); see-through DS3 materials are drawn solid");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"enemies: no see-through character material ({ChrPort.Describe(e)}); see-through DS3 materials are drawn solid");
            }
            return _template;
        }
    }
}
