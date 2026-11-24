using SoulsFormats;
// p16b <in> <out>: as p16 (instr 10 -> 3[00] flag 45000802 OFF), and at the very start of 45005811 force the player
// (10000) into anim 60750 and wait 2 s, so a visible sign shows the event runs.
var raw = DCX.Decompress(File.ReadAllBytes(args[0]), out var comp);
var em = EMEVD.Read(raw);
var t = em.Events.SelectMany(e => e.Instructions).First(i => i.Bank == 3 && i.ID == 0);
var anim = em.Events.First(e => e.ID == 45005810).Instructions.First(i => i.Bank == 2003 && i.ID == 18);
var wait = em.Events.SelectMany(e => e.Instructions).First(i => i.Bank == 1001 && i.ID == 0);
var ev = em.Events.First(e => e.ID == 45005811);
var a = new byte[t.ArgData.Length]; a[0] = 0xFF; BitConverter.TryWriteBytes(a.AsSpan(4), 45000802u);
ev.Instructions[10] = new EMEVD.Instruction(3, 0, a);
ev.Parameters.RemoveAll(p => p.InstructionIndex == 10);
var f = (byte[])anim.ArgData.Clone(); BitConverter.TryWriteBytes(f.AsSpan(0), 10000); BitConverter.TryWriteBytes(f.AsSpan(4), 60750); f[8] = 0;
var w = (byte[])wait.ArgData.Clone(); BitConverter.TryWriteBytes(w.AsSpan(0), 2f);
ev.Instructions.InsertRange(0, [new EMEVD.Instruction(2003, 18, f), new EMEVD.Instruction(1001, 0, w)]);
foreach (var p in ev.Parameters) p.InstructionIndex += 2;
Console.WriteLine($"45005811: player anim at start, instr 12 now 3[0] {Convert.ToHexString(a)}; {ev.Instructions.Count} instructions");
File.WriteAllBytes(args[1], DCX.Compress(em.Write(), comp));
