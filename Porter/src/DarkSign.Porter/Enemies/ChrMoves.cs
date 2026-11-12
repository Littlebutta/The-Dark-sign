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
/// Fallbacks "enemy-ai": DARKSIGN_ENEMY_EXTRA_MOVES=0 leaves the template's slots only.
/// Method (RULE 3): the Attack_SM / CustomManualSelectorGenerator layout is Elden Ring's own c9997.hkx read with HKLib;
/// cloning a state and its W_Attack event is how the community adds an attack to an Elden Ring enemy, and the clip
/// registration follows <see cref="MovesetPort"/>'s for the player's weapon categories.
/// </summary>
public static partial class ChrMoves
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_EXTRA_MOVES") != "0";

    public const int FirstAttack = 3000, LastAttack = 3999;
    public static bool IsAttack(int id) => id is >= FirstAttack and <= LastAttack;

    [GeneratedRegex(@"^a(\d{3})_(\d{6})\.hkx$", RegexOptions.IgnoreCase)]
    private static partial Regex HkxLeaf();

    /// <summary>A new attack and the template attack it was cloned from.</summary>
    public readonly record struct Added(int Id, int Donor);

    /// <summary>
    /// The DS3 attacks (section 0, 3000-3999) the template has neither a TAE entry nor an animation file for, each
    /// with the template's nearest attack that has both (and isn't additive). Nothing when the template has none.
    /// </summary>
    public static List<Added> Plan(IEnumerable<int> ds3Attacks, IReadOnlySet<int> templateEntries, IReadOnlySet<int> templateFiles, IReadOnlySet<int> additive)
    {
        var donors = templateEntries.Where(id => IsAttack(id) && templateFiles.Contains(id) && !additive.Contains(id)).Order().ToList();
        if (donors.Count == 0)
            return [];
        return ds3Attacks.Where(id => IsAttack(id) && !templateEntries.Contains(id) && !templateFiles.Contains(id)).Distinct().Order()
            .Select(id => new Added(id, donors.MinBy(d => (Math.Abs(d - id), d)))).ToList();
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

    /// <summary>The states for a graph already read; -1 when it has no Attack_SM.</summary>
    public static int AddStates(IHavokObject root, IReadOnlyList<Added> added)
    {
        var all = HavokGraph.All(root);
        var sm = all.OfType<hkbStateMachine>().FirstOrDefault(s => s.m_name == "Attack_SM");
        var strings = all.OfType<hkbBehaviorGraphStringData>().FirstOrDefault();
        var data = all.OfType<hkbBehaviorGraphData>().FirstOrDefault();
        if (sm is null || strings is null || data is null)
            return -1;
        int nextState = sm.m_states.Count == 0 ? 0 : sm.m_states.Max(s => s.m_stateId) + 1;
        sm.m_wildcardTransitions ??= new hkbStateMachine.TransitionInfoArray();
        var wildcards = sm.m_wildcardTransitions.m_transitions;
        int count = 0;
        foreach (var a in added)
        {
            if (sm.m_states.Any(s => s.m_generator is CustomManualSelectorGenerator c && c.m_animId == a.Id && s.m_name == $"Attack{a.Id}"))
                continue;
            var donor = sm.m_states.FirstOrDefault(s => s.m_name == $"Attack{a.Donor}" && s.m_generator is CustomManualSelectorGenerator)
                        ?? sm.m_states.FirstOrDefault(s => s.m_generator is CustomManualSelectorGenerator c && c.m_animId == a.Donor);
            if (donor?.m_generator is not CustomManualSelectorGenerator donorSelector)
                continue;
            var selector = HavokGraph.ShallowCopy(donorSelector);
            selector.m_animId = a.Id;
            selector.m_name = $"Attack{a.Id}_CMSG";
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
            state.m_name = $"Attack{a.Id}";
            state.m_generator = selector;
            state.m_listeners = [.. donor.m_listeners];
            sm.m_states.Add(state);
            foreach (var t in wildcards.Where(t => t.m_toStateId == donor.m_stateId).ToList())
            {
                if (t.m_eventId < 0 || t.m_eventId >= strings.m_eventNames.Count)
                    continue;
                var eventName = strings.m_eventNames[t.m_eventId];
                if (!eventName.EndsWith(a.Donor.ToString(), StringComparison.Ordinal))
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
