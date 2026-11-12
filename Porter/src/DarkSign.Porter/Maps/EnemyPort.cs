using System.Numerics;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// Places the DS3 map's enemies and bosses in its Elden Ring slot: every DS3 enemy part becomes an Elden Ring enemy
/// part at the same spot, on the same collision, with its DS3 entity ID and entity groups moved to the slot, so the
/// converted event scripts (boss fights, ambushes, death flags, drops) find it.
///
/// Which character stands there, by method (launcher Fallbacks window, "map-enemies"):
/// <list type="bullet">
/// <item>A (default): the ported DS3 character when the enemy porter has ported it (darksign_chr_map.tsv), otherwise
/// an Elden Ring stand-in as in B.</item>
/// <item>B: Elden Ring stand-ins only.</item>
/// <item>C: no enemies.</item>
/// </list>
///
/// Stand-ins are real Elden Ring placements (model, NpcParam, ThinkParam and the part itself, copied from a vanilla
/// legacy dungeon), picked by what the DS3 enemy is: its NpcParam name ("Lothric Knight") is matched by kind
/// (knight, soldier, dog, skeleton, dragon...) to an Elden Ring enemy of the same kind, earliest dungeon first so the
/// first DS3 areas get early-game enemies. DS3 bosses (enemies whose boss health bar the map's events show) get
/// Elden Ring bosses. DS3's people (the human body c0000, and anyone with a talk script) get Elden Ring people
/// to stand in for them (<see cref="People"/>): they stand where DS3 put them and say nothing, until DS3's
/// equipment and dialogue are ported.
/// </summary>
public static class EnemyPort
{
    public enum Method { Auto, StandIn, None }

    public static Method ParseMethod(string? text) => text?.ToLowerInvariant() switch
    {
        "standin" or "stand-in" or "b" => Method.StandIn,
        "none" or "off" or "c" => Method.None,
        _ => Method.Auto,
    };

    /// <summary>An Elden Ring character that can stand somewhere: a vanilla placement, or a ported DS3 character.</summary>
    public sealed record ErEnemy(string Model, int NpcParam, int ThinkParam, string Name, bool Boss, string Source,
        MSBE.Part.EnemyBase? Part = null, MSBE.Model.Enemy? ModelEntry = null)
    {
        public string Kind { get; } = KindOf(Name);
    }

    /// <summary>One DS3 enemy as the port sees it.</summary>
    public sealed record Ds3Enemy(string Model, int NpcParam, string Name, bool Boss, bool Npc);

    /// <summary>The kind of an enemy by its name (shared with the randomizer, which mixes enemies by the same kinds).</summary>
    public static string KindOf(string name) => DarkSign.Randomizer.Logic.EnemyKinds.KindOf(name);

    /// <summary>
    /// Picks who stands in for a DS3 enemy: the ported character if there is one (method A), else the first Elden
    /// Ring placement of the same kind and boss-ness, else the most common one of the same boss-ness.
    /// </summary>
    public sealed class Chooser
    {
        private readonly IReadOnlyList<ErEnemy> _catalog;
        private readonly ChrMap? _ported;
        private readonly Method _method;
        private readonly ErEnemy? _commonEnemy, _commonBoss;
        private readonly Dictionary<string, int> _bossTurn = new();

        public Chooser(IReadOnlyList<ErEnemy> catalog, ChrMap? ported, Method method)
        {
            _catalog = catalog;
            _ported = ported;
            _method = method;
            _commonEnemy = catalog.Where(e => !e.Boss).GroupBy(e => e.Model).OrderByDescending(g => g.Count()).Select(g => g.First()).FirstOrDefault();
            _commonBoss = catalog.FirstOrDefault(e => e.Boss);
        }

        public ErEnemy? Choose(Ds3Enemy ds3)
        {
            if (_method == Method.None || ds3.Npc)
                return null;
            if (_method == Method.Auto && _ported?.Find(ds3) is { } ported)
                return ported;
            var kind = KindOf(ds3.Name);
            var sameKind = _catalog.Where(e => e.Boss == ds3.Boss && kind.Length > 0 && e.Kind == kind).ToList();
            if (ds3.Boss)
            {
                // Spread bosses over the Elden Ring ones of their kind, so every DS3 boss isn't the same one.
                var pool = sameKind.Count > 0 ? sameKind : _catalog.Where(e => e.Boss).ToList();
                if (pool.Count == 0)
                    return _commonEnemy;
                var key = sameKind.Count > 0 ? kind : "";
                var turn = _bossTurn.GetValueOrDefault(key);
                _bossTurn[key] = turn + 1;
                return pool.DistinctBy(e => e.NpcParam).ElementAt(turn % pool.DistinctBy(e => e.NpcParam).Count());
            }
            return sameKind.FirstOrDefault() ?? _commonEnemy ?? _commonBoss;
        }

        /// <summary>The ported DS3 character for this one, if the enemy porter made it (method A only).</summary>
        public ErEnemy? Ported(Ds3Enemy ds3) => _method == Method.Auto ? _ported?.Find(ds3) : null;

