using System.Globalization;
using System.Text.Json;
using DarkSign.Porter.Maps;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// What a ported DS3 enemy drops: DS3's own item lots with DS3's chances, instead of the Elden Ring template's.
///
/// DS3's NpcParam names up to six lots (ItemLotId1-6); each lot is the row of that ID plus the rows of the IDs right
/// after it, and every row is one roll among its eight slots, weighted by their base points. Elden Ring rolls the same
/// way from ItemLotParam_enemy (NpcParam.itemLotId_enemy: that row and the ones right after it) and keeps drops that
/// happen once (rows with a "got it" flag) in ItemLotParam_map (NpcParam.itemLotId_map). So every DS3 row becomes one
/// Elden Ring row with the same eight slots, points, counts and luck switches, and all of an enemy's rows are laid end
/// to end under one new ID per table.
///
/// A slot's item is the DS3 item's Elden Ring counterpart: the ported weapon (port-weapons' plan), or the Elden Ring
/// item of the same name (the rule the weapon port uses for "Elden Ring already has it"). A DS3 item with neither
/// (armour, rings and most goods, until they are ported) keeps its slot and its points with no item in it, so the
/// chances of everything else in the roll stay exactly DS3's. Work\reports\enemy-drops.tsv lists every lot, every
/// mapping and every DS3 item that has no Elden Ring item yet.
///
/// Launcher Fallbacks window, "enemy-drops": B (DARKSIGN_ENEMY_DROPS=template) keeps the template's Elden Ring drops.
/// </summary>
public sealed class DropPort
{
    public const string ReportFile = "enemy-drops.tsv";

    /// <summary>New lot rows start here in both tables, in blocks of 100; rows Elden Ring already has are stepped over.</summary>
    public const int FirstLot = 900_000_000;

    /// <summary>Most rows one DS3 lot is followed for (DS3's longest are a handful).</summary>
    public const int MaxChain = 32;

    /// <summary>Most Elden Ring flags handed to one-time drops, so the map events (which share the pool) never run out.</summary>
    public const int FlagBudget = 1500;

    public const int Slots = 8;

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_DROPS")?.Trim().ToLowerInvariant() is not ("template" or "0" or "off");

    public enum Kind { Weapon, Armor, Ring, Goods }

    /// <summary>DS3's lot category of each kind (the top nibble of the item's full ID), and Elden Ring's.</summary>
    private static readonly (Kind Kind, uint Ds3, int Er, string Label, string Param, string Text)[] Kinds =
    [
        (Kind.Weapon, 0x00000000, 2, "weapon", "EquipParamWeapon", "WeaponName"),
        (Kind.Armor, 0x10000000, 3, "armour", "EquipParamProtector", "ProtectorName"),
        (Kind.Ring, 0x20000000, 4, "ring", "EquipParamAccessory", "AccessoryName"),
        (Kind.Goods, 0x40000000, 1, "goods", "EquipParamGoods", "GoodsName"),
    ];

    public static int ErCategory(Kind kind) => Kinds.First(k => k.Kind == kind).Er;
    public static string Label(Kind kind) => Kinds.First(k => k.Kind == kind).Label;

    public static Kind? KindOf(long ds3Category) =>
        Kinds.Where(k => k.Ds3 == (uint)ds3Category).Select(k => (Kind?)k.Kind).FirstOrDefault();

    // ---- Items ----

    /// <summary>An Elden Ring item a DS3 item stands for, and why.</summary>
    public sealed record Item(int Id, string How);

    /// <summary>In-game names of both games' items by kind, read while the archives are open (the params step has only Elden Ring's).</summary>
    public sealed class Names
    {
        public Dictionary<Kind, Dictionary<int, string>> Ds3 = [], Er = [];

        public static Names Load(GameArchives er, GameArchives ds3, List<string> notes)
        {
            var names = new Names();
            foreach (var k in Kinds)
            {
                names.Ds3[k.Kind] = Read(ds3, k.Text, notes);
                names.Er[k.Kind] = Read(er, k.Text, notes);
            }
            return names;
        }

        private static Dictionary<int, string> Read(GameArchives archives, string text, List<string> notes)
        {
            try
            {
                return ItemText.Load(archives, text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"drops: {archives.Game.ShortName} {text} not read ({ex.GetType().Name}: {ex.Message}); Paramdex row names are used instead");
                return [];
            }
        }
    }

    /// <summary>DS3 item to Elden Ring item, by kind.</summary>
    public sealed class ItemMap
    {
        private readonly Dictionary<Kind, Dictionary<int, string>> _ds3Names = [];
        private readonly Dictionary<Kind, Dictionary<string, int>> _erByName = [];
        private readonly Dictionary<Kind, HashSet<int>> _erIds = [];
        private readonly Dictionary<int, int> _portedWeapons;

        /// <param name="portedWeapons">DS3 weapon ID to the ported weapon's Elden Ring ID (port-weapons' plan).</param>
        public ItemMap(IReadOnlyDictionary<int, int> portedWeapons)
        {
            _portedWeapons = new Dictionary<int, int>(portedWeapons);
            foreach (var k in Kinds)
            {
                _ds3Names[k.Kind] = [];
                _erByName[k.Kind] = [];
                _erIds[k.Kind] = [];
            }
        }

        /// <summary>One kind's tables: DS3 names, Elden Ring names, and the Elden Ring rows that exist (null: trust the names).</summary>
        public void Add(Kind kind, IReadOnlyDictionary<int, string> ds3Names, IReadOnlyDictionary<int, string> erNames, IEnumerable<int>? erRows)
        {
            foreach (var (id, name) in ds3Names)
                _ds3Names[kind][id] = name;
            var rows = erRows?.ToHashSet();
            _erIds[kind] = rows ?? erNames.Keys.ToHashSet();
            // The lowest ID of a name: the base item (a weapon's standard row, the first of an armour's altered copies).
            foreach (var (id, name) in erNames.OrderBy(n => n.Key))
            {
                if (id <= 0 || (rows is not null && !rows.Contains(id)) || (kind == Kind.Weapon && id % 10000 != 0))
                    continue;
                var key = ItemText.Normalize(name);
                if (key.Length > 0 && !name.StartsWith('['))
                    _erByName[kind].TryAdd(key, id);
            }
        }

