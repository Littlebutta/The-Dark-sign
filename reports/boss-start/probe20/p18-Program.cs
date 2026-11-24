using System.Numerics;
using SoulsFormats;
// Probe 18 (2026-11-24): does an event-script prompt (3[24]) show anywhere under the mod?
//
// p18 inject <in.emevd.dcx> <out.emevd.dcx> <eventBase> <button>:<entity> [<button>:<entity> ...]
//     One test event per pair, started from event 0: wait 3[24] (main group) for that ActionButtonParam row on that
//     entity, then force the player (10000) into anim 60750 (the sword pull, which played in probe 16 run B).
// p18 near <map.msb.dcx> <x> <y> <z> [count]
//     Parts with an entity ID nearest a point: type, model, entity, TalkID (enemies), NpcParam, distance.
// p18 bypass <in.emevd.dcx> <out.emevd.dcx> [nosp]
//     Probe 16 run B again (45005811's 3[24] -> 3[00] flag 45000802 OFF, the player plays 60750 at the start).
//     With "nosp", 45005810's 2004[08] SpEffect 700005800 becomes a 0 s wait, to split the instant defeat.
// p18 drop <in.emevd.dcx> <out.emevd.dcx> <event>:<index> [...]
//     Replaces those instructions with a 0 s wait (probe 19: 45005810:11 = Gundyr's hitmask bit 0 off,
//     45005810:9 = his invincibility on).
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
static EMEVD.Instruction ForceAnim(int entity, int anim) => new(2003, 18, Le(entity, anim, (byte)0, (byte)0, (byte)0, (byte)0, 1f));
static EMEVD.Instruction Start(uint id) => new(2000, 0, Le(0, id));

switch (args[0])
{
    case "inject":
    {
        var em = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(args[1]), out var comp));
        var init = em.Events.First(e => e.ID == 0);
        var id = uint.Parse(args[3]);
        foreach (var pair in args.Skip(4))
        {
            var (button, entity) = (int.Parse(pair.Split(':')[0]), uint.Parse(pair.Split(':')[1]));
            var ev = new EMEVD.Event(id, EMEVD.Event.RestBehaviorType.Default);
            ev.Instructions.Add(new EMEVD.Instruction(3, 24, Le((sbyte)0, (byte)0, (byte)0, (byte)0, button, entity)));
            ev.Instructions.Add(ForceAnim(10000, 60750));
            em.Events.Add(ev);
            init.Instructions.Add(Start(id));
            Console.WriteLine($"event {id}: 3[24] button {button} on entity {entity} -> player anim 60750");
            id++;
        }
        File.WriteAllBytes(args[2], DCX.Compress(em.Write(), comp));
        break;
    }
    case "near":
    {
        var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(args[1])));
        var at = new Vector3(float.Parse(args[2]), float.Parse(args[3]), float.Parse(args[4]));
        var count = args.Length > 5 ? int.Parse(args[5]) : 25;
        foreach (var p in msb.Parts.GetEntries().Where(p => p.EntityID != 0).OrderBy(p => Vector3.Distance(p.Position, at)).Take(count))
        {
            var extra = p is MSBE.Part.Enemy en ? $"\tTalkID {en.TalkID}\tNpcParam {en.NPCParamID}\tThink {en.ThinkParamID}" : "";
            Console.WriteLine($"{p.GetType().Name}\t{p.Name}\tmodel {p.ModelName}\tentity {p.EntityID}\t{Vector3.Distance(p.Position, at):0.0} m\tat {p.Position}{extra}");
        }
        break;
    }
    case "bypass":
    {
        var em = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(args[1]), out var comp));
        var ev = em.Events.First(e => e.ID == 45005811);
        if (ev.Instructions[10] is not { Bank: 3, ID: 24 })
            throw new InvalidDataException("45005811 instruction 10 is not 3[24]");
        ev.Instructions[10] = new EMEVD.Instruction(3, 0, Le((sbyte)-1, (byte)0, (byte)0, (byte)0, 45000802u));
        ev.Parameters.RemoveAll(p => p.InstructionIndex == 10);
        ev.Instructions.InsertRange(0, [ForceAnim(10000, 60750), new EMEVD.Instruction(1001, 0, Le(2f))]);
        foreach (var p in ev.Parameters) p.InstructionIndex += 2;
        Console.WriteLine("45005811: 3[24] -> 3[00] flag 45000802 OFF, player anim 60750 at the start");
        if (args.Length > 3 && args[3] == "nosp")
        {
            var boss = em.Events.First(e => e.ID == 45005810);
            var i = boss.Instructions.FindIndex(x => x.Bank == 2004 && x.ID == 8 && BitConverter.ToInt32(x.ArgData, 4) == 700005800);
            if (i < 0) throw new InvalidDataException("45005810 has no 2004[08] 700005800");
            boss.Instructions[i] = new EMEVD.Instruction(1001, 0, Le(0f));
            Console.WriteLine($"45005810: instruction {i} (SpEffect 700005800) -> wait 0 s");
        }
        File.WriteAllBytes(args[2], DCX.Compress(em.Write(), comp));
        break;
    }
    case "drop":
    {
        var em = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(args[1]), out var comp));
        foreach (var pair in args.Skip(3))
        {
            var (id, index) = (long.Parse(pair.Split(':')[0]), int.Parse(pair.Split(':')[1]));
            var ev = em.Events.First(e => e.ID == id);
            var old = ev.Instructions[index];
            ev.Instructions[index] = new EMEVD.Instruction(1001, 0, Le(0f));
            ev.Parameters.RemoveAll(p => p.InstructionIndex == index);
            Console.WriteLine($"{id}:{index} {old.Bank}[{old.ID:D2}] {Convert.ToHexString(old.ArgData)} -> wait 0 s");
        }
        File.WriteAllBytes(args[2], DCX.Compress(em.Write(), comp));
        break;
    }
    case "warp":
    {
        // p18 warp <in> <out> [seconds]: a new event from event 0 waits, then short-warps the player to Gundyr's dummy 100
        // with 45005811's own 2004[41] (the spot the pull itself warps to), so the walk-up can't fall short.
        var em = EMEVD.Read(DCX.Decompress(File.ReadAllBytes(args[1]), out var comp));
        var warp = em.Events.First(e => e.ID == 45005811).Instructions.First(i => i.Bank == 2004 && i.ID == 41);
        var ev = new EMEVD.Event(45005995, EMEVD.Event.RestBehaviorType.Default);
        ev.Instructions.Add(new EMEVD.Instruction(1001, 0, Le(args.Length > 3 ? float.Parse(args[3]) : 4f)));
        ev.Instructions.Add(new EMEVD.Instruction(2004, 41, (byte[])warp.ArgData.Clone()));
        em.Events.Add(ev);
        em.Events.First(e => e.ID == 0).Instructions.Add(Start(45005995));
        Console.WriteLine($"45005995: wait, then 2004[41] {Convert.ToHexString(warp.ArgData)}");
        File.WriteAllBytes(args[2], DCX.Compress(em.Write(), comp));
        break;
    }
    default:
        Console.WriteLine("p18 inject|near|bypass ...");
        break;
}
