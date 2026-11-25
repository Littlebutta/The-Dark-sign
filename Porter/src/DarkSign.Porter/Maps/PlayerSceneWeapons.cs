using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's weapon hiding in the player animations that the ported DS3 map scripts play on the player.
///
/// A DS3 script plays some player animations by number ("Request Animation Playback" on the player, entity 10000), and
/// the event port passes those numbers through, so Elden Ring plays its own animation with that number. Gundyr's sword
/// pull plays 60750. DS3's 60750 hides both weapons for the pull with TAE event 710 (HideEquippedWeapon: left hand,
/// right hand, left with scabbard, right with scabbard, one byte each, the same layout in both games). Elden Ring's
/// 60750 has no 710, so the player pulled the sword with their own weapon still in hand (1.0.640 playtest, 2026-11-25).
///
/// For each player animation a ported script plays, this copies DS3's 710 events into the Elden Ring entry of the same
/// number in the mod's c0000.anibnd. Only entries Elden Ring has that DS3 hides weapons in and Elden Ring doesn't are
/// touched, and only those the ported scripts play, so Elden Ring's other scene animations stay as they are. The step
/// logs every entry it changes. Leaving this step out of port-maps undoes it.
/// </summary>
public static class PlayerSceneWeapons
{
    public const int HideWeaponEvent = 710;
    public const int Player = 10000;
    public const string ScriptInstruction = "Request Animation Playback";
    public const string PlayerBinder = "/chr/c0000.anibnd.dcx";

    /// <summary>
    /// The animation numbers the scripts play on the player, read with DS3's instruction definitions. A line whose
    /// entity or animation comes from the event's parameters is counted in <paramref name="passedIn"/>, not read.
    /// </summary>
    public static SortedSet<int> ScriptedPlayerAnims(IEnumerable<EMEVD> scripts, Emedf ds3Defs, out int passedIn)
    {
        passedIn = 0;
        var found = new SortedSet<int>();
        if (ds3Defs.Get(ScriptInstruction) is not { } def)
            return found;
        var entity = def.Args.FirstOrDefault(a => a.Name.Contains("Entity", StringComparison.OrdinalIgnoreCase));
        var anim = def.Args.FirstOrDefault(a => a.Name.Contains("Animation", StringComparison.OrdinalIgnoreCase));
        if (entity is null || anim is null)
            return found;
        foreach (var ev in scripts.SelectMany(s => s.Events))
            for (int i = 0; i < ev.Instructions.Count; i++)
            {
                var ins = ev.Instructions[i];
                if (ins.Bank != def.Bank || ins.ID != def.Id || ins.ArgData.Length < def.Length)
                    continue;
                bool Passed(Emedf.Arg a) => ev.Parameters.Any(p => p.InstructionIndex == i && p.TargetStartByte == a.Offset);
                if (Passed(entity) || Passed(anim))
                {
                    passedIn++;
                    continue;
                }
                if (Emedf.Read(ins.ArgData, entity) == Player)
                    found.Add((int)Emedf.Read(ins.ArgData, anim));
            }
        return found;
    }

