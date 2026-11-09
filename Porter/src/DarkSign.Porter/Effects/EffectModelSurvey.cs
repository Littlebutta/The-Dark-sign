using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// effect-models: a read-only report on the DS3 effects the effect port leaves out for using models (Friede's frost
/// blade 440501 and 34 others in 1.0.238: "effects: left out (uses models)"), the first step of porting them. For each
/// effect: the models and anibnds it references, and every model's FLVER header, meshes, bones and materials (MTD /
/// material names, texture slots and paths), next to the same for every model Elden Ring's own common effects carry,
/// so the material conversion the port needs (DS3 effect MTDs -> Elden Ring effect MATBINs) can be written from the
/// facts rather than guessed. Writes Work\reports\diag\effect-models.txt. Nothing in the game folders is changed.
/// Usage: effect-models [DS3 fxr id...]   (no IDs: every DS3 common effect that uses a model)
/// </summary>
public static class EffectModelSurvey
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        var ids = args.Select(a => int.TryParse(a, out var v) ? v : -1).Where(v => v > 0).ToList();
        var er = ctx.OpenArchives(PorterGame.EldenRing);
        var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        var r = new StringBuilder();
        void Log(string line) => r.AppendLine(line);
        var index = FxrPort.Ds3Index(ds3);
        var js = new FxrPort.FxrScript();
        Log($"# DS3 effects that use models ({DateTime.Now:yyyy-MM-dd HH:mm})");
        Log($"DS3 effect files: {index.Fxr.Count} fxr, {index.Resources.Count(k => k.Key.Item1 == FxrPort.Kind.Model)} flver, " +
            $"{index.Resources.Count(k => k.Key.Item1 == FxrPort.Kind.Anibnd)} anibnd, {index.Resources.Count(k => k.Key.Item1 == FxrPort.Kind.Texture)} tpf");
        int withModels = 0, unreadable = 0;
        var materials = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shown = new HashSet<int>();
        foreach (var (id, file) in index.Fxr.OrderBy(f => f.Key))
        {
            if (ids.Count > 0 && !ids.Contains(id))
                continue;
            FxrPort.FxrScript.Info info;
            try { info = js.Inspect(file.File.Bytes); }
            catch (Exception) { unreadable++; continue; }
            var models = info.Models.Where(m => m > 0).Distinct().ToList();
            var anibnds = info.Anibnds.Where(m => m > 0).Distinct().ToList();
            if (models.Count == 0 && anibnds.Count == 0 && ids.Count == 0)
                continue;
            withModels++;
            Log($"\n## f{id:D9} ({ItemText.Leaf(file.Binder)}): models {string.Join(" ", models)}; anibnds {string.Join(" ", anibnds)}; " +
                $"textures {string.Join(" ", info.Textures.Where(t => t > 0).Distinct())}; child effects {string.Join(" ", info.Sfx.Where(t => t > 0).Distinct())}");
            foreach (var m in models)
            {
                if (!index.Resources.TryGetValue((FxrPort.Kind.Model, m), out var model))
                {
                    Log($"  model {m}: NO DS3 FLVER");
                    continue;
                }
                if (shown.Add(m))
                    Describe(Log, $"  DS3 model {m} ({ItemText.Leaf(model.File.Name)}, {model.File.Bytes.Length} bytes)", model.File.Bytes, materials);
                else
                    Log($"  model {m}: described above");
            }
            foreach (var a in anibnds)
                Log(index.Resources.TryGetValue((FxrPort.Kind.Anibnd, a), out var ani)
                    ? $"  anibnd {a}: {ItemText.Leaf(ani.File.Name)}, {ani.File.Bytes.Length} bytes" : $"  anibnd {a}: NO DS3 FILE");
        }
        Log($"\n# {withModels} DS3 effects use models or anibnds ({unreadable} unreadable effects skipped)");
        Log("DS3 effect materials (MTD, by models using them):");
        foreach (var (mtd, n) in materials.OrderByDescending(k => k.Value).ThenBy(k => k.Key))
            Log($"  {n,4} {mtd}");

        // Elden Ring's side: every model its common effects carry.
        Log("\n# Elden Ring common effect models");
        var erMaterials = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int erModels = 0;
        foreach (var entry in er.Entries.Where(e => e.Path is { } p && p.StartsWith("/sfx/") && p.EndsWith(".ffxbnd.dcx", StringComparison.OrdinalIgnoreCase)
                                                    && p.Contains("commoneffects", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            BND4 bnd;
            try { bnd = BND4.Read(er.Read(entry.Path!)); }
            catch (Exception e) { Log($"{entry.Path}: unreadable ({e.Message})"); continue; }
            var flvers = bnd.Files.Where(f => ItemText.Leaf(f.Name).EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).ToList();
            Log($"\n## {entry.Path}: {bnd.Files.Count} files, {flvers.Count} models");
            foreach (var f in flvers)
            {
                erModels++;
                // The first few in full, the rest only feed the material catalogue.
                Describe(erModels <= 12 ? Log : _ => { }, $"  ER model {ItemText.Leaf(f.Name)} ({f.Bytes.Length} bytes)", f.Bytes, erMaterials);
            }
        }
        Log($"\n{erModels} Elden Ring effect models; their materials (MTD / MATBIN, by models using them):");
        foreach (var (mtd, n) in erMaterials.OrderByDescending(k => k.Value).ThenBy(k => k.Key))
            Log($"  {n,4} {mtd}");

        Directory.CreateDirectory(ctx.WorkPath("reports", "diag"));
        var outPath = ctx.WorkPath("reports", "diag", "effect-models.txt");
        File.WriteAllText(outPath, r.ToString());
        Console.WriteLine($"effect-models: {withModels} DS3 effects with models, {erModels} Elden Ring effect models -> {outPath} ({r.Length / 1024} KB)");
        return 0;
    }

    private static void Describe(Action<string> log, string head, byte[] bytes, Dictionary<string, int> materials)
    {
        if (!FLVER2.Is(bytes))
        {
            log($"{head}: not a FLVER2 ({(bytes.Length >= 6 ? Encoding.ASCII.GetString(bytes, 0, 6) : "")})");
            return;
        }
        FLVER2 flver;
        try { flver = FLVER2.Read(bytes); }
        catch (Exception e) { log($"{head}: unreadable ({e.GetType().Name}: {e.Message})"); return; }
        log($"{head}: FLVER {flver.Header.Version:X}, {flver.Meshes.Count} meshes, {flver.Nodes.Count} nodes, {flver.Dummies.Count} dummies, " +
            $"{flver.Materials.Count} materials, {flver.GXLists.Count} GX lists, bbox {V(flver.Header.BoundingBoxMin)}..{V(flver.Header.BoundingBoxMax)}");
        foreach (var m in flver.Materials)
        {
            materials[m.MTD] = materials.GetValueOrDefault(m.MTD) + 1;
            log($"    material '{m.Name}' mtd '{m.MTD}' gx {m.GXIndex}: " + string.Join("; ", m.Textures.Select(t => $"{t.ParamName}={t.Path}")));
        }
        foreach (var mesh in flver.Meshes.Take(4))
        {
            var layout = mesh.Vertices.Count > 0 ? flver.BufferLayouts[mesh.VertexBuffers[0].LayoutIndex] : null;
            log($"    mesh material {mesh.MaterialIndex} node {mesh.NodeIndex}: {mesh.Vertices.Count} vertices, {mesh.FaceSets.Count} face sets, " +
                $"layout {(layout is null ? "-" : string.Join(" ", layout.Select(l => $"{l.Semantic}{l.Index}:{l.Type}")))}");
        }
    }

    private static string V(System.Numerics.Vector3 v) => $"({v.X:0.##},{v.Y:0.##},{v.Z:0.##})";
}