        /// <summary>
        /// The ported character for a person: only a row ported for this very NpcParam. A body ported because its boss
        /// placements fight (Friede) must not put that hostile boss row where DS3 has the same body as a person (Friede
        /// in her chapel, 4500710).
        /// </summary>
        public ErEnemy? PortedPerson(Ds3Enemy ds3) => _method == Method.Auto ? _ported?.FindPerson(ds3) : null;

        /// <summary>
        /// The ThinkParam a placement gets: for a ported character whose AI was ported, the row ported from the DS3
        /// placement's own NpcThinkParam row (DS3 gives one enemy different goals by placement: a Jailer that patrols,
        /// one that waits); otherwise the character's.
        /// </summary>
        public int ThinkFor(ErEnemy who, int ds3Think)
        {
            if (who.Source != "ported DS3 character" || _ported is null)
                return who.ThinkParam;
            if (_ported.Think(ds3Think, who.Model) is { } own)
                return own;
            // DS3 places one model with another family's AI rows (the Cemetery of Ash's hollows, c1430, mostly run the
            // Hollow Soldiers' 110005 / 110010 / 110060). Rows are ported per character (row / 100 = character), so
            // those have no row on this model; up to 1.0.417 they took the character's first row (143010, a goal
            // DS3 never places there) and circled the player without attacking. An NpcThinkParam row names goals
            // and senses only, so the row ported with the other character serves this one as DS3 does.
            if (SharedThinks && _ported.ThinkAny(ds3Think) is { } shared)
            {
                BorrowedThinks++;
                return shared;
            }
            return who.ThinkParam;
        }

        /// <summary>Placements given a DS3 think row ported with another character (see <see cref="ThinkFor"/>).</summary>
        public int BorrowedThinks { get; private set; }

        /// <summary>Fallback enemy-ai H: DARKSIGN_ENEMY_SHARED_THINK=0 gives such placements their character's first row, as up to 1.0.417.</summary>
        public static bool SharedThinks => Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_SHARED_THINK") != "0";
    }

    /// <summary>
    /// The Elden Ring people who stand in for DS3's: vanilla placements of the human body (c0000) in the legacy
    /// dungeons, each with its own look (CharaInitParam), NpcParam and AI. Someone DS3 lets you talk to gets one
    /// Elden Ring lets you talk to (so they are on the player's side), with the talk script taken off; a DS3 human
    /// without a talk script (invaders, hostile humans, summons) gets an Elden Ring human who fights for the
    /// enemies' team. The same DS3 person is the same stand-in on every map.
    /// </summary>
    public sealed class People
    {
        public const string Human = "c0000";
        public IReadOnlyList<ErEnemy> Friendly { get; }
        public IReadOnlyList<ErEnemy> Hostile { get; }

        public People(IEnumerable<ErEnemy> friendly, IEnumerable<ErEnemy> hostile)
        {
            Friendly = friendly.ToList();
            Hostile = hostile.ToList();
        }

        public static bool IsHuman(string model) => model.StartsWith(Human, StringComparison.OrdinalIgnoreCase);

