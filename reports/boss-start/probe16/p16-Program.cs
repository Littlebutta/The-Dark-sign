using SoulsFormats;
// p16 <in> <out>: event 45005811 instruction 10 (3[24] button) becomes 3[00] IF Event Flag (group -1, state OFF, type 0,
// flag 45000802), taking an existing 3[00]'s argument layout.
var raw = DCX.Decompress(File.ReadAllBytes(args[0]), out var comp);
var em = EMEVD.Read(raw);
var t = em.Events.SelectMany(e => e.Instructions).First(i => i.Bank == 3 && i.ID == 0);
Console.WriteLine($"3[00] template {Convert.ToHexString(t.ArgData)}");
var ev = em.Events.First(e => e.ID == 45005811);
var old = ev.Instructions[10];
Console.WriteLine($"old instr 10: {old.Bank}[{old.ID}] {Convert.ToHexString(old.ArgData)}");
var a = new byte[t.ArgData.Length];
a[0] = 0xFF; a[1] = 0; a[2] = 0; // group -1, OFF, event flag
BitConverter.TryWriteBytes(a.AsSpan(4), 45000802u);
ev.Instructions[10] = new EMEVD.Instruction(3, 0, a);
ev.Parameters.RemoveAll(p => p.InstructionIndex == 10);
Console.WriteLine($"new instr 10: 3[0] {Convert.ToHexString(a)}");
File.WriteAllBytes(args[1], DCX.Compress(em.Write(), comp));