    /// <summary>
    /// Copies DS3's 710 events into the Elden Ring entries of <paramref name="anims"/> that lack them. Returns one line
    /// per entry looked at.
    /// </summary>
    public static List<string> Apply(TAE ds3, TAE er, IEnumerable<int> anims)
    {
        var lines = new List<string>();
        var erLength = er.Animations.SelectMany(a => a.Events).Where(e => e.Type == HideWeaponEvent)
            .Select(e => e.GetParameterBytes(false).Length).FirstOrDefault();
        var proto = er.Animations.SelectMany(a => a.Events).FirstOrDefault(e => e.Type == HideWeaponEvent && e.Group is not null)?.Group;
        foreach (var id in anims)
        {
            var mine = ds3.Animations.FirstOrDefault(a => a.ID == id);
            var theirs = er.Animations.FirstOrDefault(a => a.ID == id);
            var hides = mine?.Events.Where(e => e.Type == HideWeaponEvent).ToList() ?? [];
            if (theirs is null) { lines.Add($"{id}: Elden Ring has no entry; left alone"); continue; }
            if (hides.Count == 0) { lines.Add($"{id}: DS3 hides no weapon in it; left alone"); continue; }
            if (theirs.Events.Any(e => e.Type == HideWeaponEvent)) { lines.Add($"{id}: Elden Ring's already hides weapons; left alone"); continue; }
            theirs.EventGroups ??= [];
            var group = theirs.Events.FirstOrDefault(e => e.Type == HideWeaponEvent)?.Group;
            if (group is null)
            {
                group = proto?.GetClone() ?? new TAE.EventGroup(HideWeaponEvent);
                theirs.EventGroups.Add(group);
            }
            foreach (var e in hides)
            {
                var bytes = e.GetParameterBytes(false);
                if (erLength > 0 && bytes.Length != erLength)
                    Array.Resize(ref bytes, erLength);
                theirs.Events.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, bytes, false) { Group = group });
            }
            lines.Add($"{id}: {hides.Count} weapon hide(s) from DS3 ({string.Join(", ", hides.Select(h => $"{h.StartTime:F3}-{h.EndTime:F3} s"))})");
        }
        return lines;
    }

    /// <summary>The port-maps step: the ported maps' DS3 scripts, DS3's player TAE, the mod's player binder.</summary>
    public static string Run(PorterContext ctx, GameArchives ds3, IReadOnlyList<MapSlots.Slot> slots)
    {
        var modPath = ctx.ModPath("chr", "c0000.anibnd.dcx");
        if (!File.Exists(modPath))
            return "player scene weapons: the mod has no c0000.anibnd (port-weapons not run); Elden Ring's player animations kept";
        if (!ds3.Contains(PlayerBinder))
            return "player scene weapons: DS3 has no c0000.anibnd; nothing to copy";
        var (maps, common) = MapEvents.ReadDs3(ds3, slots);
        var scripts = maps.Values.Concat(common is null ? [] : [common]);
        var defs = Emedf.Parse(File.ReadAllText(ctx.Data.Emedf("ds3-common")));
        var anims = ScriptedPlayerAnims(scripts, defs, out int passedIn);
        if (anims.Count == 0)
            return $"player scene weapons: the ported scripts play no player animation by number ({passedIn} passed in)";

        TAE.ValidateEventBank = false;
        var ds3Bnd = BND4.Read(ds3.Read(PlayerBinder));
        var modBnd = BND4.Read(DCX.Decompress(File.ReadAllBytes(modPath), out var compression));
        var lines = new List<string>();
        int changed = 0;
        var ds3Taes = ds3Bnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => ItemText.Leaf(f.Name), f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in modBnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
        {
            if (!ds3Taes.TryGetValue(ItemText.Leaf(file.Name), out var ds3File))
                continue;
            var er = TAE.Read(file.Bytes);
            var here = anims.Where(id => er.Animations.Any(a => a.ID == id)).ToList();
            if (here.Count == 0)
                continue;
            var result = Apply(TAE.Read(ds3File.Bytes), er, here);
            lines.AddRange(result.Select(l => $"  {ItemText.Leaf(file.Name)} {l}"));
            if (result.Any(l => l.Contains("weapon hide(s) from DS3")))
            {
                file.Bytes = er.Write();
                changed++;
            }
        }
        if (changed > 0)
            File.WriteAllBytes(modPath, DCX.Compress(modBnd.Write(), compression));
        return $"player scene weapons: {anims.Count} player animation(s) played by the ported scripts ({string.Join(", ", anims)}), "
               + $"{passedIn} passed in; {changed} TAE file(s) changed" + (lines.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, lines) : "");
    }
}
