using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// DS3's people in their DS3 armour. DS3's NPCs and hostile humans have no body of their own: they are the human
/// body c0000 dressed by a CharaInitParam row (Hawkwood: Chain Helm and the Undead Legion set). Up to 1.0.366 they
/// were left out, or (fallback map-people B) replaced by an Elden Ring person in Elden Ring clothes.
///
/// For every DS3 person the ported maps place:
/// <list type="number">
/// <item>Each armour piece's model (parts/hd|bd|am|lg_m|f_NNNN, and the _l low-detail copy) is converted to Elden
/// Ring's FLVER on plain character materials (<see cref="Enemies.ChrModel"/>), with its textures, under a new model
/// number from <see cref="FirstModel"/>. Both the male and female file are written (the DS3 one that exists stands
/// in for the other), so the body the person gets doesn't decide whether the piece shows.</item>
/// <item>The piece gets an EquipParamProtector row, <see cref="ProtectorBase"/> + its DS3 ID: a copy of the most
/// common Elden Ring row of its slot (so it hides the body under it the usual way) naming the new model.</item>
/// <item>The person gets a CharaInitParam row, <see cref="CharaInitBase"/> + the DS3 row: a copy of the Elden Ring
/// person who stands in for them (their face, AI, NpcParam and weapons) wearing the DS3 pieces, with DS3's sex.</item>
/// </list>
/// The rows are added after port-weapons, so a failure there puts the placed people back on their stand-in's own
/// look (<see cref="Revert"/>) instead of naming rows that don't exist. A piece whose model can't be converted is
/// left off (the row names no armour there). DARKSIGN_PEOPLE_ARMOUR=0 (fallback map-people D) leaves DS3's
/// humans out again. Unproven in game (2026-11-12): that a converted DS3 armour model binds to Elden Ring's body.
/// </summary>
public sealed class PeopleArmour
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DARKSIGN_PEOPLE_ARMOUR") != "0";

    /// <summary>Elden Ring's CharaInitParam rows end at 2,999,999.</summary>
    public const int CharaInitBase = 3_000_000;
    /// <summary>Elden Ring's EquipParamProtector rows end at 9,999,999; DS3's are 8 digits.</summary>
    public const int ProtectorBase = 100_000_000;
    /// <summary>Elden Ring's armour models end at 3230 (and one 9000).</summary>
    public const int FirstModel = 5000;
    /// <summary>The Elden Ring armour set whose binders give the layout and FLVER header (DS3 has no 1000 set; 1010 is the first full one).</summary>
    public const int TemplateModel = 1010;

    /// <summary>DS3 / Elden Ring equipModelCategory -> file prefix and CharaInit field (DS3 name, Elden Ring name).</summary>
    public static readonly (int Category, string Prefix, string Ds3Field, string ErField)[] Slots =
    [
        (5, "hd", "equip_Helm", "equip_Helm"),
        (2, "bd", "equip_Armor", "equip_Armer"),
        (1, "am", "equip_Gaunt", "equip_Gaunt"),
        (6, "lg", "equip_Leg", "equip_Leg"),
    ];

    public sealed record Piece(int Ds3Protector, int Category, int Ds3Model, string Name);
    public sealed record Look(int Ds3CharaInit, string Name, int Sex, IReadOnlyList<Piece?> Pieces);

    private readonly Dictionary<int, Look> _looks = [];
    /// <summary>DS3 armour model -> its new number (one number for every slot of a DS3 set, so shared textures keep matching).</summary>
    private readonly Dictionary<int, int> _models = [];
    /// <summary>DS3 protector -> converted (its model was written).</summary>
    private readonly HashSet<int> _converted = [];
    /// <summary>Our CharaInit row -> the stand-in's own row it copies, filled as people are placed.</summary>
    public Dictionary<int, int> StandIns { get; } = [];
    public List<string> Notes { get; } = [];

    public IReadOnlyDictionary<int, Look> Looks => _looks;
    public IReadOnlyDictionary<int, int> Models => _models;

    /// <summary>The CharaInit row a DS3 person placed with this DS3 row wears, or null when it has no look.</summary>
    public int? LookFor(int ds3CharaInit) => _looks.ContainsKey(ds3CharaInit) ? CharaInitBase + ds3CharaInit : null;

    public static int Cell(PARAM.Row row, string name) =>
        row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value is { } v and not byte[]
            ? System.Convert.ToInt32(v) : -1;

    public static bool SetCell(PARAM.Row row, string name, int value)
    {
        var cell = row.Cells.FirstOrDefault(c => c.Def.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (cell?.Value is null or byte[])
            return false;
        cell.Value = System.Convert.ChangeType(value, cell.Value.GetType());
        return true;
    }

    /// <summary>
    /// Plans the looks of the DS3 CharaInit rows <paramref name="charaInits"/> (the c0000 people of the ported maps).
    /// <paramref name="takenModel"/> says whether Elden Ring already ships an armour model number.
    /// </summary>
    public static PeopleArmour Plan(PARAM ds3CharaInit, PARAM ds3Protector, IEnumerable<int> charaInits, Func<int, bool> takenModel)
    {
        var plan = new PeopleArmour();
        int next = FirstModel;
        foreach (var id in charaInits.Where(i => i > 0).Distinct().Order())
        {
            if (ds3CharaInit[id] is not { } row)
            {
                plan.Notes.Add($"DS3 CharaInit {id}: no row");
                continue;
            }
            var pieces = new List<Piece?>();
            foreach (var slot in Slots)
            {
                var protector = Cell(row, slot.Ds3Field);
                if (protector <= 0 || ds3Protector[protector] is not { } p)
                {
                    pieces.Add(null);
                    continue;
                }
                int model = Cell(p, "equipModelID"), category = Cell(p, "equipModelCategory");
                if (model <= 0 || Slots.All(s => s.Category != category))
                {
                    pieces.Add(null);
                    continue;
                }
                if (!plan._models.ContainsKey(model))
                {
                    while (takenModel(next))
                        next++;
                    plan._models[model] = next++;
                }
                pieces.Add(new Piece(protector, category, model, p.Name ?? ""));
            }
            plan._looks[id] = new Look(id, row.Name ?? "", Cell(row, "NpcPlayerSex"), pieces);
        }
        return plan;
    }

    /// <summary>A plan with only model numbers (checks and diagnostics).</summary>
    public static PeopleArmour WithModels(params (int Ds3, int Mine)[] models)
    {
        var plan = new PeopleArmour();
        foreach (var (ds3, mine) in models)
            plan._models[ds3] = mine;
        return plan;
    }

    /// <summary>Adds a planned look (checks).</summary>
    public void AddLook(Look look) => _looks[look.Ds3CharaInit] = look;

    public static string Prefix(int category) => Slots.First(s => s.Category == category).Prefix;

    public static string PartsPath(string prefix, char gender, int model, bool lod) =>
        $"/parts/{prefix}_{gender}_{model:D4}{(lod ? "_l" : "")}.partsbnd.dcx";

    /// <summary>"BD_M_5800_a" -> "BD_M_5000_a" for every slot and gender of a DS3 set that has a new number.</summary>
    public string Rename(string name) =>
        // One pass, so a new number that is also some DS3 set's number isn't renamed a second time.
        SetName.Replace(name, m => int.TryParse(m.Groups[3].Value, out var ds3) && _models.TryGetValue(ds3, out var mine)
            ? $"{m.Groups[1].Value.ToUpperInvariant()}_{m.Groups[2].Value.ToUpperInvariant()}_{mine:D4}" : m.Value);

    private static readonly System.Text.RegularExpressions.Regex SetName =
        new(@"(hd|bd|am|lg)_([mfa])_(\d{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// One converted parts binder: the DS3 model and textures in the Elden Ring template binder's layout (TPF 100,
    /// FLVER 200). DS3's cloth physics (Havok 2014) is left out, as for weapons.
    /// </summary>
    public BND4 ConvertBinder(BND4 source, BND4 template, string prefix, char gender, int ds3Model, int newModel, bool lod, List<Enemies.ChrMaterial> materials)
    {
        var templateFlverFile = template.Files.FirstOrDefault(f => FLVER2.Is(f.Bytes)) ?? throw new InvalidDataException("the template binder has no model");
        var templateFlver = FLVER2.Read(templateFlverFile.Bytes);
        var templateTag = $"{prefix}_M_{TemplateModel:D4}".ToUpperInvariant();
        var newTag = $"{prefix.ToUpperInvariant()}_{char.ToUpperInvariant(gender)}_{newModel:D4}";
        var leafStart = templateFlverFile.Name.Replace('/', '\\').LastIndexOf('\\') + 1;
        var folder = templateFlverFile.Name[..leafStart].Replace(templateTag, newTag, StringComparison.OrdinalIgnoreCase);
        var tpfFlags = template.Files.FirstOrDefault(f => TPF.Is(f.Bytes))?.Flags ?? templateFlverFile.Flags;
        var files = new List<BinderFile>();
        foreach (var file in source.Files)
        {
            var leaf = Rename(ItemText.Leaf(file.Name));
            if (FLVER2.Is(file.Bytes))
            {
                var flver = FLVER2.Read(file.Bytes);
                // Material names and texture folders: the low-detail copy names its own materials (its textures are the _l ones).
                materials.AddRange(Enemies.ChrModel.Convert(flver, templateFlver, newTag + (lod ? "_L" : ""), Rename,
                    $@"N:\GR\data\Model\parts\FullBody\{newTag}\tex\", plainMaterials: true, erNodes: true));
                files.Add(new BinderFile(templateFlverFile.Flags, 200, folder + leaf, flver.Write()));
            }
            else if (TPF.Is(file.Bytes))
                files.Add(new BinderFile(tpfFlags, 100, folder + leaf, Enemies.ChrModel.ConvertTpf(TPF.Read(file.Bytes), Rename, flatMetallic: true).Write()));
        }
        if (!files.Any(f => f.ID == 200))
            throw new InvalidDataException("the DS3 binder has no model");
        template.Files = files.OrderBy(f => f.ID).ToList();
        return template;
    }

    /// <summary>Converts and writes every planned model (male and female, high and low detail); returns the report line.</summary>
    public string WriteModels(PorterContext ctx, GameArchives ds3, GameArchives er, List<Enemies.ChrMaterial> materials)
    {
        var pieces = _looks.Values.SelectMany(l => l.Pieces).OfType<Piece>().DistinctBy(p => p.Ds3Protector).ToList();
        int written = 0, failed = 0;
        foreach (var group in pieces.GroupBy(p => (p.Category, p.Ds3Model)))
        {
            var (category, ds3Model) = group.Key;
            var prefix = Prefix(category);
            var newModel = _models[ds3Model];
            try
            {
                foreach (var lod in new[] { false, true })
                {
                    var templatePath = PartsPath(prefix, 'm', TemplateModel, lod);
                    var (templateBytes, compression) = er.ReadWithCompression(templatePath);
                    // DS3's own male and female files; the one that exists stands in for the other.
                    var sources = "mf".ToDictionary(g => g, g => ds3.Contains(PartsPath(prefix, g, ds3Model, lod)) ? PartsPath(prefix, g, ds3Model, lod) : null);
                    if (sources.Values.All(s => s is null))
                        throw new FileNotFoundException($"DS3 has no {PartsPath(prefix, 'm', ds3Model, lod)} (or _f_)");
                    foreach (var g in "mf")
                    {
                        var source = sources[g] ?? sources[g == 'm' ? 'f' : 'm']!;
                        var bnd = ConvertBinder(BND4.Read(ds3.Read(source)), BND4.Read(templateBytes), prefix, g, ds3Model, newModel, lod, materials);
                        File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts(PartsPath(prefix, g, newModel, lod))), DCX.Compress(bnd.Write(), compression));
                        written++;
                    }
                }
                foreach (var p in group)
                    _converted.Add(p.Ds3Protector);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failed++;
                Notes.Add($"armour {prefix}_{ds3Model:D4} ({string.Join(", ", group.Select(p => p.Name).Distinct())}): not converted ({e.GetType().Name}: {e.Message}); people wearing it go without that piece");
            }
        }
        return $"people armour: {written} armour binders written for {pieces.Count} DS3 armour pieces ({_converted.Count} converted, {failed} DS3 models failed)";
    }

    /// <summary>
    /// The row a slot's new pieces copy: the most common invisibility pattern among Elden Ring's rows of that slot
    /// (the parts of the body it hides), lowest ID first.
    /// </summary>
    public static PARAM.Row? ProtectorTemplate(PARAM erProtector, int category) =>
        erProtector.Rows.Where(r => Cell(r, "equipModelCategory") == category && Cell(r, "equipModelId") > 0)
            .GroupBy(r => string.Concat(r.Cells.Where(c => c.Def.InternalName.StartsWith("invisibleFlag", StringComparison.Ordinal)).Select(c => System.Convert.ToString(c.Value))))
            .OrderByDescending(g => g.Count()).FirstOrDefault()?.OrderBy(r => r.ID).First();

    /// <summary>Adds the protector and CharaInit rows to <paramref name="regulation"/>; returns the report line.</summary>
    public string WriteParams(ParamStore regulation)
    {
        var protector = regulation.Get("EquipParamProtector") ?? throw new InvalidDataException("no EquipParamProtector");
        var charaInit = regulation.Get("CharaInitParam") ?? throw new InvalidDataException("no CharaInitParam");
        int protectors = 0, looks = 0, noStandIn = 0;
        foreach (var piece in _looks.Values.SelectMany(l => l.Pieces).OfType<Piece>().DistinctBy(p => p.Ds3Protector).Where(p => _converted.Contains(p.Ds3Protector)))
        {
            var id = ProtectorBase + piece.Ds3Protector;
            if (protector[id] is not null || ProtectorTemplate(protector, piece.Category) is not { } template)
                continue;
            var row = CopyRow(protector, template, id, piece.Name);
            SetCell(row, "equipModelId", _models[piece.Ds3Model]);
            protector.Rows.Add(row);
            protectors++;
        }
        foreach (var (ours, standIn) in StandIns)
        {
            var look = _looks[ours - CharaInitBase];
            if (charaInit[ours] is not null)
                continue;
            if (charaInit[standIn] is not { } template)
            {
                noStandIn++;
                continue;
            }
            var row = CopyRow(charaInit, template, ours, look.Name);
            for (int i = 0; i < Slots.Length; i++)
                SetCell(row, Slots[i].ErField, look.Pieces[i] is { } p && _converted.Contains(p.Ds3Protector) ? ProtectorBase + p.Ds3Protector : -1);
            if (look.Sex >= 0)
                SetCell(row, "npcPlayerSex", look.Sex);
            charaInit.Rows.Add(row);
            looks++;
        }
        protector.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        charaInit.Rows.Sort((a, b) => a.ID.CompareTo(b.ID));
        return $"people armour: {protectors} EquipParamProtector rows, {looks} CharaInitParam rows (DS3 people in their armour)"
               + (noStandIn > 0 ? $"; {noStandIn} stand-in rows missing" : "");
    }

    private static PARAM.Row CopyRow(PARAM param, PARAM.Row from, int id, string name)
    {
        var row = new PARAM.Row(id, name, param.AppliedParamdef);
        for (int i = 0; i < row.Cells.Count; i++)
            row.Cells[i].Value = from.Cells[i].Value is byte[] b ? b.Clone() : from.Cells[i].Value;
        return row;
    }

    /// <summary>Puts every placed person back on their stand-in's own look (the rows couldn't be written).</summary>
    public int Revert(PorterContext ctx, IEnumerable<MapSlots.Slot> slots)
    {
        int reverted = 0;
        foreach (var slot in slots)
        {
            var path = ctx.ModPath(MapPorter.ModParts(MapPorter.MsbPath(slot.Er)));
            if (!File.Exists(path))
                continue;
            var msb = MSBE.Read(DCX.Decompress(File.ReadAllBytes(path), out var compression));
            int here = 0;
            foreach (var e in msb.Parts.Enemies.Where(e => StandIns.ContainsKey(e.CharaInitID)))
            {
                e.CharaInitID = StandIns[e.CharaInitID];
                here++;
            }
            if (here > 0)
                File.WriteAllBytes(path, DCX.Compress(msb.Write(), compression));
            reverted += here;
        }
        return reverted;
    }

    /// <summary>The c0000 people's DS3 CharaInit rows in these DS3 maps (phantoms are filtered when placing).</summary>
    public static IEnumerable<int> PeopleIn(IEnumerable<MSB3> maps) =>
        maps.SelectMany(m => m.Parts.Enemies).Where(e => EnemyPort.People.IsHuman(e.ModelName) && e.CharaInitID > 0).Select(e => e.CharaInitID);
}
