using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;
// idprobe <c7112.behbnd(.dcx)>   (probe 9, 2026-11-24)
// Do clips play when their internal ID is one the template registered? Probe 8 pointed Attack3012 at a000_003014
// with 3014's port-registered ID 213 and got only a twitch. Here:
//  - Attack3012's a000 clip names a000_003014 but keeps 3012's template ID (33);
//  - Event30002's clip (added state, ID 224) takes Event30003's template ID (146), name unchanged.
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
var dst = cmsgs.First(c => c.m_animId == 3012).m_generators.OfType<hkbClipGenerator>().First(g => g.m_animationName == "a000_003012");
Console.WriteLine($"Attack3012 a000 clip: {dst.m_animationName} id {dst.m_animationInternalId}");
dst.m_animationName = "a000_003014";
Console.WriteLine($"  now: {dst.m_animationName} id {dst.m_animationInternalId}");
var kneel = cmsgs.First(c => c.m_animId == 30002).m_generators.OfType<hkbClipGenerator>().First();
var slot = cmsgs.First(c => c.m_animId == 30003).m_generators.OfType<hkbClipGenerator>().First().m_animationInternalId;
Console.WriteLine($"Event30002 clip: {kneel.m_animationName} id {kneel.m_animationInternalId} -> id {slot}");
kneel.m_animationInternalId = slot;
using var ms = new MemoryStream();
new HavokBinarySerializer().Write(root, ms);
f.Bytes = ms.ToArray();
File.WriteAllBytes(args[0], dcx ? DCX.Compress(bnd.Write(), comp) : bnd.Write());
