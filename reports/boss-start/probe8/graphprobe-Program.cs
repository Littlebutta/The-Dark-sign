using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;
// graphprobe <c7112.behbnd(.dcx)>   (probe 8, 2026-11-24)
// Does the game use the graph in our behbnd at all? Edits ONE template state, Attack3012 (which reliably plays when
// forced): its a000 clip generator now names 3014's clip (a000_003014, 3014's internal ID) and plays at 0.4x speed.
// If forcing 3012 then shows 3014's motion, slowly, the game reads our c9997.hkx and picks clips by the generator.
var raw = File.ReadAllBytes(args[0]);
bool dcx = DCX.Is(raw);
DCX.CompressionInfo comp = null;
var bnd = BND4.Read(dcx ? DCX.Decompress(raw, out comp) : raw);
var f = bnd.Files.First(f => f.Name.EndsWith("c9997.hkx", StringComparison.OrdinalIgnoreCase));
var root = new HavokBinarySerializer().Read(new MemoryStream(f.Bytes));
var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
var cmsgs = new List<CustomManualSelectorGenerator>();
void Walk(object o)
{
    if (o is null || o is string || o.GetType().IsPrimitive || o.GetType().IsEnum || !seen.Add(o)) return;
    if (o is CustomManualSelectorGenerator c) cmsgs.Add(c);
    if (o is System.Collections.IList l) { foreach (var e in l) Walk(e); return; }
    if (o.GetType().Namespace?.StartsWith("HKLib") != true) return;
    foreach (var fi in o.GetType().GetFields()) Walk(fi.GetValue(o));
}
Walk(root);
var src = cmsgs.First(c => c.m_animId == 3014).m_generators.OfType<hkbClipGenerator>().First(g => g.m_animationName == "a000_003014");
var dst = cmsgs.First(c => c.m_animId == 3012).m_generators.OfType<hkbClipGenerator>().First(g => g.m_animationName == "a000_003012");
Console.WriteLine($"Attack3012 a000 clip: '{dst.m_name}' {dst.m_animationName} id {dst.m_animationInternalId} speed {dst.m_playbackSpeed}");
dst.m_animationName = src.m_animationName;
dst.m_animationInternalId = src.m_animationInternalId;
dst.m_playbackSpeed = 0.4f;
Console.WriteLine($"  now: {dst.m_animationName} id {dst.m_animationInternalId} speed {dst.m_playbackSpeed}");
using var ms = new MemoryStream();
new HavokBinarySerializer().Write(root, ms);
f.Bytes = ms.ToArray();
File.WriteAllBytes(args[0], dcx ? DCX.Compress(bnd.Write(), comp) : bnd.Write());