        /// <summary>DS3's invisible dummy character (c1000): talk and menu points, bullet sources.</summary>
        public static bool IsDummy(string model) => model.StartsWith("c1000", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A DS3 character that is only logic, never seen: the talk dummy (c1000), the invisible platoon leader c0100
        /// ("Patrol Leader - Collective Patrol") and target dummies ("... target dummy character"). An Elden Ring
        /// stand-in there would put a visible enemy where DS3 shows nothing.
        /// </summary>
        public static bool IsLogic(string model, string name) =>
            IsDummy(model) || model.StartsWith("c0100", StringComparison.OrdinalIgnoreCase)
            || name.Contains("dummy", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A DS3 summon or invader ("... - White Phantom", "... - Mad Phantom", "Dark Spirit ..."): DS3 places them
        /// out of sight and only an event or a summon sign brings them in. Standing as a hostile Elden Ring person they
        /// attacked the player at map 2's start (1.0.340: knocked down again and again), so they are left out.
        /// </summary>
        public static bool IsPhantom(string name) =>
            name.Contains("Phantom", StringComparison.OrdinalIgnoreCase) || name.Contains("Dark Spirit", StringComparison.OrdinalIgnoreCase);

        public ErEnemy? Choose(Ds3Enemy ds3, bool talks)
        {
            var pool = talks ? Friendly : Hostile;
            if (pool.Count == 0 || !IsHuman(ds3.Model))
                return null;
            // Not string.GetHashCode: the pick has to be the same on every run.
            var hash = (uint)ds3.NpcParam * 2654435761u;
            return pool[(int)((hash >> 8) % (uint)pool.Count)];
        }
    }

    /// <summary>
    /// darksign_chr_map.tsv, written by the enemy porter: which DS3 characters it ported and as what.
    /// Lines: "npc &lt;DS3 NpcParam&gt; &lt;ER model&gt; &lt;ER NpcParam&gt; &lt;ER ThinkParam&gt; [name]" for one DS3 NpcParam row, and
    /// "chr &lt;DS3 model&gt; &lt;ER model&gt; &lt;ER NpcParam&gt; &lt;ER ThinkParam&gt; [name]" for any row of a DS3 model, and
    /// "think &lt;DS3 ThinkParam&gt; &lt;ER model&gt; &lt;ER ThinkParam&gt; [name]" for each DS3 NpcThinkParam row ported with
    /// the character's AI (Enemies.AiPort), tab-separated.
    /// </summary>
    public sealed class ChrMap
    {
        public const string FileName = "darksign_chr_map.tsv";
        private readonly Dictionary<int, ErEnemy> _byNpc = new();
        private readonly Dictionary<string, ErEnemy> _byModel = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _npcModels = new(StringComparer.OrdinalIgnoreCase); // ER models with per-NpcParam rows
        private readonly Dictionary<(int, string), int> _think = new();

        /// <summary>The ported NpcThinkParam row for a DS3 placement's own row on this model, if its AI was ported.</summary>
        public int? Think(int ds3Think, string model) => _think.TryGetValue((ds3Think, model.ToLowerInvariant()), out var t) ? t : null;

        /// <summary>The ported NpcThinkParam row for a DS3 row on whichever model it was ported with (the first listed), if any.</summary>
        public int? ThinkAny(int ds3Think) => _thinkAny.TryGetValue(ds3Think, out var t) ? t : null;
        private readonly Dictionary<int, int> _thinkAny = new();

        public int Count => _byNpc.Count + _byModel.Count;

        /// <summary>A ported DS3 model's name (c6020 -> Sister Friede), or null.</summary>
        public string? NameOf(string ds3Model) => _byModel.TryGetValue(ds3Model, out var e) ? e.Name : null;

        public static ChrMap Parse(IEnumerable<string> lines)
        {
            var map = new ChrMap();
            foreach (var line in lines)
            {
                var p = line.Split('\t');
                if (!line.StartsWith('#') && p.Length >= 4 && p[0] == "think" && int.TryParse(p[1], out var ds3Think) && int.TryParse(p[3], out var erThink))
                {
                    map._think[(ds3Think, p[2].ToLowerInvariant())] = erThink;
                    map._thinkAny.TryAdd(ds3Think, erThink);
                    continue;
                }
                if (line.StartsWith('#') || p.Length < 5 || !int.TryParse(p[3], out var npc) || !int.TryParse(p[4], out var think))
                    continue;
                var name = p.Length > 5 ? p[5] : p[1];
                var enemy = new ErEnemy(p[2], npc, think, name, false, "ported DS3 character");
                if (p[0] == "npc" && int.TryParse(p[1], out var ds3Npc))
                {
                    map._byNpc[ds3Npc] = enemy;
                    map._npcModels.Add(enemy.Model);
                }
                else if (p[0] == "chr")
                    map._byModel[p[1]] = enemy;
            }
            return map;
        }

        public ErEnemy? FindNpc(Ds3Enemy ds3) => _byNpc.TryGetValue(ds3.NpcParam, out var e) ? e with { Boss = ds3.Boss } : null;

        /// <summary>
        /// For a person: their own NpcParam's row, or the model's row when the model was ported without per-NpcParam
        /// rows. A model ported with rows for other NpcParams (Friede's two boss stages) doesn't lend them to this one.
        /// </summary>
        public ErEnemy? FindPerson(Ds3Enemy ds3) =>
            FindNpc(ds3) ?? (_byModel.TryGetValue(ds3.Model, out var m) && !_npcModels.Contains(m.Model) ? m with { Boss = ds3.Boss } : null);

        public ErEnemy? Find(Ds3Enemy ds3) =>
            _byNpc.TryGetValue(ds3.NpcParam, out var e) ? e with { Boss = ds3.Boss }
            : _byModel.TryGetValue(ds3.Model, out var m) ? m with { Boss = ds3.Boss } : null;
    }

    /// <param name="Npcs">DS3 people left out.</param>
    /// <param name="People">DS3 people placed (counted in Placed too).</param>
    public sealed record Result(int Placed, int Ported, int StandIns, int Npcs, int Skipped, IReadOnlyList<string> Notes, int People = 0);

    /// <summary>
    /// Adds the DS3 map's enemies to the converted layout. <paramref name="describe"/> says what each DS3 enemy is
    /// (name from its NpcParam, boss or not, NPC or not).
    /// </summary>
    public static Result Place(MSB3 ds3, MSBE msb, MapId ds3Id, MapId erId, MsbPort.Options options, Chooser chooser,
        Func<MSB3.Part.Enemy, Ds3Enemy> describe, MSBE.Part.Enemy? fallbackTemplate = null, List<string>? table = null,
        Dictionary<string, string>? partNames = null, People? people = null, IReadOnlyDictionary<string, Seat>? seats = null,
        bool placePeople = true, PeopleArmour? armour = null, bool erStandIns = true)
    {
        int dressed = 0, borrowedBefore = chooser.BorrowedThinks;
        int placed = 0, ported = 0, standIns = 0, npcs = 0, skipped = 0, peoplePlaced = 0, seated = 0, sameSeat = 0;
        var takenSeats = new List<Vector3>();
        var peopleUse = new Dictionary<string, int>();
        var notes = new List<string>();
        var collisions = msb.Parts.Collisions.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = msb.Parts.GetEntries().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextInstance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var standInUse = new Dictionary<string, int>();

        var layerOk = !options.AllLayers && MsbPort.BaseLayerMeans(ds3);
        int otherState = 0, phantoms = 0, logic = 0;
        var otherStateNames = new Dictionary<string, int>();
        foreach (var src in ds3.Parts.Enemies)
        {
            var what = describe(src);
            if (MsbPort.OtherState(src, layerOk))
            {
                otherState++;
                otherStateNames[what.Name] = otherStateNames.GetValueOrDefault(what.Name) + 1;
                continue;
            }
            ErEnemy? who;
            if (what.Npc && People.IsPhantom(what.Name))
            {
                phantoms++;
                npcs++;
                continue;
            }
            if (what.Npc)
            {
                // A person: the ported DS3 character if it has a body of its own (Fire Keeper, Ludleth...). Elden Ring
                // people stand in only when asked (--er-people, fallback map-people B): Devin wants DS3's own people
                // at Firelink or nobody (1.0.353). c1000 is DS3's invisible talk dummy (bonfires, menus): nobody.
                // A DS3 human (c0000) with a DS3 look (PeopleArmour) is an Elden Ring person wearing DS3's armour.
                who = !placePeople ? null
                    : People.IsHuman(what.Model) ? (erStandIns || armour?.LookFor(src.CharaInitID) is not null ? people?.Choose(what, src.TalkID > 0) : null)
                    : chooser.PortedPerson(what) ?? (People.IsDummy(what.Model) || !erStandIns ? null : people?.Choose(what with { Model = People.Human }, talks: true));
                if (who is null)
                {
                    npcs++;
                    continue;
                }
            }
            else if (People.IsLogic(what.Model, what.Name))
            {
                logic++;
                skipped++;
                continue;
            }
            else
                who = chooser.Choose(what);
            if (who is null)
            {
                skipped++;
                continue;
            }
            var seat = what.Npc ? seats?.GetValueOrDefault(src.Name) : null;
            if (seat is not null)
            {
                // Two versions of one person (DS3 enables one by quest state) share a seat: only the first stands.
                if (takenSeats.Any(t => Vector3.Distance(t, seat.Position) < 0.5f))
                {
                    sameSeat++;
                    continue;
                }
                takenSeats.Add(seat.Position);
            }
            var template = who.Part as MSBE.Part.Enemy ?? fallbackTemplate ?? new MSBE.Part.Enemy();
            var part = (MSBE.Part.Enemy)template.DeepCopy();
            MsbPort.CopyCommon(src, part, ds3Id, erId);
            MsbPort.ApplyGroups(src, part.Unk1, options);
            // Part names are model_instance, unique in the map.
            var instance = nextInstance.GetValueOrDefault(who.Model, 9000);
            while (names.Contains($"{who.Model}_{instance:D4}"))
                instance++;
            nextInstance[who.Model] = instance + 1;
            part.Name = $"{who.Model}_{instance:D4}";
            part.InstanceID = instance;
            names.Add(part.Name);
            partNames?.TryAdd(src.Name, part.Name);
            part.ModelName = who.Model;
            part.NPCParamID = who.NpcParam;
            part.ThinkParamID = chooser.ThinkFor(who, src.ThinkParamID);
            part.TalkID = 0;
            part.PlatoonID = src.PlatoonID;
            // An Elden Ring person keeps the look of the placement it was copied from; anything else has none.
            if (!(what.Npc && who.Source != "ported DS3 character"))
                part.CharaInitID = -1;
            // DS3's look: the stand-in's row, dressed in the DS3 armour, is added after port-weapons (PeopleArmour).
            if (what.Npc && People.IsHuman(what.Model) && part.CharaInitID > 0 && armour?.LookFor(src.CharaInitID) is int look)
            {
                armour.StandIns.TryAdd(look, part.CharaInitID);
                part.CharaInitID = look;
                dressed++;
            }
            part.WalkRouteName = ""; // DS3 patrol routes need the map's regions, which aren't ported yet
            part.BackupEventAnimID = -1;
            part.CollisionPartName = collisions.Contains(src.CollisionName ?? "") ? src.CollisionName : "";
            if (seat is not null)
            {
                // DS3's scripts move this person from the holding spot to their seat when the map loads.
                part.Position = seat.Position;
                part.Rotation = seat.Rotation;
                part.CollisionPartName = collisions.Contains(seat.Collision) ? seat.Collision : "";
                seated++;
            }
            // Load and activation settings come from this map, not the dungeon the stand-in was copied from.
            if (msb.Parts.MapPieces.FirstOrDefault()?.TileLoad is { } tile)
                part.TileLoad = tile.DeepCopy();
            part.ChrActivateCondParamID = 0; // some vanilla enemies only appear at night or after an event
            msb.Parts.Enemies.Add(part);
            MsbPort.AddModel(msb.Models.Enemies, who.Model, who.ModelEntry, erId);
            placed++;
            // One row of darksign_enemies.tsv (Randomizer EnemyTables.PlacedFile), so the randomizer knows this spot.
            // People aren't listed: the randomizer must not put a monster where someone stands.
            if (!what.Npc)
                table?.Add(string.Join('\t', ds3Id, src.Name, src.EntityID, erId, part.Name, part.EntityID, src.ModelName, src.NPCParamID,
                    Clean(what.Name), what.Boss ? 1 : 0, who.Source == "ported DS3 character" ? "ported" : "stand-in", who.Model, who.NpcParam, who.ThinkParam));
            if (what.Npc)
            {
                peoplePlaced++;
                var key = $"{what.Name} -> {who.Name}{(src.TalkID > 0 ? " (silent)" : " (hostile)")}";
                peopleUse[key] = peopleUse.GetValueOrDefault(key) + 1;
            }
            else if (who.Source == "ported DS3 character")
                ported++;
            else
            {
                standIns++;
                var key = $"{what.Name} -> {who.Name}";
                standInUse[key] = standInUse.GetValueOrDefault(key) + 1;
            }
            if (what.Boss)
                notes.Add($"boss {what.Name} (entity {part.EntityID}) -> {who.Name} ({who.Model}, {who.Source})");
        }
        if (standInUse.Count > 0)
            notes.Add("stand-ins: " + string.Join("; ", standInUse.OrderByDescending(s => s.Value).Take(15).Select(s => $"{s.Key} x{s.Value}")));
        if (otherState > 0)
            notes.Add($"{otherState} left out that belong to another state of the map (off DS3's layer bit 0, e.g. the Untended Graves): "
                      + string.Join("; ", otherStateNames.OrderByDescending(n => n.Value).Take(12).Select(n => $"{n.Key} x{n.Value}")));
        if (logic > 0)
            notes.Add($"{logic} DS3 logic characters left out (talk dummy c1000, platoon leader c0100, target dummies): DS3 shows nothing there");
        if (phantoms > 0)
            notes.Add($"people: {phantoms} DS3 summons and invaders (White/Mad Phantom, Dark Spirit) left out: they only appear when summoned or invading");
        if (seated > 0)
            notes.Add($"people: {seated} moved from DS3's holding spot to the seat DS3's scripts warp them to"
                      + (sameSeat > 0 ? $"; {sameSeat} other versions of a seated person left out (DS3 shows one by quest state)" : ""));
        if (peopleUse.Count > 0)
            notes.Add("people: " + string.Join("; ", peopleUse.OrderBy(s => s.Key, StringComparer.Ordinal).Take(30).Select(s => $"{s.Key} x{s.Value}")));
        if (dressed > 0)
            notes.Add($"people: {dressed} DS3 people wear their DS3 armour (CharaInit {PeopleArmour.CharaInitBase}+, on an Elden Ring person's face and AI)");
        if (chooser.BorrowedThinks > borrowedBefore)
            notes.Add($"AI: {chooser.BorrowedThinks - borrowedBefore} placements run a DS3 think row ported with another character (DS3 places them with that family's AI)");
        return new Result(placed, ported, standIns, npcs, skipped, notes, peoplePlaced);
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>darksign_er_enemies.tsv rows (Randomizer EnemyTables.ErFile): the catalog's Elden Ring placements.</summary>
    public static IEnumerable<string> CatalogRows(IEnumerable<ErEnemy> catalog) =>
        catalog.Where(c => c.Part is not null && c.Source.StartsWith('m')).Select(c => string.Join('\t', c.Source, c.Part!.Name,
            c.Part.EntityID, c.Model, c.NpcParam, c.ThinkParam, Clean(c.Name), c.Boss ? 1 : 0));

    // ---- What the event scripts say about bosses ----

    /// <summary>
    /// The entity IDs a game's scripts show a boss health bar for: given directly, or passed to the event that shows
    /// it. <paramref name="ds3Layout"/>: DS3's InitializeCommonEvent has no slot argument.
    /// </summary>
    public static HashSet<long> BossEntities(IEnumerable<EMEVD> scripts, bool ds3Layout)
    {
        var list = scripts.ToList();
        var bosses = new HashSet<long>();
        var passed = new Dictionary<long, HashSet<long>>(); // event ID -> parameter source bytes holding a boss
        foreach (var e in list.SelectMany(s => s.Events))
            for (int i = 0; i < e.Instructions.Count; i++)
            {
                var ins = e.Instructions[i];
                if (ins.Bank != 2003 || ins.ID is not (11 or 80) || ins.ArgData.Length < 8)
                    continue;
                var param = e.Parameters.FirstOrDefault(p => p.InstructionIndex == i && p.TargetStartByte == 4);
                if (param is null)
                    bosses.Add(BitConverter.ToInt32(ins.ArgData, 4));
                else
                    (passed.TryGetValue(e.ID, out var set) ? set : passed[e.ID] = new()).Add(param.SourceStartByte);
            }
        foreach (var ins in list.SelectMany(s => s.Events).SelectMany(e => e.Instructions).Where(i => i.Bank == 2000 && i.ID is 0 or 6))
        {
            // InitializeEvent: slot, event, args. InitializeCommonEvent: [slot,] event, args.
            int eventAt = ins.ID == 6 && ds3Layout ? 0 : 4;
            if (ins.ArgData.Length < eventAt + 4)
                continue;
            var callee = BitConverter.ToUInt32(ins.ArgData, eventAt);
            if (!passed.TryGetValue(callee, out var sources))
                continue;
            foreach (var source in sources)
            {
                var at = eventAt + 4 + (int)source;
                if (at + 4 <= ins.ArgData.Length)
                    bosses.Add(BitConverter.ToInt32(ins.ArgData, at));
            }
        }
        bosses.Remove(0);
        return bosses;
    }

    /// <summary>
    /// Every placement that is part of a boss fight: the boss entities (<see cref="BossEntities"/>) and, for a boss's
    /// own body, its other placements that share one of the boss's entity groups. DS3 gives bosses who speak a talk
    /// script (Sister Friede 4500801/4500800 and Father Ariandel 4500803/4500802 in the Painted World: TalkIDs
    /// 450551, 450552, 450560, 450561), and the port took every placement with a talk script for a person, so neither
    /// was ported or placed and the Friede fight had nobody in it. Ariandel's first-phase placement (4500803) never
    /// shows a health bar; it shares the fight's group 4505800. Friede's chapel placement (4500710, no group) stays a
    /// person. Humans (c0000) are never counted: their look is equipment, not a body the enemy porter can build.
    /// </summary>
    public static HashSet<long> BossEncounter(MSB3 msb, IReadOnlySet<long> bosses)
    {
        var encounter = new HashSet<long>(bosses);
        var bossParts = msb.Parts.Enemies.Where(e => e.EntityID > 0 && bosses.Contains(e.EntityID) && !People.IsHuman(e.ModelName)).ToList();
        var models = bossParts.Select(e => e.ModelName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = bossParts.SelectMany(e => e.EntityGroups).Where(g => g > 0).ToHashSet();
        foreach (var e in msb.Parts.Enemies)
            if (e.EntityID > 0 && models.Contains(e.ModelName) && e.EntityGroups.Any(groups.Contains))
                encounter.Add(e.EntityID);
        return encounter;
    }

    /// <summary>A DS3 placement the port treats as a person: a human, or a talker that is not part of a boss fight.</summary>
    public static bool IsPerson(MSB3.Part.Enemy e, IReadOnlySet<long> encounter) =>
        People.IsHuman(e.ModelName) || e.TalkID > 0 && !encounter.Contains(e.EntityID);

    // ---- Where DS3's scripts move characters ----

    // Character Warp Request, Warp Character and Set Floor, Issue Short Warp Request, Warp Character and Copy Floor:
    // each starts Entity ID, Warp Type, Warp Destination Entity ID.
    private static readonly HashSet<int> CharacterWarps = [3, 40, 41, 42];

    /// <summary>
    /// DS3 character entity -> the entity its scripts first warp it to (Warp Character and Copy Floor and its kin),
    /// with the arguments of parameterised events read from the lines that start them. DS3 keeps most of Firelink
    /// Shrine's people at one spot under the floor and its scripts move each to their seat when the map loads.
    /// </summary>
    public static Dictionary<int, int> EventWarps(IEnumerable<EMEVD> scripts, bool ds3Layout = true)
    {
        var list = scripts.ToList();
        var calls = new Dictionary<long, List<byte[]>>();
        foreach (var ins in list.SelectMany(s => s.Events).SelectMany(e => e.Instructions).Where(i => i.Bank == 2000 && i.ID is 0 or 6))
        {
            int at = ins.ID == 6 && ds3Layout ? 0 : 4;
            if (ins.ArgData.Length < at + 4)
                continue;
            var id = BitConverter.ToUInt32(ins.ArgData, at);
            (calls.TryGetValue(id, out var l) ? l : calls[id] = []).Add(ins.ArgData[(at + 4)..]);
        }
        var warps = new Dictionary<int, int>();
        foreach (var e in list.SelectMany(s => s.Events))
            for (int k = 0; k < e.Instructions.Count; k++)
            {
                var ins = e.Instructions[k];
                if (ins.Bank != 2004 || !CharacterWarps.Contains(ins.ID) || ins.ArgData.Length < 12)
                    continue;
                var who = BitConverter.ToInt32(ins.ArgData, 0);
                var to = BitConverter.ToInt32(ins.ArgData, 8);
                var ps = e.Parameters.Where(p => p.InstructionIndex == k).ToList();
                if (ps.Count == 0)
                {
                    if (who > 0 && to > 0)
                        warps.TryAdd(who, to);
                    continue;
                }
                foreach (var call in calls.GetValueOrDefault(e.ID) ?? [])
                {
                    int w = who, t = to;
                    foreach (var p in ps)
                        if (p.SourceStartByte + 4 <= call.Length)
                        {
                            var value = BitConverter.ToInt32(call, (int)p.SourceStartByte);
                            if (p.TargetStartByte == 0) w = value;
                            else if (p.TargetStartByte == 8) t = value;
                        }
                    if (w > 0 && t > 0)
                        warps.TryAdd(w, t);
                }
            }
        return warps;
    }

    /// <summary>Where a seated person stands: the DS3 region or part it is warped to, and a collision near it.</summary>
    public sealed record Seat(Vector3 Position, Vector3 Rotation, string Collision);

    /// <summary>
    /// The seats of DS3 characters that stand at a holding spot (three or more characters at one point, as Firelink
    /// Shrine's people do under its floor) and that the scripts warp somewhere: DS3 moves them there when the map
    /// loads, so they are placed there. The collision is that of the nearest character or start point that isn't
    /// held, within 60 m.
    /// </summary>
    public static Dictionary<string, Seat> Seats(MSB3 ds3, IReadOnlyDictionary<int, int> warps)
    {
        var seats = new Dictionary<string, Seat>(StringComparer.Ordinal);
        bool Held(MSB3.Part p) => ds3.Parts.Enemies.Count(o => Vector3.Distance(o.Position, p.Position) < 0.5f) >= 3;
        var anchors = ds3.Parts.Enemies.Where(e => !Held(e) && !string.IsNullOrEmpty(e.CollisionName)).Select(e => (e.Position, Collision: e.CollisionName))
            .Concat(ds3.Parts.Players.Select(p => (p.Position, Collision: ""))).ToList();
        foreach (var e in ds3.Parts.Enemies)
        {
            if (e.EntityID <= 0 || !warps.TryGetValue(e.EntityID, out var to) || !Held(e))
                continue;
            (Vector3 Pos, Vector3 Rot)? spot =
                ds3.Regions.GetEntries().FirstOrDefault(r => r.EntityID == to) is { } r ? (r.Position, r.Rotation)
                : ds3.Parts.GetEntries().FirstOrDefault(p => p.EntityID == to) is { } p ? (p.Position, p.Rotation) : null;
            if (spot is null)
                continue;
            var near = anchors.Where(a => a.Collision.Length > 0 && Vector3.Distance(a.Position, spot.Value.Pos) < 60)
                .OrderBy(a => Vector3.Distance(a.Position, spot.Value.Pos)).Select(a => a.Collision).FirstOrDefault() ?? "";
            seats[e.Name] = new Seat(spot.Value.Pos, spot.Value.Rot, near);
        }
        return seats;
    }

    // ---- The Elden Ring catalog ----

    // Legacy dungeons in rough order of progression: stand-ins come from the earliest one that has the kind.
    public static readonly string[] CatalogMaps =
    [
        "m18_00_00_00", "m10_00_00_00", "m10_01_00_00", "m14_00_00_00", "m16_00_00_00", "m11_00_00_00",
        "m11_05_00_00", "m35_00_00_00", "m13_00_00_00", "m15_00_00_00", "m12_01_00_00", "m12_02_00_00",
    ];

    /// <summary>Every distinct enemy (model, NpcParam, ThinkParam) placed in Elden Ring's legacy dungeons, with its name.</summary>
    public static List<ErEnemy> BuildCatalog(GameArchives er, PARAM? npcParam, List<string> notes) =>
        BuildCatalog(er, npcParam, notes, out _);

    /// <summary>The team a row of NpcParam fights for, or -1.</summary>
    public static int TeamOf(PARAM? npcParam, int id)
    {
        try { return npcParam?[id] is { } row ? System.Convert.ToInt32(row["teamType"].Value) : -1; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return -1; }
    }

    /// <summary>
    /// Sorts the vanilla human placements into the two pools of <see cref="People"/>. Friendly: has a talk script.
    /// Hostile: no talk script, and on a team the ordinary enemies of the catalog are on (so the game already
    /// treats it as an enemy; summons, corpses and quest phantoms are on other teams and are left out).
    /// </summary>
    public static People SortPeople(IEnumerable<(ErEnemy Who, bool Talks)> humans, IEnumerable<ErEnemy> catalog, Func<int, int> teamOf, List<string> notes,
        Func<ErEnemy, string?>? looksWrong = null)
    {
        var enemyTeams = catalog.Select(c => teamOf(c.NpcParam)).Where(t => t >= 0).ToHashSet();
        var all = humans.ToList();
        // A person must look dressed (1.0.340: a naked man at Firelink).
        var rejected = new Dictionary<string, List<string>>();
        var list = all.Where(h =>
        {
            if (looksWrong?.Invoke(h.Who) is not { } why)
                return true;
            (rejected.TryGetValue(why, out var l) ? l : rejected[why] = []).Add($"{h.Who.Name} ({h.Who.NpcParam}/{h.Who.Part?.CharaInitID})");
            return false;
        }).ToList();
        if (rejected.Count > 0)
            notes.Add("people: left out of the pools: " + string.Join("; ", rejected.Select(r =>
                $"{r.Key} x{r.Value.Count} ({string.Join(", ", r.Value.Distinct().Take(8))})")));
        var friendly = list.Where(h => h.Talks).Select(h => h.Who).DistinctBy(h => (h.NpcParam, h.Part?.CharaInitID)).ToList();
        var hostile = list.Where(h => !h.Talks && enemyTeams.Contains(teamOf(h.Who.NpcParam))).Select(h => h.Who)
            .DistinctBy(h => (h.NpcParam, h.Part?.CharaInitID)).ToList();
        notes.Add($"people: {friendly.Count} Elden Ring people with a talk script and {hostile.Count} hostile humans found to stand in for DS3's "
                  + $"(of {list.Count} human placements; enemy teams {string.Join(",", enemyTeams.OrderBy(t => t))})");
        if (hostile.Count > 0)
            notes.Add("people: hostile humans: " + string.Join("; ", hostile.Take(20).Select(h => $"{h.Name} ({h.NpcParam}, {h.Source})")));
        return new People(friendly, hostile);
    }

    /// <summary>
    /// Why an Elden Ring human placement can't stand in for a DS3 person, or null when it can: its CharaInitParam
    /// row has no body armour, so it stands there bare-chested or naked (1.0.340: the Fire Keeper's spot got Boc the
    /// Seamster, CharaInit 23420, a hood and nothing else). 220 of Elden Ring's 3,273 rows have no body armour.
    /// </summary>
    public static string? PersonLooksWrong(PARAM? charaInit, int chara)
    {
        if (charaInit is null)
            return null;
        if (charaInit[chara] is not { } c)
            return "no CharaInitParam row";
        var body = c.Cells.FirstOrDefault(x => x.Def.InternalName == "equip_Armer")?.Value;
        return body is null or byte[] || System.Convert.ToInt32(body) <= 0 ? "no body armour" : null;
    }

    public static List<ErEnemy> BuildCatalog(GameArchives er, PARAM? npcParam, List<string> notes, out People people, PARAM? charaInit = null)
    {
        var catalog = new List<ErEnemy>();
        var humans = new List<(ErEnemy, bool)>();
        var seen = new HashSet<(string, int, int)>();
        int bystanders = 0;
        var names = npcParam?.Rows.ToDictionary(r => r.ID, r => r.Name ?? "") ?? new();
        foreach (var name in CatalogMaps)
        {
            var id = MapId.Parse(name);
            var msbPath = MapPorter.MsbPath(id);
            if (!er.Contains(msbPath))
                continue;
            MSBE msb;
            try
            {
                msb = MSBE.Read(er.Read(msbPath));
            }
            catch (Exception ex)
            {
                notes.Add($"catalog: {name} doesn't read ({ex.Message})");
                continue;
            }
            var scripts = new List<EMEVD>();
            var eventPath = MapEvents.ScriptPath(id);
            if (er.Contains(eventPath))
                scripts.Add(EMEVD.Read(er.Read(eventPath)));
            if (er.Contains(MapEvents.CommonFunc))
                scripts.Add(EMEVD.Read(er.Read(MapEvents.CommonFunc)));
            var bosses = BossEntities(scripts, ds3Layout: false);
            foreach (var part in msb.Parts.Enemies)
            {
                // Humans (c0000) are NPCs or invaders built from equipment: they stand in for DS3's people, not its enemies.
                if (People.IsHuman(part.ModelName) && part.NPCParamID > 0 && part.CharaInitID > 0)
                    humans.Add((new ErEnemy(part.ModelName, part.NPCParamID, part.ThinkParamID, names.GetValueOrDefault(part.NPCParamID, part.ModelName),
                        false, name, part, msb.Models.Enemies.FirstOrDefault(m => m.Name.Equals(part.ModelName, StringComparison.OrdinalIgnoreCase))), part.TalkID > 0));
                if (People.IsHuman(part.ModelName) || part.TalkID > 0 || part.NPCParamID <= 0)
                    continue;
                if (!seen.Add((part.ModelName, part.NPCParamID, part.ThinkParamID)))
                    continue;
                // Wildlife and helper characters (deer, eagles, talk dummies, bonfires) are nobody's stand-in.
                if (DarkSign.Randomizer.Logic.EnemyKinds.IsBystander(names.GetValueOrDefault(part.NPCParamID, "")))
                {
                    bystanders++;
                    continue;
                }
                var model = msb.Models.Enemies.FirstOrDefault(m => m.Name.Equals(part.ModelName, StringComparison.OrdinalIgnoreCase));
                catalog.Add(new ErEnemy(part.ModelName, part.NPCParamID, part.ThinkParamID,
                    names.GetValueOrDefault(part.NPCParamID, part.ModelName), bosses.Contains(part.EntityID), name, part, model));
            }
        }
        if (bystanders > 0)
            notes.Add($"catalog: {bystanders} wildlife and helper placements left out (deer, eagles, dummies, bonfires)");
        people = SortPeople(humans, catalog.Where(c => !c.Boss), id => TeamOf(npcParam, id), notes,
            who => PersonLooksWrong(charaInit, who.Part?.CharaInitID ?? -1));
        return catalog;
    }

    /// <summary>The Elden Ring map enemies are read from first, for a part to copy when no stand-in has one.</summary>
    public static MSBE.Part.Enemy? AnyEnemyPart(IReadOnlyList<ErEnemy> catalog) => catalog.Select(c => c.Part).OfType<MSBE.Part.Enemy>().FirstOrDefault();
}
