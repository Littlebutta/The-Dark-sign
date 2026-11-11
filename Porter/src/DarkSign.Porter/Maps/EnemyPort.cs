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
    /// "chr &lt;DS3 model&gt; &lt;ER model&gt; &lt;ER NpcParam&gt; &lt;ER ThinkParam&gt; [name]" for any row of a DS3 model, tab-separated.
    /// </summary>
    public sealed class ChrMap
    {
        public const string FileName = "darksign_chr_map.tsv";
        private readonly Dictionary<int, ErEnemy> _byNpc = new();
        private readonly Dictionary<string, ErEnemy> _byModel = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _byNpc.Count + _byModel.Count;

        public static ChrMap Parse(IEnumerable<string> lines)
        {
            var map = new ChrMap();
            foreach (var line in lines)
            {
                var p = line.Split('\t');
                if (line.StartsWith('#') || p.Length < 5 || !int.TryParse(p[3], out var npc) || !int.TryParse(p[4], out var think))
                    continue;
                var name = p.Length > 5 ? p[5] : p[1];
                var enemy = new ErEnemy(p[2], npc, think, name, false, "ported DS3 character");
                if (p[0] == "npc" && int.TryParse(p[1], out var ds3Npc))
                    map._byNpc[ds3Npc] = enemy;
                else if (p[0] == "chr")
                    map._byModel[p[1]] = enemy;
            }
            return map;
        }

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
        Dictionary<string, string>? partNames = null, People? people = null, IReadOnlyDictionary<string, Seat>? seats = null)
    {
        int placed = 0, ported = 0, standIns = 0, npcs = 0, skipped = 0, peoplePlaced = 0, seated = 0, sameSeat = 0;
        var takenSeats = new List<Vector3>();
        var peopleUse = new Dictionary<string, int>();
        var notes = new List<string>();
        var collisions = msb.Parts.Collisions.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = msb.Parts.GetEntries().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextInstance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var standInUse = new Dictionary<string, int>();

        var layerOk = !options.AllLayers && MsbPort.BaseLayerMeans(ds3);
        int otherState = 0;
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
            if (what.Npc)
            {
                // A person: the ported DS3 character if it isn't the human body (Fire Keeper, Ludleth...), else an
                // Elden Ring person. Without either the spot stays empty, as it was before people were placed.
                who = people is null ? null
                    : People.IsHuman(what.Model) ? people.Choose(what, src.TalkID > 0)
                    // A talker with a body of its own (Fire Keeper, Ludleth): the ported body, else a silent person.
                    // c1000 is DS3's invisible talk dummy (bonfires, menus): nothing to stand there.
                    : chooser.Ported(what) ?? (People.IsDummy(what.Model) ? null : people.Choose(what with { Model = People.Human }, talks: true));
                if (who is null)
                {
                    npcs++;
                    continue;
                }
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
            part.ThinkParamID = who.ThinkParam;
            part.TalkID = 0;
            part.PlatoonID = src.PlatoonID;
            // An Elden Ring person keeps the look of the placement it was copied from; anything else has none.
            if (!(what.Npc && who.Source != "ported DS3 character"))
                part.CharaInitID = -1;
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
        if (seated > 0)
            notes.Add($"people: {seated} moved from DS3's holding spot to the seat DS3's scripts warp them to"
                      + (sameSeat > 0 ? $"; {sameSeat} other versions of a seated person left out (DS3 shows one by quest state)" : ""));
        if (peopleUse.Count > 0)
            notes.Add("people: " + string.Join("; ", peopleUse.OrderBy(s => s.Key, StringComparer.Ordinal).Take(30).Select(s => $"{s.Key} x{s.Value}")));
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
    public static People SortPeople(IEnumerable<(ErEnemy Who, bool Talks)> humans, IEnumerable<ErEnemy> catalog, Func<int, int> teamOf, List<string> notes)
    {
        var enemyTeams = catalog.Select(c => teamOf(c.NpcParam)).Where(t => t >= 0).ToHashSet();
        var list = humans.ToList();
        var friendly = list.Where(h => h.Talks).Select(h => h.Who).DistinctBy(h => (h.NpcParam, h.Part?.CharaInitID)).ToList();
        var hostile = list.Where(h => !h.Talks && enemyTeams.Contains(teamOf(h.Who.NpcParam))).Select(h => h.Who)
            .DistinctBy(h => (h.NpcParam, h.Part?.CharaInitID)).ToList();
        notes.Add($"people: {friendly.Count} Elden Ring people with a talk script and {hostile.Count} hostile humans found to stand in for DS3's "
                  + $"(of {list.Count} human placements; enemy teams {string.Join(",", enemyTeams.OrderBy(t => t))})");
        if (hostile.Count > 0)
            notes.Add("people: hostile humans: " + string.Join("; ", hostile.Take(20).Select(h => $"{h.Name} ({h.NpcParam}, {h.Source})")));
        return new People(friendly, hostile);
    }

    public static List<ErEnemy> BuildCatalog(GameArchives er, PARAM? npcParam, List<string> notes, out People people)
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
        people = SortPeople(humans, catalog.Where(c => !c.Boss), id => TeamOf(npcParam, id), notes);
        return catalog;
    }

    /// <summary>The Elden Ring map enemies are read from first, for a part to copy when no stand-in has one.</summary>
    public static MSBE.Part.Enemy? AnyEnemyPart(IReadOnlyList<ErEnemy> catalog) => catalog.Select(c => c.Part).OfType<MSBE.Part.Enemy>().FirstOrDefault();
}