        public string Ds3Name(Kind kind, int id) =>
            _ds3Names[kind].TryGetValue(kind == Kind.Weapon ? BaseWeapon(id) : id, out var name) ? name : "";

        /// <summary>
        /// A DS3 weapon ID without its reinforcement level and infusion (DS3: base + infusion * 100 + level). Arrows and
        /// bolts (IDs below 1,000,000, a hundred apart: Standard Arrow 400000, Fire Arrow 400100) have neither.
        /// </summary>
        public static int BaseWeapon(int id) => id < 1_000_000 ? id : id / 10000 * 10000;

        /// <summary>The Elden Ring item for a DS3 item, or null when there is none yet.</summary>
        public Item? Map(Kind kind, int ds3Id)
        {
            if (ds3Id <= 0)
                return null;
            if (kind == Kind.Weapon)
            {
                // Drops are the plain weapon: Elden Ring's reinforcement levels and affinities are other numbers.
                int baseId = BaseWeapon(ds3Id);
                if (_portedWeapons.TryGetValue(baseId, out var ported) && _erIds[kind].Contains(ported))
                    return new Item(ported, baseId == ds3Id ? "ported weapon" : "ported weapon (DS3's level or infusion left off)");
                ds3Id = baseId;
            }
            var key = ItemText.Normalize(Ds3Name(kind, ds3Id));
            if (key.Length > 0 && _erByName[kind].TryGetValue(key, out var same) && _erIds[kind].Contains(same))
                return new Item(same, "Elden Ring item of the same name");
            return null;
        }
    }

    // ---- The port ----

    private sealed record Slot(Kind Kind, int Ds3Item, string Name, int Count, int Points, Item? Er);

    private sealed record LotRow(int Ds3Id, List<Slot?> SlotList, int Total, long Flag, bool Pity);

    private readonly Dictionary<int, PARAM.Row> _ds3Lots;
    private readonly PARAM _erEnemy, _erMap;
    private readonly HashSet<int> _usedEnemy, _usedMap;
    private readonly object?[] _enemyDefaults, _mapDefaults;
    private readonly ItemMap _items;
    private readonly Func<long, long>? _moveFlag;
    private readonly Dictionary<string, int> _enemyChains = [], _mapChains = [];
    private readonly Dictionary<int, LotRow?> _rows = [];
    private readonly List<string> _npcLines = [];
    private readonly HashSet<int> _reported = [];
    private readonly List<string> _lotLines = [];
    private readonly Dictionary<(Kind, int), (string Name, HashSet<int> Npcs, int Rows)> _missing = [];
    private readonly Dictionary<(Kind, int), (string Name, Item Er, int Rows)> _mapped = [];
    private readonly Dictionary<long, long> _flags = [];
    private int _nextEnemy = FirstLot, _nextMap = FirstLot;

    public int Npcs, NpcsWithDrops, EnemyRows, MapRows, EmptyRows, SlotsMapped, SlotsMissing, PityRows, FlagsLost;
    public List<string> Notes = [];

    public DropPort(PARAM ds3Lots, PARAM erEnemyLots, PARAM erMapLots, ItemMap items, Func<long, long>? moveFlag)
    {
        _ds3Lots = [];
        foreach (var row in ds3Lots.Rows)
            _ds3Lots.TryAdd(row.ID, row);
        _erEnemy = erEnemyLots;
        _erMap = erMapLots;
        _usedEnemy = erEnemyLots.Rows.Select(r => r.ID).ToHashSet();
        _usedMap = erMapLots.Rows.Select(r => r.ID).ToHashSet();
        _enemyDefaults = ParamDefaults.Common(erEnemyLots);
        _mapDefaults = ParamDefaults.Common(erMapLots);
        _items = items;
        _moveFlag = moveFlag;
    }

