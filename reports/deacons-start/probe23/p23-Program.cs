using SoulsFormats;
// Probe 23 (2026-11-25, Deacons of the Deep fight start, thread "Make Deacons of the Deep fight start").
// p23 watch <m45_05_00_00.emevd.dcx in> <out>
//   Adds watcher events 45059950-45059954 to the ported Cathedral script, started from event 0. Each waits for one
//   condition and then shows an Elden Ring banner (2007[02]), so screenshots show which step of the fight start ran:
//     31 "Commence"            player (10000) inside area 45052800 (the doorway box), on its own
//     20 "Duty Fulfilled"      entry flag 1030297714 (DS3 13505805) ON, on its own                      (+1 s)
//     33 "Victory"             both together (what DS3 13505810 = 1030297625 waits for)                 (+3 s)
//     18 "Enemy Felled"        flag 1030297749 (DS3 13500801) ON: 1030297625 passed its wait            (+6 s)
//     13 "Lost Grace Discovered" flag 1030297598 (defeated) ON at any time (should never show)
static byte[] Le(params object[] parts)
{
    var ms = new MemoryStream();
    foreach (var p in parts)
        ms.Write(p switch
        {
            int i => BitConverter.GetBytes(i), uint u => BitConverter.GetBytes(u), float f => BitConverter.GetBytes(f),
            byte b => new[] { b }, sbyte s => new[] { (byte)s }, _ => throw new ArgumentException(p.ToString()),
        });
    return ms.ToArray();
}
static EMEVD.Instruction Flag(sbyte group, uint flag) => new(3, 0, Le(group, (byte)1, (byte)0, (byte)0, flag));
static EMEVD.Instruction InArea(sbyte group, uint area) => new(3, 2, Le(group, (byte)1, (byte)0, (byte)0, 10000u, area, 1));
static EMEVD.Instruction Group(sbyte target) => new(0, 0, Le((sbyte)0, (byte)1, target, (byte)0));
static EMEVD.Instruction Wait(float s) => new(1001, 0, Le(s));
static EMEVD.Instruction Banner(byte type) => new(2007, 2, Le(type, (byte)0, (byte)0, (byte)0));

if (args.Length == 3 && args[0] == "warp")
{
    // Probe 24: banners inside the F1 warp event (common.emevd, 1039291408): 13 after its 3 s wait (before 3[08]
    // IF Player In Map), 22 after 3[08] passed. Plus, with "watch", a watcher in the map script on 3[08] alone.
    var cm = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(args[1]), out var cc));
    var w = cm.Events.First(e => e.ID == 1039291408);
    int inMap = w.Instructions.FindIndex(i => i.Bank == 3 && i.ID == 8);
    if (inMap < 1 || w.Instructions[inMap - 1] is not { Bank: 1001, ID: 0 }) throw new InvalidDataException("1039291408 layout");
    w.Instructions.Insert(inMap + 1, Banner(22));
    w.Instructions.Insert(inMap, Banner(13));
    foreach (var p in w.Parameters) if (p.InstructionIndex >= inMap) p.InstructionIndex += p.InstructionIndex > inMap ? 2 : 1;
    File.WriteAllBytes(args[2], DCX.Compress(cm.Write(), cc));
    Console.WriteLine($"1039291408: banner 13 before 3[08] (index {inMap}), banner 22 after it; wrote {args[2]}");
    return;
}
if (args.Length == 3 && args[0] == "split")
{
    // Probe 25: the fix design, patched by hand. 1039291408 turns pending flag 1039292990 on just before its warp;
    // a new event 1039291990 (started from event 0, so it starts again with every load) waits for that flag AND the
    // player in area 45052800, turns the flag off, sets entry 1039291408 would have set (1030297714), banner 20.
    var cm = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(args[1]), out var cc));
    var w = cm.Events.First(e => e.ID == 1039291408);
    int warp = w.Instructions.FindIndex(i => i.Bank == 2003 && i.ID == 14);
    if (warp < 0) throw new InvalidDataException("1039291408 has no warp");
    static EMEVD.Instruction Set(uint flag, byte on) => new(2003, 66, Le((byte)0, (byte)0, (byte)0, (byte)0, flag, on, (byte)0, (byte)0, (byte)0));
    w.Instructions.Insert(warp, Set(1039292990, 1));
    foreach (var p in w.Parameters) if (p.InstructionIndex >= warp) p.InstructionIndex++;
    cm.Events.RemoveAll(e => e.ID == 1039291990);
    var ev = new EMEVD.Event(1039291990, EMEVD.Event.RestBehaviorType.Default);
    ev.Instructions.AddRange([Flag(1, 1039292990), InArea(1, 45052800), Group(1), Set(1039292990, 0), Set(1030297714, 1), Wait(1), Banner(20)]);
    cm.Events.Add(ev);
    cm.Events.First(e => e.ID == 0).Instructions.Add(new EMEVD.Instruction(2000, 0, Le(0, 1039291990u, 0u)));
    File.WriteAllBytes(args[2], DCX.Compress(cm.Write(), cc));
    Console.WriteLine($"1039291408: pending flag on before the warp (index {warp}); event 1039291990 sets the entry flag after the load; wrote {args[2]}");
    return;
}
if (args.Length != 3 || args[0] != "watch") { Console.WriteLine("p23 watch|warp <in.emevd.dcx> <out.emevd.dcx>"); return; }
var em = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(args[1]), out var comp));
var init = em.Events.First(e => e.ID == 0);
uint id = 45059950;
void Add(string what, params EMEVD.Instruction[] ins)
{
    em.Events.RemoveAll(e => e.ID == id);
    var ev = new EMEVD.Event(id, EMEVD.Event.RestBehaviorType.Default);
    ev.Instructions.AddRange(ins);
    em.Events.Add(ev);
    init.Instructions.Add(new EMEVD.Instruction(2000, 0, Le(0, id, 0u)));
    Console.WriteLine($"event {id}: {what}");
    id++;
}
const uint Area = 45052800, Entry = 1030297714, Passed = 1030297749, Defeated = 1030297598;
Add("in area -> banner 31", InArea(1, Area), Group(1), Banner(31));
Add("entry flag -> +1 s banner 20", Flag(1, Entry), Group(1), Wait(1), Banner(20));
Add("entry flag AND in area -> +3 s banner 33", Flag(1, Entry), InArea(1, Area), Group(1), Wait(3), Banner(33));
Add("13505810 passed (flag 1030297749) -> +6 s banner 18", Flag(1, Passed), Group(1), Wait(6), Banner(18));
Add("defeated flag -> banner 2", Flag(1, Defeated), Group(1), Banner(2));
Add("3[08] player in map 45 5 0 0 -> +2 s banner 16", new EMEVD.Instruction(3, 8, Le((sbyte)1, (byte)1, (sbyte)45, (sbyte)5, (sbyte)0, (sbyte)0, (byte)0, (byte)0)), Group(1), Wait(2), Banner(16));
File.WriteAllBytes(args[2], DCX.Compress(em.Write(), comp));
Console.WriteLine($"wrote {args[2]}");
