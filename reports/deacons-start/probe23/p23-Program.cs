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

if (args.Length != 3 || args[0] != "watch") { Console.WriteLine("p23 watch <in.emevd.dcx> <out.emevd.dcx>"); return; }
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
Add("defeated flag -> banner 13", Flag(1, Defeated), Group(1), Banner(13));
File.WriteAllBytes(args[2], DCX.Compress(em.Write(), comp));
Console.WriteLine($"wrote {args[2]}");
