using SoulsFormats;
// abtest <in> <out>: adds event 45005998 (started from event 0): wait for action button 74000007 on Gundyr 45000800
// in condition group 0 (a direct wait), then force Gundyr's 3012 (a visible 5 s lunge) twice, 3 s apart.
var raw = DCX.Decompress(File.ReadAllBytes(args[0]), out var comp);
var em = EMEVD.Read(raw);
var ab = em.Events.First(e => e.ID == 45005811).Instructions[10];
var anim = em.Events.First(e => e.ID == 45005810).Instructions.First(i => i.Bank == 2003 && i.ID == 18);
var wait = em.Events.SelectMany(e => e.Instructions).First(i => i.Bank == 1001 && i.ID == 0);
var a = (byte[])ab.ArgData.Clone(); a[0] = 0; // condition group 0
var f = (byte[])anim.ArgData.Clone(); BitConverter.TryWriteBytes(f.AsSpan(0), 45000800); BitConverter.TryWriteBytes(f.AsSpan(4), 3012); f[8] = 0;
var w = (byte[])wait.ArgData.Clone(); BitConverter.TryWriteBytes(w.AsSpan(0), 3f);
var ev = new EMEVD.Event(45005998, EMEVD.Event.RestBehaviorType.Default);
ev.Instructions.AddRange([new EMEVD.Instruction(3, 24, a), new EMEVD.Instruction(2003, 18, f), new EMEVD.Instruction(1001, 0, w), new EMEVD.Instruction(2003, 18, (byte[])f.Clone())]);
em.Events.Add(ev);
var sa = new byte[8]; BitConverter.TryWriteBytes(sa.AsSpan(4), 45005998u);
em.Events.First(e => e.ID == 0).Instructions.Add(new EMEVD.Instruction(2000, 0, sa));
File.WriteAllBytes(args[1], DCX.Compress(em.Write(), comp));
Console.WriteLine($"3[24] args {Convert.ToHexString(a)}");
