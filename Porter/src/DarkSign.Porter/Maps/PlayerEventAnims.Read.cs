using SoulsFormats;

namespace DarkSign.Porter.Maps;

public static partial class PlayerEventAnims
{
    /// <summary>The player's entity ID in both games' scripts.</summary>
    public const int Player = 10000;

    /// <summary>One place a DS3 script plays an animation on the player.</summary>
    public readonly record struct Use(int Anim, long Event, string Instruction, bool PassedIn);

    /// <summary>
    /// Every animation the scripts play on the player, read with DS3's instruction definitions: any instruction with
    /// an entity and an animation argument (Force/Request Animation Playback, and Rotate Character, which DS3 uses to
    /// walk the player to a spot and into a pose: the level-up kneel is "Rotate Character(player, Fire Keeper,
    /// 90250)"), plus 2003[72] (<see cref="PlayerSceneWeapons.PairedAnimId"/>). An entity or animation the event
    /// takes from its parameters is looked up in every line that starts the event (InitializeEvent 2000[00]: slot,
    /// event, arguments; InitializeCommonEvent 2000[06]: event, arguments), so the kneel's 90250 and stand-up 90260,
    /// which common event 20006005 is handed by Firelink's map script, are found too.
    /// </summary>
    /// <param name="unresolved">Lines whose entity or animation is passed in by an event nothing in these scripts starts.</param>
    public static List<Use> Scripted(IReadOnlyCollection<EMEVD> scripts, Emedf ds3Defs, out int unresolved)
    {
        unresolved = 0;
        var uses = new List<Use>();
        var starts = new Dictionary<long, List<byte[]>>();
        foreach (var ins in scripts.SelectMany(s => s.Events).SelectMany(e => e.Instructions))
        {
            if (ins.Bank != 2000 || ins.ID is not (0 or 6))
                continue;
            int at = ins.ID == 0 ? 4 : 0;
            if (ins.ArgData.Length < at + 4)
                continue;
            long id = BitConverter.ToUInt32(ins.ArgData, at);
            if (!starts.TryGetValue(id, out var list))
                starts[id] = list = [];
            list.Add(ins.ArgData[(at + 4)..]);
        }

        var defs = ds3Defs.All
            .Select(d => (Def: d, Entity: d.Args.FirstOrDefault(a => a.Name.Contains("Entity", StringComparison.OrdinalIgnoreCase)),
                Anims: d.Args.Where(a => a.Name.Contains("Animation", StringComparison.OrdinalIgnoreCase)).ToList()))
            .Where(d => d.Entity is not null && d.Anims.Count > 0 && !(d.Def.Bank == PlayerSceneWeapons.PairedAnimBank && d.Def.Id == PlayerSceneWeapons.PairedAnimId))
            .ToDictionary(d => (d.Def.Bank, d.Def.Id));
        foreach (var ev in scripts.SelectMany(s => s.Events))
            for (int i = 0; i < ev.Instructions.Count; i++)
            {
                var ins = ev.Instructions[i];
                var calls = starts.GetValueOrDefault(ev.ID) ?? [];
                EMEVD.Parameter? Passed(long offset) => ev.Parameters.FirstOrDefault(p => p.InstructionIndex == i && p.TargetStartByte == offset);
                // The values an argument takes: its own, or one per line that starts the event.
                List<long>? Values(long offset, Func<long> own, ref int missing)
                {
                    if (Passed(offset) is not { } p)
                        return null;
                    var values = calls.Where(c => c.Length >= p.SourceStartByte + 4).Select(c => (long)BitConverter.ToInt32(c, (int)p.SourceStartByte)).ToList();
                    if (values.Count == 0)
                        missing++;
                    return values;
                }
                if (ins.Bank == PlayerSceneWeapons.PairedAnimBank && ins.ID == PlayerSceneWeapons.PairedAnimId)
                {
                    if (ins.ArgData.Length < 9 || ins.ArgData[8] == 0 && Passed(8) is null)
                        continue;
                    int missing = 0;
                    var anims = Values(4, () => 0, ref missing);
                    foreach (var anim in anims ?? [BitConverter.ToInt32(ins.ArgData, 4)])
                        if (anim > 0)
                            uses.Add(new Use((int)anim, ev.ID, "2003[72]", anims is not null));
                    unresolved += missing;
                    continue;
                }
                if (!defs.TryGetValue((ins.Bank, ins.ID), out var def) || ins.ArgData.Length < def.Def.Length)
                    continue;
                foreach (var arg in def.Anims)
                {
                    int missing = 0;
                    var entities = Values(def.Entity!.Offset, () => 0, ref missing);
                    var anims = Values(arg.Offset, () => 0, ref missing);
                    if (entities is null && anims is null)
                    {
                        if (Emedf.Read(ins.ArgData, def.Entity) == Player && Emedf.Read(ins.ArgData, arg) > 0)
                            uses.Add(new Use((int)Emedf.Read(ins.ArgData, arg), ev.ID, def.Def.Name, false));
                        continue;
                    }
                    // Passed in: each starting line gives the entity and animation of one run of the event.
                    if (entities is null && Emedf.Read(ins.ArgData, def.Entity) != Player)
                        continue;
                    if (missing > 0)
                    {
                        // Only a line that could be the player's counts as unread.
                        unresolved++;
                        continue;
                    }
                    int runs = Math.Max(entities?.Count ?? 0, anims?.Count ?? 0);
                    for (int c = 0; c < runs; c++)
                    {
                        long entity = entities is null ? Player : entities[c];
                        long anim = anims is null ? Emedf.Read(ins.ArgData, arg) : anims[c];
                        if (entity == Player && anim > 0)
                            uses.Add(new Use((int)anim, ev.ID, def.Def.Name, true));
                    }
                }
            }
        return uses;
    }
}
