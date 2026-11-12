using System.Text.RegularExpressions;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// DS3 attacks a ported enemy's Elden Ring template has no slot for (Pontiff Sulyvahn's 3033-3038 on the Godskin
/// Apostle, who stops at 3032). Once the enemy thinks with DS3's AI (<see cref="AiPort"/>) it asks for DS3's attack
/// IDs, and an attack the character's files don't have doesn't play: the behaviour has no state for it and the
/// animation binder no clip. For each such attack, the same way for every enemy:
/// <list type="bullet">
/// <item>animations: a file named for the attack next to the template's nearest attack (same binder, so the binder's
/// .blf rules and compendium hold), which the animation port then fills with DS3's clip as it does every other move,
/// and a TAE entry cloned from that attack's, whose events the port replaces with DS3's;</item>
/// <item>behaviour (c9997.hkx in the character's behbnd): a state in Attack_SM cloned from the nearest attack's, with
/// its selector playing the new clip (registered in the graph's animation list, whose index is a clip's internal
/// ID), and the two global wildcard transitions Elden Ring gives every attack
/// (W_Attack&lt;id&gt; for the AI, W_Event&lt;id&gt; for scripts), with the new event names.</item>
/// </list>
/// The same goes for the waiting poses, getting-up moves and map-script moves DS3's map scripts force (see
/// <see cref="Families"/>): the Hollow Soldiers of the High Wall wait crouched (702) and stand up (1702) before
/// bursting into the Pus of Man, and a forced animation the graph has no state for never plays, so the map script
/// waiting for it to finish never goes on.
/// Fallbacks "enemy-ai": DARKSIGN_ENEMY_EXTRA_MOVES=0 leaves the template's slots only; =attacks adds attacks only.
/// Method (RULE 3): the Attack_SM / CustomManualSelectorGenerator layout is Elden Ring's own c9997.hkx read with HKLib;
/// cloning a state and its W_Attack event is how the community adds an attack to an Elden Ring enemy, and the clip
/// registration follows <see cref="MovesetPort"/>'s for the player's weapon categories.
/// </summary>
public static partial class ChrMoves
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_EXTRA_MOVES") != "0";

    /// <summary>DARKSIGN_ENEMY_EXTRA_MOVES=attacks: attacks only, no waiting poses or map-script moves (as up to 1.0.383).</summary>
    public static bool AttacksOnly => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_EXTRA_MOVES")?.Trim().ToLowerInvariant() == "attacks";

    public const int FirstAttack = 3000, LastAttack = 3999;
    public static bool IsAttack(int id) => id is >= FirstAttack and <= LastAttack;

    /// <summary>
    /// A range of animation IDs Elden Ring's character graph (c9997.hkx) plays from one state machine, a state per ID
    /// reached by a global wildcard "W_&lt;state name&gt;" (and W_Event&lt;id&gt; for attacks): attacks the AI asks for,
    /// and the poses and moves DS3's map scripts force (the Hollow Soldier waiting crouched in 702 and getting up with
    /// 1702 before common event 20005415 has it burst into the Pus of Man).
    /// </summary>
    public sealed record Family(string Name, int Min, int Max, string Machine)
    {
        public bool Contains(int id) => id >= Min && id <= Max;
    }

    public static readonly IReadOnlyList<Family> Families =
    [
        new("attack", FirstAttack, LastAttack, "Attack_SM"),
        new("waiting pose", 700, 799, "IdleUniques_SM"),
        new("getting up", 1700, 1749, "IdleUniqueToIdle_SM"),
        new("map-script move", 20000, 29999, "EventSingle_SM"),
        new("map-script loop", 30000, 39999, "EventLoop_SM"),
    ];

    public static Family? FamilyOf(int id) => Families.FirstOrDefault(f => f.Contains(id));

    /// <summary>The IDs that may be added: attacks only with DS3's AI (which is what asks for them), the rest with any AI.</summary>
    public static bool Wanted(int id, bool ds3Ai) => FamilyOf(id) is { } f && (f.Name == "attack" ? ds3Ai : !AttacksOnly);

    [GeneratedRegex(@"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase)]
    private static partial Regex HkxLeaf();

    /// <summary>A new attack and the template attack it was cloned from.</summary>
    public readonly record struct Added(int Id, int Donor);

    /// <summary>
    /// The DS3 animations of a <see cref="Families">family</see> (section 0) the template has neither a TAE entry nor an
    /// animation file for, each with the template's nearest ID of the same family that has both (and isn't additive).
    /// Nothing for a family the template has none of.
    /// </summary>
    public static List<Added> Plan(IEnumerable<int> ds3Attacks, IReadOnlySet<int> templateEntries, IReadOnlySet<int> templateFiles, IReadOnlySet<int> additive)
    {
        var plan = new List<Added>();
        var ds3 = ds3Attacks.Where(id => !templateEntries.Contains(id) && !templateFiles.Contains(id)).Distinct().Order().ToList();
        foreach (var family in Families)
        {
            // The donor is the template's nearest ID of the same family: its state's place in the graph, its blends
            // and its listeners are what the new ID's state gets.
            var donors = templateEntries.Where(id => family.Contains(id) && templateFiles.Contains(id) && !additive.Contains(id)).Order().ToList();
            if (donors.Count == 0)
                continue;
            plan.AddRange(ds3.Where(family.Contains).Select(id => new Added(id, donors.MinBy(d => (Math.Abs(d - id), d)))));
        }
        return plan.OrderBy(a => a.Id).ToList();
    }

    /// <summary>
    /// Step 1, the animations: for each planned attack a copy of its donor's file (same binder, ID and path for the new
    /// number) and a TAE entry cloned from the donor's in the section 0 TAE. Returns the attacks added.
    /// </summary>
    public static List<Added> AddAnimations(IEnumerable<BND4> anibnds, IReadOnlyList<Added> plan)
    {
        var added = new List<Added>();
        if (plan.Count == 0)
            return added;
        var binders = anibnds.ToList();
        var tae = binders.SelectMany(b => b.Files).Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(f => ChrPort.TaeSection(f.Name) == 0);
        if (tae is null)
            return added;
        var taeFile = TAE.Read(tae.Bytes);
        foreach (var a in plan)
        {
            var donorEntry = taeFile.Animations.FirstOrDefault(x => x.ID == a.Donor);
            var (bnd, donorFile) = binders.SelectMany(b => b.Files.Select(f => (b, f)))
                .FirstOrDefault(p => HkxLeaf().Match(ItemText.Leaf(p.f.Name)) is { Success: true } m && int.Parse(m.Groups[1].Value) == 0 && int.Parse(m.Groups[2].Value) == a.Donor);
            if (donorEntry is null || donorFile is null || taeFile.Animations.Any(x => x.ID == a.Id))
                continue;
            long id = donorFile.ID - a.Donor + a.Id;
            if (bnd.Files.Any(f => f.ID == id))
                continue;
            var leaf = ItemText.Leaf(donorFile.Name);
            var name = donorFile.Name[..^leaf.Length] + $"a000_{a.Id:D6}.hkx";
            bnd.Files.Add(new BinderFile(donorFile.Flags, (int)id, name, (byte[])donorFile.Bytes.Clone()));
            bnd.Files.Sort((x, y) => x.ID.CompareTo(y.ID));
            taeFile.Animations.Add(CloneEntry(donorEntry, a.Id));
            added.Add(a);
        }
        taeFile.Animations.Sort((x, y) => x.ID.CompareTo(y.ID));
        tae.Bytes = taeFile.Write();
        return added;
    }

    /// <summary>A TAE entry under a new ID: the donor's header and events (with its event groups cloned), the new file name.</summary>
    public static TAE.Animation CloneEntry(TAE.Animation donor, int id)
    {
        var header = donor.MiniHeader?.GetClone() ?? new TAE.Animation.AnimMiniHeader.Standard();
        var entry = new TAE.Animation(id, header, $"a000_{id:D6}.hkt");
        var groups = new Dictionary<TAE.EventGroup, TAE.EventGroup>(ReferenceEqualityComparer.Instance);
        foreach (var g in donor.EventGroups ?? [])
            entry.EventGroups.Add(groups[g] = g.GetClone());
        foreach (var e in donor.Events)
        {
            TAE.EventGroup? group = null;
            if (e.Group is { } g && !groups.TryGetValue(g, out group))
                entry.EventGroups.Add(group = groups[g] = g.GetClone());
            entry.Events.Add(new TAE.Event(e.StartTime, e.EndTime, e.Type, e.Unk04, (byte[])e.GetParameterBytes(false).Clone(), false) { Group = group });
        }
        return entry;
    }

    /// <summary>
    /// Step 2, the behaviour: the character's behbnd file holding Attack_SM (c9997.hkx) gets a state per added attack.
    /// Returns how many states were added (0 leaves the file as it was).
    /// </summary>
    public static int AddStates(BND4 behbnd, IReadOnlyList<Added> added, List<string>? notes = null)
    {
        if (added.Count == 0)
            return 0;
        var compendium = behbnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
        foreach (var file in behbnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
        {
            lock (AnimPort.HavokLock)
            {
                IHavokObject root;
                try { root = AnimPort.ReadEr(file.Bytes, compendium); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
                int states = AddStates(root, added);
                if (states < 0)
                    continue;
                using var output = new MemoryStream();
                new HavokBinarySerializer().Write(root, output);
                file.Bytes = output.ToArray();
                return states;
            }
        }
        notes?.Add("extra DS3 attacks: no behaviour file with Attack_SM");
        return 0;
    }

    /// <summary>The states for a graph already read; -1 when it has no Attack_SM (not the character's main graph).</summary>
    public static int AddStates(IHavokObject root, IReadOnlyList<Added> added)
    {
        var all = HavokGraph.All(root);
        var machines = all.OfType<hkbStateMachine>().Where(s => s.m_name is not null).GroupBy(s => s.m_name).ToDictionary(g => g.Key, g => g.First());
        var strings = all.OfType<hkbBehaviorGraphStringData>().FirstOrDefault();
        var data = all.OfType<hkbBehaviorGraphData>().FirstOrDefault();
        if (!machines.ContainsKey("Attack_SM") || strings is null || data is null)
            return -1;
        int count = 0;
        foreach (var a in added)
        {
            if (FamilyOf(a.Id) is not { } family || !machines.TryGetValue(family.Machine, out var sm))
                continue;
            if (sm.m_states.Any(s => s.m_generator is CustomManualSelectorGenerator c && c.m_animId == a.Id))
                continue;
            var donor = sm.m_states.FirstOrDefault(s => s.m_name == $"Attack{a.Donor}" && s.m_generator is CustomManualSelectorGenerator)
                        ?? sm.m_states.FirstOrDefault(s => s.m_generator is CustomManualSelectorGenerator c && c.m_animId == a.Donor);
            if (donor?.m_generator is not CustomManualSelectorGenerator donorSelector)
                continue;
            int nextState = sm.m_states.Count == 0 ? 0 : sm.m_states.Max(s => s.m_stateId) + 1;
            sm.m_wildcardTransitions ??= new hkbStateMachine.TransitionInfoArray();
            var wildcards = sm.m_wildcardTransitions.m_transitions;
            // Names follow the donor's (Attack3010 / Attack3010_CMSG, IdleUnique700 / IdleUnique700_CMSG,
            // IdleUniqueToIdle1700 / TransToIdles1700_CMSG, Event20000 / Event20000_CMSG).
            string Renamed(string? name, string fallback) =>
                name is not null && name.Contains(a.Donor.ToString(), StringComparison.Ordinal) ? name.Replace(a.Donor.ToString(), a.Id.ToString(), StringComparison.Ordinal) : fallback;
            var selector = HavokGraph.ShallowCopy(donorSelector);
            selector.m_animId = a.Id;
            selector.m_name = Renamed(donorSelector.m_name, $"Attack{a.Id}_CMSG");
            selector.m_generators = [];
            var name = $"a000_{a.Id:D6}";
            var donorClip = donorSelector.m_generators.OfType<hkbClipGenerator>().FirstOrDefault(c => c.m_animationName?.StartsWith("a000_", StringComparison.Ordinal) == true);
            if (donorClip is null)
                continue;
            // A clip's internal ID is its index in the graph's animation list ("..\Model\chr\c4340\hkx\a000_003000.hkx").
            var animations = strings.m_animationNames;
            string path = donorClip.m_animationInternalId >= 0 && donorClip.m_animationInternalId < animations.Count && animations[donorClip.m_animationInternalId] is { } donorPath
                ? donorPath[..(donorPath.LastIndexOf('\\') + 1)] + name + ".hkx" : name + ".hkx";
            int index = animations.FindIndex(n => n is not null && n.EndsWith("\\" + name + ".hkx", StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                index = animations.Count;
                animations.Add(path);
            }
            short internalId = checked((short)index);
            foreach (var clip in donorSelector.m_generators.OfType<hkbClipGenerator>().Where(c => c.m_animationName?.StartsWith("a000_", StringComparison.Ordinal) == true))
            {
                var copy = HavokGraph.ShallowCopy(clip);
                copy.m_name = clip.m_name?.Replace(clip.m_animationName!, name, StringComparison.Ordinal);
                copy.m_animationName = name;
                copy.m_animationInternalId = internalId;
                selector.m_generators.Add(copy);
            }
            var state = HavokGraph.ShallowCopy(donor);
            state.m_stateId = nextState++;
            state.m_name = Renamed(donor.m_name, $"Attack{a.Id}");
            state.m_generator = selector;
            state.m_listeners = [.. donor.m_listeners];
            sm.m_states.Add(state);
            foreach (var t in wildcards.Where(t => t.m_toStateId == donor.m_stateId).ToList())
            {
                if (t.m_eventId < 0 || t.m_eventId >= strings.m_eventNames.Count)
                    continue;
                var eventName = strings.m_eventNames[t.m_eventId];
                // Only the wildcard events (W_Attack3010, W_Event3010, W_IdleUnique700...): a named blend from
                // another state (IdleToIdleUnique1750_to_IdleUnique700) is the donor's own and stays with it.
                if (!eventName.StartsWith("W_", StringComparison.Ordinal) || !eventName.EndsWith(a.Donor.ToString(), StringComparison.Ordinal))
                    continue;
                var newName = eventName[..^a.Donor.ToString().Length] + a.Id;
                int eventId = strings.m_eventNames.IndexOf(newName);
                if (eventId < 0)
                {
                    eventId = strings.m_eventNames.Count;
                    strings.m_eventNames.Add(newName);
                    if (t.m_eventId < data.m_eventInfos.Count)
                        data.m_eventInfos.Add(HavokGraph.ShallowCopy(data.m_eventInfos[t.m_eventId]));
                }
                var copy = HavokGraph.ShallowCopy(t);
                copy.m_eventId = eventId;
                copy.m_toStateId = state.m_stateId;
                wildcards.Add(copy);
            }
            count++;
        }
        return count;
    }
}