    /// <summary>
    /// The drop port over the mod's regulation, or null (with a note) when a param it needs can't be read. The DS3
    /// flags of one-time drops get Elden Ring flags from the same table the map events use (darksign_flags.tsv).
    /// </summary>
    public static DropPort? Create(PorterContext ctx, ParamStore er, ParamStore ds3, Names? names, List<string> notes)
    {
        if (!Enabled)
        {
            notes.Add("drops: off (Fallbacks enemy-drops B); ported enemies keep their template's Elden Ring drops");
            return null;
        }
        try
        {
            var ds3Lots = ds3.Get("ItemLotParam") ?? throw new InvalidDataException("DS3 has no ItemLotParam");
            var enemy = er.Get("ItemLotParam_enemy") ?? throw new InvalidDataException("Elden Ring has no ItemLotParam_enemy");
            var map = er.Get("ItemLotParam_map") ?? throw new InvalidDataException("Elden Ring has no ItemLotParam_map");
            var items = new ItemMap(PortedWeapons(ctx, notes));
            foreach (var k in Kinds)
            {
                PARAM? ds3Param = TryGet(ds3, k.Param, notes), erParam = TryGet(er, k.Param, notes);
                items.Add(k.Kind,
                    names?.Ds3.GetValueOrDefault(k.Kind) is { Count: > 0 } d ? d : RowNames(ds3Param),
                    names?.Er.GetValueOrDefault(k.Kind) is { Count: > 0 } e ? e : RowNames(erParam),
                    erParam?.Rows.Select(r => r.ID));
            }
            var flagPath = ctx.ModPath(MapEvents.FlagFile);
            var flags = FlagMap.Load(flagPath);
            var port = new DropPort(ds3Lots, enemy, map, items, FlagMover(flags));
            port._saveFlags = () => flags.Save(flagPath);
            return port;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"drops: not ported, ported enemies keep their template's Elden Ring drops ({ChrPort.Describe(ex)})");
            return null;
        }
    }

    private Action? _saveFlags;

    /// <summary>
    /// A DS3 flag as the map events move it: a ported map's own flag goes with the map into its slot, any other flag
    /// gets a number of its own (<see cref="FlagMap"/>).
    /// </summary>
    public static Func<long, long> FlagMover(FlagMap flags)
    {
        var maps = MapSlots.All.Where(s => !s.Probe).Select(s => (s.Ds3, s.Er)).ToList();
        return flag =>
        {
            foreach (var (ds3, er) in maps)
                if (ds3.OwnsDs3Id(flag))
                    return MapFlags.Common ? flags.LocalFlag(flag) : ds3.MoveEntity((int)flag, er);
            return flags.Flag(flag);
        };
    }

    private static PARAM? TryGet(ParamStore store, string name, List<string> notes)
    {
        try
        {
            return store.Get(name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"drops: {name} not read ({ex.GetType().Name}: {ex.Message}); its items are matched by text only");
            return null;
        }
    }

    public static Dictionary<int, string> RowNames(PARAM? param) =>
        param?.Rows.Where(r => !string.IsNullOrWhiteSpace(r.Name)).GroupBy(r => r.ID).ToDictionary(g => g.Key, g => g.First().Name!) ?? [];

    /// <summary>DS3 weapon ID to ported Elden Ring weapon ID, from the plan port-weapons wrote before the regulation.</summary>
    public static Dictionary<int, int> PortedWeapons(PorterContext ctx, List<string> notes)
    {
        var path = ctx.WorkPath("reports", "weapons-plan.json");
        try
        {
            var plans = JsonSerializer.Deserialize<List<WeaponPlan>>(File.ReadAllText(path)) ?? [];
            return plans.Where(p => p.Status == "port" && p.NewId is not null).GroupBy(p => p.Ds3Id).ToDictionary(g => g.Key, g => g.First().NewId!.Value);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            notes.Add($"drops: the weapon plan (Work\\reports\\weapons-plan.json) was not read ({ex.GetType().Name}); DS3 weapons drop only where Elden Ring has a weapon of the same name");
            return [];
        }
    }

    /// <summary>
    /// Gives an Elden Ring NpcParam row the drops of a DS3 NpcParam row: its lots are ported (once, however many rows
    /// share them) and the row's two lot fields name them, or -1 where DS3 drops nothing.
    /// </summary>
    public void Apply(PARAM.Row ds3Npc, PARAM.Row erNpc)
    {
        if (Cell(erNpc, "itemLotId_enemy") is not { } enemyCell || Cell(erNpc, "itemLotId_map") is not { } mapCell)
            return;
        int enemy, map;
        try
        {
            (enemy, map) = Port(ds3Npc);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One row's lots failing costs that row its DS3 drops (it keeps the template's), not its NpcParam row.
            if (Notes.Count(n => n.Contains("drops not ported", StringComparison.Ordinal)) < 5)
                Notes.Add($"drops: DS3 NpcParam {ds3Npc.ID}: drops not ported, the template's kept ({ChrPort.Describe(ex)})");
            return;
        }
        enemyCell.Value = enemy;
        mapCell.Value = map;
        _npcLines.Add(string.Join('\t', "npc", ds3Npc.ID, Clean(ds3Npc.Name), erNpc.ID, enemy, map));
    }

    /// <summary>The Elden Ring lots (ItemLotParam_enemy, ItemLotParam_map) for a DS3 NpcParam row, -1 for none.</summary>
    public (int Enemy, int Map) Port(PARAM.Row ds3Npc)
    {
        Npcs++;
        var rows = new List<LotRow>();
        for (int i = 1; i <= 6; i++)
        {
            int lot = Int(ds3Npc, $"ItemLotId{i}");
            for (int k = 0; lot > 0 && k < MaxChain && _ds3Lots.ContainsKey(lot + k); k++)
                if (Row(lot + k) is { } row && !rows.Contains(row))
                    rows.Add(row);
        }
        foreach (var row in rows)
            foreach (var slot in row.SlotList)
                if (slot is { Er: null } && _missing.TryGetValue((slot.Kind, slot.Ds3Item), out var m))
                    m.Npcs.Add(ds3Npc.ID);
        // A row with nothing Elden Ring can give would only be a roll for nothing: left out, the other rows roll the same.
        var live = rows.Where(r => r.SlotList.Any(s => s?.Er is not null)).ToList();
        if (live.Count > 0)
            NpcsWithDrops++;
        return (Chain(live.Where(r => r.Flag <= 0).ToList(), _erEnemy, _usedEnemy, _enemyDefaults, _enemyChains, ref _nextEnemy, ref EnemyRows),
                // A one-time drop whose flag found no room would drop on every kill: it is left out instead.
                Chain(live.Where(r => r.Flag > 0 && MoveFlag(r.Flag) > 0).ToList(), _erMap, _usedMap, _mapDefaults, _mapChains, ref _nextMap, ref MapRows));
    }

    /// <summary>One DS3 lot row, read and mapped once.</summary>
    private LotRow? Row(int id)
    {
        if (_rows.TryGetValue(id, out var done))
            return done;
        var ds3 = _ds3Lots[id];
        var slots = new List<Slot?>();
        int total = 0;
        bool pity = Int(ds3, "cumulateNumMax") > 0 || Int(ds3, "cumulateNumFlagId") > 0;
        for (int s = 1; s <= Slots; s++)
        {
            int points = Math.Max(0, Int(ds3, $"LotItemBasePoint{s:D2}"));
            total += points;
            int item = Int(ds3, $"ItemLotId{s}"), count = Int(ds3, $"LotItemNum{s}");
            pity |= Int(ds3, $"cumulateLotPoint{s:D2}") > 0;
            var kind = Cell(ds3, $"LotItemCategory{s:D2}")?.Value is { } c ? KindOf(Convert.ToInt64(c)) : null;
            if (points <= 0 || item <= 0 || count <= 0 || kind is null)
            {
                slots.Add(points > 0 ? new Slot(Kind.Goods, 0, "", 0, points, null) : null);
                continue;
            }
            var er = _items.Map(kind.Value, item);
            var name = _items.Ds3Name(kind.Value, item);
            slots.Add(new Slot(kind.Value, item, name, count, points, er));
        }
        long flag = Int(ds3, "getItemFlagId");
        var row = total > 0 ? new LotRow(id, slots, total, flag, pity) : null;
        _rows[id] = row;
        if (row is null)
            return null;
        foreach (var slot in slots.Where(s => s is { Ds3Item: > 0 }))
        {
            var key = (slot!.Kind, slot.Ds3Item);
            if (slot.Er is null)
            {
                SlotsMissing++;
                if (!_missing.TryGetValue(key, out var have))
                    have = (slot.Name, new HashSet<int>(), 0);
                _missing[key] = (have.Name, have.Npcs, have.Rows + 1);
            }
            else
            {
                SlotsMapped++;
                _mapped[key] = (slot.Name, slot.Er, (_mapped.TryGetValue(key, out var have) ? have.Rows : 0) + 1);
            }
        }
        if (pity)
            PityRows++;
        if (!slots.Any(s => s?.Er is not null))
            EmptyRows++;
        return row;
    }

    /// <summary>The Elden Ring ID of these rows laid end to end in one table (made the first time), or -1 for none.</summary>
    private int Chain(List<LotRow> rows, PARAM table, HashSet<int> used, object?[] defaults, Dictionary<string, int> chains, ref int next, ref int count)
    {
        if (rows.Count == 0)
            return -1;
        var key = string.Join(',', rows.Select(r => r.Ds3Id));
        if (chains.TryGetValue(key, out var have))
            return have;
        // The game rolls the row a character names and every row whose ID follows on: a new chain starts on a round
        // hundred with no row right before it or right after its last one.
        int step = (rows.Count + 1 + 99) / 100 * 100, start = next;
        while (Enumerable.Range(start - 1, rows.Count + 2).Any(used.Contains))
            start += 100;
        next = start + step;
        for (int i = 0; i < rows.Count; i++)
        {
            table.Rows.Add(ErRow(table, defaults, start + i, rows[i]));
            used.Add(start + i);
            count++;
            Report(rows[i], table == _erMap ? "map" : "enemy", start + i);
        }
        return chains[key] = start;
    }

    private PARAM.Row ErRow(PARAM table, object?[] defaults, int id, LotRow from)
    {
        var ds3 = _ds3Lots[from.Ds3Id];
        var row = new PARAM.Row(id, $"[DS3] {(string.IsNullOrWhiteSpace(ds3.Name) ? $"lot {from.Ds3Id}" : ds3.Name)}", table.AppliedParamdef);
        // What Elden Ring's own rows mostly hold (who the drop is rolled for in co-op, rarity) where DS3 says nothing.
        for (int i = 0; i < row.Cells.Count; i++)
            if (defaults[i] is { } common)
                row.Cells[i].Value = common;
        Set(row, "getItemFlagId", MoveFlag(from.Flag));
        Set(row, "cumulateNumFlagId", 0);
        Set(row, "cumulateNumMax", 0);
        if (Cell(ds3, "ClearCount")?.Value is { } clear)
            Set(row, "GameClearOffset", Convert.ToInt32(clear));
        for (int s = 1; s <= Slots; s++)
        {
            var slot = from.SlotList[s - 1];
            Set(row, $"lotItemId{s:D2}", slot?.Er?.Id ?? 0);
            Set(row, $"lotItemCategory{s:D2}", slot?.Er is null ? 0 : ErCategory(slot.Kind));
            Set(row, $"lotItemBasePoint{s:D2}", slot?.Points ?? 0);
            Set(row, $"lotItemNum{s:D2}", slot?.Er is null ? 0 : Math.Min(slot.Count, byte.MaxValue));
            Set(row, $"enableLuck{s:D2}", slot is null ? 0 : Int(ds3, $"EnableLuck{s:D2}"));
            // DS3's rising chance after dry kills (cumulate fields) counts in flags Elden Ring has no room set aside for.
            Set(row, $"cumulateLotPoint{s:D2}", 0);
            Set(row, $"cumulateReset{s:D2}", 0);
            Set(row, $"getItemFlagId{s:D2}", slot?.Er is null ? 0 : MoveFlag(Int(ds3, $"GetItemFlagId{s:D2}")));
        }
        return row;
    }

    private long MoveFlag(long ds3Flag)
    {
        if (ds3Flag <= 0)
            return 0;
        if (_flags.TryGetValue(ds3Flag, out var have))
            return have;
        long er = 0;
        try
        {
            if (_moveFlag is not null && _flags.Count < FlagBudget)
                er = _moveFlag(ds3Flag);
        }
        catch (InvalidOperationException)
        {
        }
        if (er <= 0)
        {
            er = 0;
            FlagsLost++;
        }
        return _flags[ds3Flag] = er;
    }

    private void Report(LotRow row, string table, int erId)
    {
        if (!_reported.Add(row.Ds3Id))
            return;
        for (int s = 0; s < Slots; s++)
        {
            if (row.SlotList[s] is not { } slot)
                continue;
            string chance = (100.0 * slot.Points / row.Total).ToString("0.###", CultureInfo.InvariantCulture);
            _lotLines.Add(slot.Ds3Item <= 0
                ? string.Join('\t', "drop", row.Ds3Id, s + 1, "nothing", "", "", "", chance, table, erId, "", "")
                : string.Join('\t', "drop", row.Ds3Id, s + 1, Label(slot.Kind), slot.Ds3Item, Clean(slot.Name), slot.Count, chance, table, erId,
                    slot.Er?.Id.ToString() ?? "", slot.Er?.How ?? "NOT PORTED: the slot keeps its chance and gives nothing"));
        }
    }

    /// <summary>Sorts the tables, saves the flag numbers handed out and writes the report. Returns the summary line.</summary>
    public string Finish(PorterContext? ctx)
    {
        _erEnemy.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        _erMap.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        Check();
        if (ctx is not null)
        {
            try
            {
                if (_flags.Values.Any(f => f > 0))
                    _saveFlags?.Invoke();
                File.WriteAllLines(ctx.WorkPath("reports", ReportFile), ReportLines());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notes.Add($"drops: report or flag table not written ({ex.Message})");
            }
        }
        return Summary;
    }

    /// <summary>Reads the new rows back: every chain an enemy names stands alone, and every item is one the regulation has.</summary>
    private void Check()
    {
        foreach (var (table, chains, name) in new[] { (_erEnemy, _enemyChains, "ItemLotParam_enemy"), (_erMap, _mapChains, "ItemLotParam_map") })
        {
            var ids = table.Rows.Select(r => r.ID).ToList();
            if (ids.Count != ids.Distinct().Count())
                Notes.Add($"drops: FAIL: {name} has a row ID twice");
            var set = ids.ToHashSet();
            foreach (var (key, start) in chains)
            {
                int n = key.Split(',').Length;
                if (set.Contains(start - 1) || set.Contains(start + n) || Enumerable.Range(start, n).Any(i => !set.Contains(i)))
                    Notes.Add($"drops: FAIL: {name} chain {start} (DS3 {key}) runs into another row or has a gap");
            }
        }
    }

    public string Summary =>
        $"drops: {NpcsWithDrops} of {Npcs} ported enemy rows drop DS3's items ({EnemyRows} lot rows, {MapRows} one-time rows); " +
        $"{_mapped.Count} DS3 items have an Elden Ring item, {_missing.Count} have none yet and give nothing ({EmptyRows} lot rows hold only such items)" +
        (PityRows > 0 ? $"; {PityRows} rows lose DS3's rising chance after dry kills" : "") +
        (FlagsLost > 0 ? $"; {FlagsLost} one-time flags had no room and are off" : "") +
        $" (report: Work\\reports\\{ReportFile})";

    public IEnumerable<string> ReportLines()
    {
        yield return "# What ported DS3 enemies drop (DarkSignPorter port-maps, enemy params). " + Summary;
        yield return "# Chances are DS3's: a slot's points out of its row's total. A DS3 item without an Elden Ring item keeps its slot and gives nothing.";
        foreach (var note in Notes)
            yield return "# " + note;
        yield return "";
        yield return "# DS3 items with no Elden Ring item yet, by kind, then by how many ported enemy rows drop them";
        yield return "missing\tkind\tDS3 item\tname\tenemy rows\tlot rows";
        foreach (var (key, m) in _missing.OrderBy(m => m.Key.Item1).ThenByDescending(m => m.Value.Npcs.Count).ThenBy(m => m.Key.Item2))
            yield return string.Join('\t', "missing", Label(key.Item1), key.Item2, Clean(m.Name), m.Npcs.Count, m.Rows);
        yield return "";
        yield return "# DS3 items that drop as an Elden Ring item";
        yield return "mapped\tkind\tDS3 item\tname\tElden Ring item\thow\tlot rows";
        foreach (var (key, m) in _mapped.OrderBy(m => m.Key.Item1).ThenBy(m => m.Key.Item2))
            yield return string.Join('\t', "mapped", Label(key.Item1), key.Item2, Clean(m.Name), m.Er.Id, m.Er.How, m.Rows);
        yield return "";
        yield return "# Every ported lot row, slot by slot";
        yield return "drop\tDS3 lot row\tslot\tkind\tDS3 item\tname\tcount\tchance %\tElden Ring table\tElden Ring row\tElden Ring item\thow";
        foreach (var line in _lotLines)
            yield return line;
        yield return "";
        yield return "# Ported enemy rows and the lots they name (-1: drops nothing)";
        yield return "npc\tDS3 NpcParam\tname\tElden Ring NpcParam\tItemLotParam_enemy\tItemLotParam_map";
        foreach (var line in _npcLines)
            yield return line;
    }

    /// <summary>Names of the DS3 items of a kind that have no Elden Ring item yet (most dropped first).</summary>
    public IEnumerable<string> Missing(Kind kind) =>
        _missing.Where(m => m.Key.Item1 == kind).OrderByDescending(m => m.Value.Npcs.Count).Select(m => m.Value.Name.Length > 0 ? m.Value.Name : m.Key.Item2.ToString());

    // ---- drops-plan ----

    /// <summary>
    /// drops-plan: a dry run on the two regulations alone (no archives, nothing written to the mod): ports the drops
    /// of every DS3 NpcParam row that names a lot and writes Work\reports\enemy-drops-plan.tsv. Item names are
    /// Paramdex's row names; ported weapons come from the last port-weapons run's plan when there is one.
    /// </summary>
    public static int Plan(PorterContext ctx)
    {
        var notes = new List<string>();
        var ds3 = ctx.LoadParams(PorterGame.DarkSouls3);
        var er = ActionButtons.LoadRegulation(ctx);
        var ds3Lots = ds3.Get("ItemLotParam") ?? throw new InvalidDataException("DS3 has no ItemLotParam");
        var enemy = er.Get("ItemLotParam_enemy") ?? throw new InvalidDataException("Elden Ring has no ItemLotParam_enemy");
        var map = er.Get("ItemLotParam_map") ?? throw new InvalidDataException("Elden Ring has no ItemLotParam_map");
        var npcs = ds3.Get("NpcParam") ?? throw new InvalidDataException("DS3 has no NpcParam");
        var items = new ItemMap(PortedWeapons(ctx, notes));
        foreach (var k in Kinds)
        {
            var erParam = TryGet(er, k.Param, notes);
            items.Add(k.Kind, RowNames(TryGet(ds3, k.Param, notes)), RowNames(erParam), erParam?.Rows.Select(r => r.ID));
        }
        var port = new DropPort(ds3Lots, enemy, map, items, FlagMover(new FlagMap()));
        port.Notes.AddRange(notes);
        int before = enemy.Rows.Count + map.Rows.Count;
        foreach (var npc in npcs.Rows.Where(r => Enumerable.Range(1, 6).Any(i => Int(r, $"ItemLotId{i}") > 0)))
        {
            var (e, m) = port.Port(npc);
            port._npcLines.Add(string.Join('\t', "npc", npc.ID, Clean(npc.Name), "", e, m));
        }
        port.Finish(null);
        var path = ctx.WorkPath("reports", "enemy-drops-plan.tsv");
        File.WriteAllLines(path, port.ReportLines());
        Console.WriteLine(port.Summary.Replace(ReportFile, "enemy-drops-plan.tsv"));
        Console.WriteLine($"drops: {enemy.Rows.Count + map.Rows.Count - before} rows would be added; Elden Ring item discovery and luck switches are DS3's");
        foreach (var k in Kinds)
        {
            var missing = port.Missing(k.Kind).ToList();
            Console.WriteLine($"drops: {k.Label}: {missing.Count} DS3 items without an Elden Ring item{(missing.Count > 0 ? ": " + string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? ", ..." : "") : "")}");
        }
        foreach (var note in port.Notes)
            Console.WriteLine(note);
        Console.WriteLine($"report: {path}");
        return port.Notes.Any(n => n.Contains("FAIL", StringComparison.Ordinal)) ? 1 : 0;
    }

    // ---- Self-test ----

    private static PARAM TestParam(params (string Name, PARAMDEF.DefType Type)[] fields)
    {
        var def = new PARAMDEF();
        foreach (var (name, type) in fields)
            def.Fields.Add(new PARAMDEF.Field(def, type, name));
        var param = new PARAM { Rows = [] };
        param.ApplyParamdef(def);
        return param;
    }

    private static PARAM.Row TestRow(PARAM param, int id, params (string Field, object Value)[] values)
    {
        var row = new PARAM.Row(id, "", param.AppliedParamdef);
        foreach (var (field, value) in values)
            row[field].Value = Convert.ChangeType(value, row[field].Value.GetType());
        param.Rows.Add(row);
        return row;
    }

    /// <summary>Offline checks on small params built in memory (part of enemy-selftest).</summary>
    public static void SelfTest(Action<string, bool> check)
    {
        const PARAMDEF.DefType S32 = PARAMDEF.DefType.s32, U32 = PARAMDEF.DefType.u32, S16 = PARAMDEF.DefType.s16, U16 = PARAMDEF.DefType.u16,
            U8 = PARAMDEF.DefType.u8, S8 = PARAMDEF.DefType.s8;
        var ds3Fields = new List<(string, PARAMDEF.DefType)>();
        var erFields = new List<(string, PARAMDEF.DefType)>();
        for (int s = 1; s <= Slots; s++)
        {
            ds3Fields.AddRange([($"ItemLotId{s}", S32), ($"LotItemCategory{s:D2}", U32), ($"LotItemBasePoint{s:D2}", S16), ($"cumulateLotPoint{s:D2}", S16),
                ($"GetItemFlagId{s:D2}", S32), ($"LotItemNum{s}", U8), ($"EnableLuck{s:D2}", U8)]);
            erFields.AddRange([($"lotItemId{s:D2}", S32), ($"lotItemCategory{s:D2}", S32), ($"lotItemBasePoint{s:D2}", U16), ($"cumulateLotPoint{s:D2}", U16),
                ($"getItemFlagId{s:D2}", U32), ($"lotItemNum{s:D2}", U8), ($"enableLuck{s:D2}", U16)]);
        }
        ds3Fields.AddRange([("getItemFlagId", S32), ("cumulateNumFlagId", S32), ("cumulateNumMax", U8), ("ClearCount", S8)]);
        erFields.AddRange([("getItemFlagId", U32), ("cumulateNumFlagId", U32), ("cumulateNumMax", U8), ("GameClearOffset", S8), ("canExecByFriendlyGhost", U8)]);
        var ds3Lots = TestParam([.. ds3Fields]);
        PARAM erEnemy = TestParam([.. erFields]), erMap = TestParam([.. erFields]);
        // Elden Ring's own rows: most let a co-op phantom's kill roll the drop; one sits where the second new chain would go.
        TestRow(erEnemy, 100, ("canExecByFriendlyGhost", 1), ("lotItemId01", 5), ("GameClearOffset", -1));
        TestRow(erEnemy, 200, ("canExecByFriendlyGhost", 1), ("GameClearOffset", -1));
        TestRow(erEnemy, FirstLot + 100, ("canExecByFriendlyGhost", 0));
        const uint weapon = 0x00000000, armour = 0x10000000, goods = 0x40000000, none = 0xFFFFFFFF;
        // Lot 1000: two rows. Row 1000: 800 nothing, 150 ported sword (+3 in DS3), 50 unported helm. Row 1001: same-name goods x2 with luck.
        TestRow(ds3Lots, 1000, ("LotItemCategory01", none), ("LotItemBasePoint01", 800),
            ("ItemLotId2", 2000003), ("LotItemCategory02", weapon), ("LotItemBasePoint02", 150), ("LotItemNum2", 1), ("EnableLuck02", 1),
            ("ItemLotId3", 51000000), ("LotItemCategory03", armour), ("LotItemBasePoint03", 50), ("LotItemNum3", 1), ("EnableLuck03", 1),
            ("getItemFlagId", -1), ("ClearCount", -1));
        TestRow(ds3Lots, 1001, ("LotItemCategory01", none), ("LotItemBasePoint01", 900),
            ("ItemLotId2", 296), ("LotItemCategory02", goods), ("LotItemBasePoint02", 100), ("LotItemNum2", 2), ("EnableLuck02", 1), ("getItemFlagId", -1), ("ClearCount", -1));
        // Lot 2000: a one-time drop (flag) of a weapon Elden Ring has under the same name.
        TestRow(ds3Lots, 2000, ("ItemLotId1", 100000), ("LotItemCategory01", weapon), ("LotItemBasePoint01", 1000), ("LotItemNum1", 1), ("getItemFlagId", 50006200));
        // Lot 3000: only unported items. Lot 4000: another enemy's goods, with DS3's rising chance.
        TestRow(ds3Lots, 3000, ("ItemLotId1", 51000000), ("LotItemCategory01", armour), ("LotItemBasePoint01", 1000), ("LotItemNum1", 1));
        TestRow(ds3Lots, 4000, ("ItemLotId1", 296), ("LotItemCategory01", goods), ("LotItemBasePoint01", 10), ("LotItemNum1", 1),
            ("LotItemCategory02", none), ("LotItemBasePoint02", 990), ("cumulateLotPoint01", 5), ("cumulateNumMax", 10), ("cumulateNumFlagId", 61000000));

        var items = new ItemMap(new Dictionary<int, int> { [2000000] = 80010000, [2010000] = 80990000 });
        items.Add(Kind.Weapon, new Dictionary<int, string> { [2000000] = "Lothric Knight Sword", [2010000] = "Unbuilt Sword", [100000] = "Dagger" },
            new Dictionary<int, string> { [1000000] = "Dagger", [1000100] = "Heavy Dagger", [80010000] = "Lothric Knight Sword", [50020000] = "Fire Arrow" },
            [1000000, 1000100, 80010000, 50020000]);
        items.Add(Kind.Weapon, new Dictionary<int, string> { [400000] = "Standard Arrow", [400100] = "Fire Arrow" }, new Dictionary<int, string>(), [1000000, 1000100, 80010000, 50020000]);
        items.Add(Kind.Armor, new Dictionary<int, string> { [51000000] = "Lothric Knight Helm" }, new Dictionary<int, string> { [10000] = "Iron Helmet" }, [10000]);
        items.Add(Kind.Ring, new Dictionary<int, string>(), new Dictionary<int, string>(), []);
        items.Add(Kind.Goods, new Dictionary<int, string> { [296] = "Throwing Knife" }, new Dictionary<int, string> { [1700] = "Throwing knife", [1701] = "Throwing Knife" }, [1700, 1701]);
        check("items: a ported weapon by the plan, a level or infusion left off, a weapon whose ported row is missing unmapped",
            items.Map(Kind.Weapon, 2000000) is { Id: 80010000 } && items.Map(Kind.Weapon, 2000003) is { Id: 80010000 } && items.Map(Kind.Weapon, 2000100) is { Id: 80010000 }
            && items.Map(Kind.Weapon, 2010000) is null);
        check("items: arrows are a hundred apart and keep their own ID and name (Fire Arrow is not Standard Arrow)",
            ItemMap.BaseWeapon(400100) == 400100 && items.Ds3Name(Kind.Weapon, 400100) == "Fire Arrow" && items.Map(Kind.Weapon, 400100) is { Id: 50020000 }
            && items.Map(Kind.Weapon, 400000) is null);
        check("items: the Elden Ring item of the same name (its lowest ID), nothing for an item Elden Ring lacks",
            items.Map(Kind.Weapon, 100000) is { Id: 1000000 } && items.Map(Kind.Goods, 296) is { Id: 1700 } && items.Map(Kind.Armor, 51000000) is null
            && items.Map(Kind.Goods, 0) is null);

        var npcParam = TestParam(Enumerable.Range(1, 6).Select(i => ($"ItemLotId{i}", S32)).ToArray());
        var erNpcParam = TestParam(("itemLotId_enemy", S32), ("itemLotId_map", S32));
        var flagMap = new FlagMap();
        var port = new DropPort(ds3Lots, erEnemy, erMap, items, f => flagMap.Flag(f));
        var knight = TestRow(npcParam, 1, ("ItemLotId1", 1000), ("ItemLotId2", 2000), ("ItemLotId3", -1));
        var erKnight = TestRow(erNpcParam, 70000000, ("itemLotId_enemy", 555), ("itemLotId_map", 666));
        port.Apply(knight, erKnight);
        int lot = (int)erKnight["itemLotId_enemy"].Value, mapLot = (int)erKnight["itemLotId_map"].Value;
        PARAM.Row? r0 = erEnemy[lot], r1 = erEnemy[lot + 1], m0 = erMap[mapLot];
        check($"an enemy's random rows go end to end into ItemLotParam_enemy from {FirstLot}, its one-time row into ItemLotParam_map ({lot}, {mapLot})",
            lot == FirstLot && mapLot == FirstLot && r0 is not null && r1 is not null && erEnemy[lot + 2] is null && m0 is not null && erMap[mapLot + 1] is null);
        check("a row keeps DS3's points slot for slot: nothing 800, the ported weapon 150, and 50 for the unported helm with no item in it",
            r0 is not null && Int(r0, "lotItemBasePoint01") == 800 && Int(r0, "lotItemId01") == 0 && Int(r0, "lotItemCategory01") == 0
            && Int(r0, "lotItemBasePoint02") == 150 && Int(r0, "lotItemId02") == 80010000 && Int(r0, "lotItemCategory02") == 2 && Int(r0, "lotItemNum02") == 1
            && Int(r0, "lotItemBasePoint03") == 50 && Int(r0, "lotItemId03") == 0 && Int(r0, "lotItemCategory03") == 0 && Int(r0, "lotItemNum03") == 0
            && Int(r0, "lotItemBasePoint04") == 0);
        check("goods keep their count and luck switch; Elden Ring's usual co-op setting and DS3's game cycle are on the new row",
            r1 is not null && Int(r1, "lotItemId02") == 1700 && Int(r1, "lotItemCategory02") == 1 && Int(r1, "lotItemNum02") == 2 && Int(r1, "enableLuck02") == 1
            && Int(r1, "enableLuck01") == 0 && Int(r1, "canExecByFriendlyGhost") == 1 && Int(r1, "GameClearOffset") == -1 && Int(r1, "lotItemId01") == 0);
        check("a one-time drop keeps its flag as an Elden Ring flag, the same one the map events get for it",
            m0 is not null && Int(m0, "getItemFlagId") == FlagMap.FlagSlot(0) && flagMap.Flag(50006200) == FlagMap.FlagSlot(0)
            && Int(m0, "lotItemId01") == 1000000 && Int(m0, "lotItemCategory01") == 2 && r0 is not null && Int(r0, "getItemFlagId") == 0);

        var second = TestRow(erNpcParam, 70000001);
        port.Apply(knight, second);
        check("rows that share DS3 lots share the new ones", (int)second["itemLotId_enemy"].Value == lot && (int)second["itemLotId_map"].Value == mapLot
            && erEnemy.Rows.Count == 5 && erMap.Rows.Count == 1);
        var hollow = TestRow(erNpcParam, 70010000);
        port.Apply(TestRow(npcParam, 2, ("ItemLotId1", 4000), ("ItemLotId2", 3000)), hollow);
        int hollowLot = (int)hollow["itemLotId_enemy"].Value;
        check($"the next chain steps over a row Elden Ring already has ({hollowLot}); a row of only unported items is left out; DS3's rising chance is cleared and counted",
            hollowLot == FirstLot + 200 && erEnemy[hollowLot + 1] is null && (int)hollow["itemLotId_map"].Value == -1 && port.EmptyRows == 1 && port.PityRows == 1
            && erEnemy[hollowLot] is { } h && Int(h, "cumulateLotPoint01") == 0 && Int(h, "cumulateNumMax") == 0 && Int(h, "lotItemBasePoint01") == 10 && Int(h, "lotItemBasePoint02") == 990);
        var naked = TestRow(erNpcParam, 70020000, ("itemLotId_enemy", 555), ("itemLotId_map", 666));
        port.Apply(TestRow(npcParam, 3, ("ItemLotId1", 3000), ("ItemLotId2", -1)), naked);
        check("an enemy DS3 gives nothing Elden Ring has drops nothing, not the template's items", (int)naked["itemLotId_enemy"].Value == -1 && (int)naked["itemLotId_map"].Value == -1);
        port.Finish(null);
        var report = port.ReportLines().ToList();
        check("the report lists the unported item with the enemies that drop it, every mapping and no failed check",
            report.Any(l => l.StartsWith("missing\tarmour\t51000000\tLothric Knight Helm\t3\t2", StringComparison.Ordinal))
            && report.Any(l => l.StartsWith("mapped\tweapon\t2000003\tLothric Knight Sword\t80010000", StringComparison.Ordinal))
            && report.Any(l => l.StartsWith($"drop\t1000\t3\tarmour\t51000000\tLothric Knight Helm\t1\t5\tenemy\t{FirstLot}\t\tNOT PORTED", StringComparison.Ordinal))
            && port.Notes.Count == 0 && erEnemy.Rows.Select(r => r.ID).SequenceEqual(erEnemy.Rows.Select(r => r.ID).Order()));
        var full = new DropPort(ds3Lots, TestParam([.. erFields]), TestParam([.. erFields]), items, null);
        var (noFlagEnemy, noFlagMap) = full.Port(knight);
        check("a one-time drop with no flag to keep it one-time is left out rather than dropped on every kill", noFlagEnemy == FirstLot && noFlagMap == -1 && full.FlagsLost == 1);
        var saved = Environment.GetEnvironmentVariable("DARKSIGN_ENEMY_DROPS");
        Environment.SetEnvironmentVariable("DARKSIGN_ENEMY_DROPS", "template");
        bool off = !Enabled;
        Environment.SetEnvironmentVariable("DARKSIGN_ENEMY_DROPS", saved);
        check("Fallbacks enemy-drops B (DARKSIGN_ENEMY_DROPS=template) keeps the template's drops", off && (saved is not null || Enabled));
    }

    private static string Clean(string? text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace("\r", "");

    // DS3's Paramdex field names differ from Elden Ring's only by case in places, so fields are found without it.
    private static PARAM.Cell? Cell(PARAM.Row row, string name) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int Int(PARAM.Row row, string name) =>
        Cell(row, name)?.Value is { } v and not byte[] ? (int)Math.Clamp(Convert.ToInt64(v), int.MinValue, int.MaxValue) : 0;

    private static void Set(PARAM.Row row, string name, long value)
    {
        if (Cell(row, name) is not { } cell || cell.Value is null or byte[])
            return;
        try { cell.Value = Convert.ChangeType(value, cell.Value.GetType()); }
        catch (Exception e) when (e is OverflowException or InvalidCastException) { }
    }
}
