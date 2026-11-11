using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// Glowing DS3 character materials (an emissive texture: the Jailer's lantern c2230_weapon_e, all fifteen of the Soul
/// of Cinder's) on an Elden Ring material that glows. The plain character material (C[AMSN].spx) has no emissive
/// slot, so up to 1.0.326 they were drawn without their glow, and the only glow a ported Jailer showed was the cyan
/// lantern light and blue effect of its DS3 SpEffects (see <see cref="ScriptEffects"/>), not its orange lantern.
/// <para>The template is one of Elden Ring's own glowing character materials, found once per run: a C[...] MATBIN
/// that samples albedo, normal and emissive maps (and nothing the port can't fill), preferring the plain glowing
/// shader C[AMSN]_Glow, with the FLVER material and GX list its character uses for it. Its colour parameters are
/// set to white, so the glow is the colour of DS3's own emissive texture (the Jailer's is orange). Without one
/// (DARKSIGN_ENEMY_GLOW=0, or none found) glowing materials stay plain, as before. The weapon port does the same
/// for weapons (<see cref="ModelPort.Emissive"/>).</para>
/// </summary>
public static class ChrGlow
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_GLOW")?.Trim() != "0";

    /// <summary>A glowing Elden Ring character material: its MATBIN, sampler types, the FLVER material and GX list.</summary>
    public sealed record Template(string Matbin, string Shader, IReadOnlyList<string> Samplers, FLVER2.Material Material, FLVER2.GXList GX)
    {
        public string? Sampler(string kind) => Samplers.FirstOrDefault(s => ModelPort.SlotKind(s) == kind);
    }

    private static readonly object Lock = new();
    private static Template? _found;
    private static bool _searched;

    /// <summary>The template, found once per run; null when glowing is off or Elden Ring has none the port can fill.</summary>
    public static Template? Find(GameArchives er)
    {
        lock (Lock)
        {
            if (_searched)
                return _found;
            _searched = true;
            if (!Enabled)
            {
                Console.WriteLine("enemies: glowing DS3 materials are drawn plain (DARKSIGN_ENEMY_GLOW=0)");
                return null;
            }
            try
            {
                _found = Search(er);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"enemies: no glowing character material ({ChrPort.Describe(e)}); glowing DS3 materials stay plain");
            }
            return _found;
        }
    }

    /// <summary>How a candidate MATBIN ranks, or null when the port can't fill it (a sampler of a kind it has no texture for).</summary>
    public static int? Score(MATBIN matbin)
    {
        var kinds = matbin.Samplers.Select(s => ModelPort.SlotKind(s.Type)).ToList();
        if (kinds.Any(k => k is null) || !kinds.Contains("emissive") || !kinds.Contains("albedo") || !kinds.Contains("normal"))
            return null;
        var shader = Path.GetFileNameWithoutExtension(ItemText.Leaf(matbin.ShaderPath));
        // 2026-11-11 survey of Elden Ring's 321 glowing character materials: 47 on C[AMSN]_Glow, all with the plain
        // character samplers plus an EmissiveMap; most have white colours and a full g_GlowScale.
        bool neutral = matbin.Params.Where(IsColour).All(p => ((float[])p.Value).Take(3).All(v => v == 1f))
                       && (matbin.Params.FirstOrDefault(p => p.Name == "g_GlowScale")?.Value is not float[] { Length: 4 } scale || scale[3] >= 0.5f);
        return (shader.Equals("C[AMSN]_Glow", StringComparison.OrdinalIgnoreCase) ? 4 : shader.StartsWith("C[AMSN]_Glow", StringComparison.OrdinalIgnoreCase) ? 2 : 0)
               + (kinds.Contains("metallic") ? 1 : 0) + (neutral ? 1 : 0);
    }

    private static Template? Search(GameArchives er)
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
                     .OrderByDescending(c => c.Score).ThenBy(c => c.Name, StringComparer.Ordinal).Take(40))
        {
            // C[c3570]_Body_Glow -> chr\c3570.chrbnd: the character that wears it, for the GX list it is drawn with.
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
                var shader = Path.GetFileNameWithoutExtension(ItemText.Leaf(matbin.ShaderPath));
                var template = new Template(name, shader, matbin.Samplers.Select(s => s.Type).ToList(), mat, flver.GXLists[mat.GXIndex]);
                Console.WriteLine($"enemies: glowing DS3 materials use Elden Ring's {name} ({shader}; samplers {string.Join(", ", template.Samplers)}; " +
                                  $"params {string.Join(", ", matbin.Params.Select(p => $"{p.Name}={Format(p.Value)}"))}; {candidates.Count} candidates)");
                return template;
            }
        }
        Console.WriteLine($"enemies: no glowing Elden Ring character material found ({candidates.Count} candidates); glowing DS3 materials stay plain");
        return null;
    }

    private static string Format(object? v) => v is Array a ? "[" + string.Join(" ", a.Cast<object>()) + "]" : v?.ToString() ?? "";

    /// <summary>
    /// A colour parameter of the glowing material (g_DiffuseMapColor, C_AMSN__Glow_snp_0_color_N, g_GlowScale: a tint on
    /// the albedo or the glow): its colour part (the first three values) is set to white, so DS3's textures keep their
    /// own colours. Every other value (the strength in the fourth / fifth, the other parameters) stays Elden Ring's.
    /// </summary>
    public static bool IsColour(MATBIN.Param p) =>
        p.Value is float[] { Length: >= 3 } && (p.Name.Contains("color", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("colour", StringComparison.OrdinalIgnoreCase)
                                                || p.Name.Equals("g_GlowScale", StringComparison.OrdinalIgnoreCase));

    /// <summary>The colour parameters of a copied glowing material set to white (see <see cref="IsColour"/>); returns how many.</summary>
    public static int Whiten(MATBIN matbin)
    {
        int n = 0;
        foreach (var p in matbin.Params.Where(IsColour))
        {
            var v = (float[])((float[])p.Value).Clone();
            v[0] = v[1] = v[2] = 1f;
            p.Value = v;
            n++;
        }
        return n;
    }
}
