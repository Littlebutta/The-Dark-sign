using SoulsFormats;
// p16c <in> <out>: two test events from event 0, both on asset 45001952 (AEG920_000_9001, 0.3 m from Gundyr):
// 45005996 waits 3[24] group 0 button 74000007 -> player (10000) anim 60750; 45005997 waits button 6000 -> Gundyr anim 3012.
var raw = DCX.Decompress(File.ReadAllBytes(args[0]), out var comp);
var em = EMEVD.Read(raw);
var ab = em.Events.First(e => e.ID == 45005811).Instructions[10];
var anim = em.Events.First(e => e.ID == 45005810).Instructions.First(i => i.Bank == 2003 && i.ID == 18);
EMEVD.Event Make(long id, int button, int who, int animId)
{
    var a = (byte[])ab.ArgData.Clone(); a[0] = 0; BitConverter.TryWriteBytes(a.AsSpan(4), button); BitConverter.TryWriteBytes(a.AsSpan(8), 45001952);
    var f = (byte[])anim.ArgData.Clone(); BitConverter.TryWriteBytes(f.AsSpan(0), who); BitConverter.TryWriteBytes(f.AsSpan(4), animId); f[8] = 0;
    var ev = new EMEVD.Event(id, EMEVD.Event.RestBehaviorType.Default);
    ev.Instructions.AddRange([new EMEVD.Instruction(3, 24, a), new EMEVD.Instruction(2003, 18, f)]);
    Console.WriteLine($"{id}: 3[24] {Convert.ToHexString(a)} -> 2003[18] {Convert.ToHexString(f)}");
    return ev;
}
em.Events.Add(Make(45005996, 74000007, 10000, 60750));
em.Events.Add(Make(45005997, 6000, 45000800, 3012));
var init = em.Events.First(e => e.ID == 0);
foreach (uint id in new[] { 45005996u, 45005997u }) { var sa = new byte[8]; BitConverter.TryWriteBytes(sa.AsSpan(4), id); init.Instructions.Add(new EMEVD.Instruction(2000, 0, sa)); }
File.WriteAllBytes(args[1], DCX.Compress(em.Write(), comp));
