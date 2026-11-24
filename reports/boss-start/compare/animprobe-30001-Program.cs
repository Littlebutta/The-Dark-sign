using SoulsFormats;

// animprobe <in.emevd.dcx> <out.emevd.dcx>: adds event 45005999 (started from event 0) that forces anims 3012,
// 30002 (loop), 20003 on entity 45000800 with waits, copying the 2003[18] argument layout from event 45005810.
var bytes = File.ReadAllBytes(args[0]);
var raw = DCX.Decompress(bytes, out var compression);
var em = EMEVD.Read(raw);
var src = em.Events.First(e => e.ID == 45005810).Instructions.First(i => i.Bank == 2003 && i.ID == 18);
var wait = em.Events.SelectMany(e => e.Instructions).First(i => i.Bank == 1001 && i.ID == 0);
EMEVD.Instruction Anim(int anim, byte loop)
{
    var a = (byte[])src.ArgData.Clone();
    BitConverter.TryWriteBytes(a.AsSpan(0), 45000800);
    BitConverter.TryWriteBytes(a.AsSpan(4), anim);
    a[8] = loop; a[9] = 0; a[10] = 0;
    return new EMEVD.Instruction(2003, 18, a);
}
EMEVD.Instruction Wait(float s)
{
    var a = (byte[])wait.ArgData.Clone();
    BitConverter.TryWriteBytes(a.AsSpan(0), s);
    return new EMEVD.Instruction(1001, 0, a);
}
var ev = new EMEVD.Event(45005999, EMEVD.Event.RestBehaviorType.Default);
ev.Instructions.AddRange([Wait(40), Anim(30001, 1)]);
em.Events.RemoveAll(e => e.ID == 45005999);
em.Events.Add(ev);
var init = em.Events.First(e => e.ID == 0);
var start = init.Instructions.First(i => i.Bank == 2000 && i.ID == 0);
var sa = new byte[8];
BitConverter.TryWriteBytes(sa.AsSpan(0), 0);           // slot
BitConverter.TryWriteBytes(sa.AsSpan(4), 45005999u);   // event id

init.Instructions.Add(new EMEVD.Instruction(2000, 0, sa));
File.WriteAllBytes(args[1], DCX.Compress(em.Write(), compression));
Console.WriteLine($"src 2003[18] args {Convert.ToHexString(src.ArgData)}; wait args {Convert.ToHexString(wait.ArgData)}; start template {Convert.ToHexString(start.ArgData)}; events {em.Events.Count}");

