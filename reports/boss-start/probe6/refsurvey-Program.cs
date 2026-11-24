using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;
// refsurvey <anibnd(.dcx)>...  (2026-11-24, read-only)
// For every animation in each binder: does it carry root motion (hkaDefaultAnimatedReferenceFrame, m_extractedMotion)?
// Prints per-binder counts and every clip without it (name, duration, frames, tracks).
// Needs HKLib (Porter/third_party/HKLib) and SoulsFormats (Oodle DLL next to it for .dcx), like the Porter.
foreach (var path in args)
{
    var raw = File.ReadAllBytes(path);
    var bnd = BND4.Read(DCX.Is(raw) ? DCX.Decompress(raw) : raw);
    var compendium = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
    int with = 0, without = 0, failed = 0;
    var missing = new List<string>();
    foreach (var f in bnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("skeleton", StringComparison.OrdinalIgnoreCase)))
    {
        try
        {
            var s = new HavokBinarySerializer();
            if (compendium is not null) s.LoadCompendium(new MemoryStream(compendium));
            var root = (hkRootLevelContainer)s.Read(new MemoryStream(f.Bytes));
            var c = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaAnimationContainer>().FirstOrDefault();
            var a = c?.m_animations.FirstOrDefault();
            if (a is null) continue;
            if (a.m_extractedMotion is not null) with++;
            else { without++; missing.Add($"    {Path.GetFileName(f.Name)}: {a.m_duration:0.###} s, {a.m_numberOfTransformTracks} tracks, {(a as hkaSplineCompressedAnimation)?.m_numFrames} frames"); }
        }
        catch (Exception ex) { failed++; missing.Add($"    {Path.GetFileName(f.Name)}: unreadable ({ex.GetType().Name})"); }
    }
    Console.WriteLine($"{path}: {with} with root motion, {without} without, {failed} unreadable");
    foreach (var m in missing) Console.WriteLine(m);
}
