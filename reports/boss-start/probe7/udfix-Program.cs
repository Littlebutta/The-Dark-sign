using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;
// udfix <behbnd(.dcx)> [--dry]   (probe 7, 2026-11-24)
// Elden Ring numbers every state's CustomManualSelectorGenerator: m_userData = (state machine number << 16) | the
// state's index in m_states (866 of 866 in vanilla c3061's c9997.hkx). States the port added copied their donor's
// number, so two or more states share one. This rewrites the low 16 bits of each CMSG's m_userData to its index and
// prints every change. Nothing else in the file changes.
var raw = File.ReadAllBytes(args[0]);
bool dcx = DCX.Is(raw);
DCX.CompressionInfo comp = null;
var bnd = BND4.Read(dcx ? DCX.Decompress(raw, out comp) : raw);
int changed = 0;
foreach (var f in bnd.Files.Where(f => f.Name.EndsWith("c9997.hkx", StringComparison.OrdinalIgnoreCase)))
{
    var root = new HavokBinarySerializer().Read(new MemoryStream(f.Bytes));
    var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
    var machines = new List<hkbStateMachine>();
    void Walk(object o)
    {
        if (o is null || o is string || o.GetType().IsPrimitive || o.GetType().IsEnum || !seen.Add(o)) return;
        if (o is hkbStateMachine sm) machines.Add(sm);
        if (o is System.Collections.IList l) { foreach (var e in l) Walk(e); return; }
        if (o.GetType().Namespace?.StartsWith("HKLib") != true) return;
        foreach (var fi in o.GetType().GetFields()) Walk(fi.GetValue(o));
    }
    Walk(root);
    foreach (var sm in machines)
        for (int i = 0; i < sm.m_states.Count; i++)
            if (sm.m_states[i].m_generator is CustomManualSelectorGenerator g && (g.m_userData & 0xFFFF) != (ulong)i)
            {
                ulong high = sm.m_states.Select(s => s.m_generator).OfType<CustomManualSelectorGenerator>().First().m_userData & ~0xFFFFUL;
                Console.WriteLine($"{sm.m_name} [{i}] {sm.m_states[i].m_name}: userData {g.m_userData:X} -> {(high | (ulong)i):X}");
                g.m_userData = high | (ulong)i;
                changed++;
            }
    if (changed > 0 && !args.Contains("--dry"))
    {
        using var ms = new MemoryStream();
        new HavokBinarySerializer().Write(root, ms);
        f.Bytes = ms.ToArray();
    }
}
Console.WriteLine($"{changed} selector(s) renumbered{(args.Contains("--dry") ? " (dry run, nothing written)" : "")}");
if (changed > 0 && !args.Contains("--dry"))
    File.WriteAllBytes(args[0], dcx ? DCX.Compress(bnd.Write(), comp) : bnd.Write());
