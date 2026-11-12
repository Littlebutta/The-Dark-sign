using DarkSign.Porter.Maps;
using SoulsFormats;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// The enemy effect, sound and ability port (<see cref="ChrEffects"/>) on small files and params built in memory (no
/// game files), run as part of enemy-selftest: a character's effect binder (layout, IDs, textures, resource lists),
/// its moves' events (effects, SpEffects, projectiles, sounds; the template's dropped), enemy SpEffect rows and the
/// NpcParam row.
/// </summary>
public static class ChrEffectsSelfTest
{
    private static PARAM Param(params (string Name, PARAMDEF.DefType Type)[] fields)
    {
        var def = new PARAMDEF();
        foreach (var (name, type) in fields)
            def.Fields.Add(new PARAMDEF.Field(def, type, name));
        var param = new PARAM { Rows = [] };
        param.ApplyParamdef(def);
        return param;
    }

    private static PARAM.Row Add(PARAM param, int id, string name, params (string Field, object Value)[] values)
    {
        var row = new PARAM.Row(id, name, param.AppliedParamdef);
        foreach (var (field, value) in values)
            row[field].Value = Convert.ChangeType(value, row[field].Value.GetType());
        param.Rows.Add(row);
        return row;
    }

    private static byte[] Ints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    public static int Run()
    {
        try { return Checks(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine($"FAIL the effect checks stopped: {e}");
            return 1;
        }
    }

    private static int Checks()
    {
        int failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }
        const PARAMDEF.DefType S32 = PARAMDEF.DefType.s32, F32 = PARAMDEF.DefType.f32, U8 = PARAMDEF.DefType.u8;

        // A character effect binder as Elden Ring lays one out.
        var sampleBinder = new BND4();
        sampleBinder.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 0, @"N:\GR\data\INTERROOT_win64\sfx\effect\f000450100.fxr", [1]));
        sampleBinder.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 100000, @"N:\GR\data\INTERROOT_win64\sfx\tex\s04500.tpf", [2]));
        sampleBinder.Files.Add(new BinderFile(Binder.FileFlags.Flag1, 400000, @"N:\GR\data\INTERROOT_win64\sfx\ResourceList\f000450100.ffxreslist", [3]));
        var layout = new FxrPort.ChrBinderLayout([(sampleBinder, new DCX.NoCompressionInfo())]);
        var (binder, _) = layout.Create();
        Check("effect binder: a new one is empty and Elden Ring's kinds are known", layout.CanCreate && binder.Files.Count == 0 && layout.Known(".fxr") && layout.Known(".flver"));
        layout.Add(binder, ".ffxreslist", "f800000001.ffxreslist", [9]);
        layout.Add(binder, ".fxr", "f800000001.fxr", [9]);
        layout.Add(binder, ".flver", "s950000.flver", [9]);
        layout.Add(binder, ".fxr", "f800000002.fxr", [9]);
        layout.Add(binder, ".fxr", "f800000001.fxr", [7]);
        Check("effect binder: files sit by kind in ID order (effects from 0, models 200000, lists 400000), a kind Elden Ring's samples lack takes its usual folder, a file of the same name is replaced",
            binder.Files.Select(f => f.ID).SequenceEqual([0, 1, 200000, 400000]) && binder.Files[0].Bytes[0] == 7
            && binder.Files[2].Name == @"N:\GR\data\INTERROOT_win64\sfx\model\s950000.flver" && binder.Files[1].Name.EndsWith(@"\sfx\effect\f800000002.fxr"));

        // DS3 effects: 500 plays 501; 502 uses a texture DS3 doesn't ship; 503 plays 502.
        var js = new FxrPort.FxrScript();
        var ds3Fx = new FxrPort.Ds3Effects();
        void Fxr(int id, int child, int texture) =>
            ds3Fx.Fxr[id] = new FxrPort.Ds3File(new BinderFile(Binder.FileFlags.Flag1, id, $@"N:\FDP\data\Sfx\effect\f{id:D9}.fxr", js.Sample(id, child, texture)), "test");
        Fxr(500, 501, 30);
        Fxr(501, 0, 31);
        Fxr(502, 0, 99);
        Fxr(503, 502, 30);
        foreach (var t in new[] { 20, 30, 31 })
            ds3Fx.Resources[(FxrPort.Kind.Texture, t)] = new FxrPort.Ds3File(new BinderFile(Binder.FileFlags.Flag1, t, $@"N:\FDP\data\Sfx\Tex\s{t:D5}.tpf", [(byte)t]), "test");
        var converter = new FxrPort.ChrConverter(ds3Fx, () => []);
        (binder, _) = layout.Create();
        var added = converter.AddTo(binder, [500, 502, 503], layout);
        Check("enemy effects: an effect comes with the effect it plays, under 800,000,000 + the DS3 ID",
            added.Effects.Count == 2 && added.Effects[500] == 800_000_500 && added.Effects[501] == 800_000_501);
        Check($"enemy effects: one that needs a missing texture is left out with the reason, and so is one that plays it ({added.Reasons.GetValueOrDefault(503)})",
            added.Reasons.GetValueOrDefault(502, "").Contains("s99") && added.Reasons.GetValueOrDefault(503, "").Contains("f502"));
        var names = binder.Files.Select(f => f.Name[(f.Name.LastIndexOf('\\') + 1)..]).ToList();
        Check($"enemy effects: the binder holds the effects, their textures by DS3 rank (s30 -> 950001, s31 -> 950002) and a resource list each ({string.Join(" ", names)})",
            names.SequenceEqual(["f800000500.fxr", "f800000501.fxr", "s950001.tpf", "s950002.tpf", "f800000500.ffxreslist", "f800000501.ffxreslist"])
            && binder.Files.Select(f => f.ID).SequenceEqual([0, 1, 100000, 100001, 400000, 400001]));
        var convertedFxr = binder.Files[0].Bytes;
        bool Names(int id) => convertedFxr.AsSpan().IndexOf(BitConverter.GetBytes(id)) >= 0;
        Check("enemy effects: the converted effect is Elden Ring's format (version 5) and names its child and texture (a number property) by their new IDs",
            BitConverter.ToInt16(convertedFxr, 6) == 5 && BitConverter.ToInt16(ds3Fx.Fxr[500].File.Bytes, 6) == 4 && Names(800_000_501)
            && convertedFxr.AsSpan().IndexOf(BitConverter.GetBytes(950_001f)) >= 0 && convertedFxr.AsSpan().IndexOf(BitConverter.GetBytes(30f)) < 0);
        var (second, _) = layout.Create();
        var again = converter.AddTo(second, [501], layout);
        Check("enemy effects: an effect is the same bytes in every character's binder",
            again.Effects.Count == 1 && second.Files[0].Bytes.AsSpan().SequenceEqual(binder.Files[1].Bytes));

        // Enemy SpEffects.
        static PARAMDEF.DefType TypeOf(string f) => f.EndsWith("Rate", StringComparison.Ordinal) || f == "effectEndurance" ? F32 : S32;
        var (ds3Fields, erFields) = SpEffectPort.TestFields;
        var ds3Sp = Param([.. ds3Fields.Distinct().Select(f => (f, TypeOf(f)))]);
        var erSp = Param([.. erFields.Distinct().Select(f => (f, TypeOf(f))), ("iconId", S32), ("erOnly", U8)]);
        for (int i = 0; i < 3; i++)
            Add(erSp, 10 + i, "", ("iconId", -1), ("erOnly", 4), ("maxHpRate", 1f), ("physicsDiffenceRate", 1f), ("replaceSpEffectId", -1));
        Add(ds3Sp, 11880, "Jailer - HP Reduction", ("maxHpRate", 0.9f), ("physDefRate", 0.5f), ("lifeReductionRate", 0.25f), ("effectEndurance", 10f), ("replaceSpEffectId", 11890),
            ("spCategory", 100), ("categoryPriority", 4));
        Add(ds3Sp, 11890, "Jailer - HP Reduction 2", ("changeHpPoint", 30), ("maxHpRate", 1f), ("physDefRate", 1f));
        Add(ds3Sp, 11900, "Jailer - Lantern VFX", ("EffectVfx0", 4000), ("maxHpRate", 1f), ("physDefRate", 1f));
        Add(ds3Sp, 7000, "NG+ scaling", ("maxHpRate", 2f), ("physDefRate", 1f));
        Add(ds3Sp, 5, "[HKS] marker", ("maxHpRate", 1f), ("physDefRate", 1f), ("effectEndurance", 1f));
        Add(ds3Sp, 6, "taken", ("changeHpPoint", 5), ("maxHpRate", 1f), ("physDefRate", 1f));
        Add(erSp, ChrEffects.SpEffectId(6), "someone else's", ("iconId", -1));
        var ds3Vfx = Param(("midstSfxId", S32), ("InitSfxId", S32), ("finishSfxId", S32), ("isInvisibleWeapon", U8));
        var erVfx = Param(("midstSfxId", S32), ("initSfxId", S32), ("finishSfxId", S32), ("isInvisibleWeapon", U8));
        Add(erVfx, 1, "", ("midstSfxId", -1), ("initSfxId", -1), ("finishSfxId", -1));
        Add(ds3Vfx, 4000, "", ("midstSfxId", 500), ("InitSfxId", -1), ("finishSfxId", -1));
        var weapons = Param(("spEffectBehaviorId0", S32), ("spEffectBehaviorId1", S32), ("spEffectBehaviorId2", S32));
        var sp = new SpEffectPort(erSp, ds3Sp, erVfx, ds3Vfx, weapons, ChrEffects.SpEffectId, full: true);
        int drain = sp.Port(11880);
        var drainRow = erSp[drain];
        Check("enemy SpEffects: the new ID is 700,000,000 + the DS3 ID, one row (no upgrade rows)", drain == 700_011_880 && erSp[drain + 1] is null && erSp.Rows.Count(r => r.ID >= 700_000_000 && r.ID <= 700_011_880) == 2);
        Check("enemy SpEffects: DS3's values, defence, life drain and category (one effect of a category shows: the Jailer's lights) included, over Elden Ring's usual values for its own fields",
            drainRow is not null && Convert.ToSingle(drainRow["maxHpRate"].Value) == 0.9f && Convert.ToSingle(drainRow["physicsDiffenceRate"].Value) == 0.5f
            && Convert.ToSingle(drainRow["lifeReductionRate"].Value) == 0.25f && ParamDefaults.Int(drainRow, "erOnly") == 4 && ParamDefaults.Int(drainRow, "iconId") == -1
            && ParamDefaults.Int(drainRow, "spCategory") == 100 && ParamDefaults.Int(drainRow, "categoryPriority") == 4);
        Check("enemy SpEffects: the effect it turns into is ported and named by its new ID; no chain stays none",
            ParamDefaults.Int(drainRow!, "replaceSpEffectId") == 700_011_890 && erSp[700_011_890] is { } next && ParamDefaults.Int(next, "changeHpPoint") == 30
            && ParamDefaults.Int(next, "replaceSpEffectId") == -1 && ParamDefaults.Int(drainRow!, "cycleOccurrenceSpEffectId") == -1);
        Check("enemy SpEffects: an effect whose new ID is taken, or that DS3 lacks, is not ported", sp.Port(6) == -1 && sp.Port(123456) == -1
            && ChrEffects.SpEffectId(0) == -1 && ChrEffects.SpEffectId(300_000_000) == -1);
        int lantern = sp.Port(11900);
        var lanternVfx = lantern > 0 ? ParamDefaults.Int(erSp[lantern], erFields.First(f => f.Contains("fx", StringComparison.OrdinalIgnoreCase))) : -1;
        var bound = new FxrPort.Result();
        bound.Effects[500] = 800_000_500;
        sp.BindVisuals(bound);
        Check("enemy SpEffects: an effect that only shows something is ported with its visual row, which names the converted effect",
            lantern == 700_011_900 && lanternVfx > 0 && erVfx[lanternVfx] is { } vfxRow && ParamDefaults.Int(vfxRow, "midstSfxId") == 800_000_500);
        Check("NpcParam effects: ones that show, chain or change health are wanted; plain stat scaling is the template's business",
            ChrEffects.NpcEffectWanted(ds3Sp[11900]) && ChrEffects.NpcEffectWanted(ds3Sp[11880]) && ChrEffects.NpcEffectWanted(ds3Sp[11890]) && !ChrEffects.NpcEffectWanted(ds3Sp[7000]));

        // One character's moves.
        var erEvents = new List<TAE.Event> { new(0, 1, 2, 0, new byte[32], false), new(0, 1, 96, 0, new byte[16], false) };
        var events = EffectEvents.ForTest(new() { [96] = [0] }, new() { [96] = Ints(0, 0, -1, 7) });
        var planned = new SpEffectPort(Param([.. erFields.Distinct().Select(f => (f, TypeOf(f)))]), ds3Sp, null, null, weapons, ChrEffects.SpEffectId, full: true);
        var swing = new byte[32];
        var ctx = new PorterContext(Path.Combine(Path.GetTempPath(), "darksign-selftest-" + Guid.NewGuid().ToString("N")));
        var fx = ChrEffects.ForTest(ctx, null, ds3Sp, events, new FxrPort.ChrConverter(ds3Fx, () => []), layout, planned, swing, erEvents);
        var template = new EnemyPort.ErEnemy("c4300", 43000000, 43000000, "Knight", false, "test");
        var plan = new ChrPort.Plan(new ChrPort.Ds3Chr(1280, "Jailer", false, [128000]), template, 4300, 1, ChrPort.Method.Full, 7005);
        var ds3Events = new List<TAE.Event>
        {
            new(0.1f, 0.2f, 96, 0, Ints(500, 220, 0), false),      // an effect that converts (12 bytes; Elden Ring's has 16)
            new(0.2f, 0.3f, 96, 0, Ints(502, 220, 0), false),      // an effect that doesn't
            new(0.3f, 0.4f, 67, 0, Ints(11880, 0), false),         // casts a SpEffect
            new(0.4f, 0.5f, 67, 0, Ints(5, 0), false),             // casts a script marker
            new(0.5f, 0.6f, 2, 0, Convert.FromHexString("88270000000000006A00000000000000FFFF0000000000000000000000000000"), false), // fires a projectile
            new(0.6f, 0.7f, 128, 0, Ints(1, 128000100, 0, 0), false), // its own sound (no DS3 bank in this test)
        };
        var binders = new Dictionary<string, (BND4 Bnd, DCX.CompressionInfo Compression)>();
        var notes = new List<string>();
        var session = fx.Open(plan, binders, ds3Events, notes);
        Check($"enemy moves: the character without an effect binder gets one of its own with its moves' effects ({notes.LastOrDefault()})",
            binders.TryGetValue("/sfx/sfxbnd_c7005.ffxbnd.dcx", out var own) && own.Bnd.Files.Count(f => f.Name.EndsWith(".fxr")) == 2
            && session.Built is { HasBinder: true, BinderCreated: true, EffectsWanted: 2, EffectsPorted: 1 });
        var effect = session.Convert(ds3Events[0]);
        Check("enemy moves: a DS3 effect event names the converted effect, at Elden Ring's length with Elden Ring's usual tail",
            effect.Fate == ChrEffects.Session.Fate.Add && effect.Bytes!.AsSpan().SequenceEqual(Ints(800_000_500, 220, 0, 7)));
        Check("enemy moves: an effect event whose effect wasn't converted is dropped, never left with a DS3 number", session.Convert(ds3Events[1]).Fate == ChrEffects.Session.Fate.Drop);
        var cast = session.Convert(ds3Events[2]);
        Check("enemy moves: a SpEffect event casts the ported effect; a script marker is dropped",
            cast.Fate == ChrEffects.Session.Fate.Add && BitConverter.ToInt32(cast.Bytes!, 0) == 700_011_880 && session.Convert(ds3Events[3]).Fate == ChrEffects.Session.Fate.Drop);
        var shot = session.Convert(ds3Events[4]);
        Check("enemy moves: a projectile event keeps its dummy poly and behaviour judge in Elden Ring's layout",
            shot.Fate == ChrEffects.Session.Fate.Add && Convert.ToHexString(shot.Bytes!) == "88270000000000006A000000000000000000FF00000000000000000000000000");
        Check("enemy moves: without a DS3 sound bank the sound events are left to the template",
            !session.HasSounds && session.Convert(ds3Events[5]).Fate == ChrEffects.Session.Fate.Other && !session.DropsTemplate(new TAE.Event(0, 1, 128, 0, Ints(1, 430000100, 0, 0), false)));
        Check("enemy moves: the template's effect events go (they were timed for another animation), its other events stay",
            session.DropsTemplate(new TAE.Event(0, 1, 96, 0, new byte[16], false)) && !session.DropsTemplate(new TAE.Event(0, 1, 0, 0, new byte[16], false)));
        var anim = new TAE.Animation(3000, new TAE.Animation.AnimMiniHeader.Standard(), "a000_003000.hkx");
        var jump = new TAE.EventGroup(0);
        anim.EventGroups.Add(jump);
        anim.Events.Add(new TAE.Event(1f, 2f, 0, 0, new byte[16], false) { Group = jump });
        anim.Events.Add(new TAE.Event(0.5f, 0.6f, 96, 0, Ints(450100, 0, -1, 7), false) { Group = new TAE.EventGroup(96) });
        var merged = ChrTae.Merge(ds3Events, anim, new Dictionary<int, int> { [0] = 16 }, new Dictionary<int, TAE.EventGroup>(), 2f, 2f, session,
            new Dictionary<int, TAE.EventGroup> { [67] = new TAE.EventGroup(67), [2] = new TAE.EventGroup(2) });
        Check($"TAE merge with effects: the template's effect is replaced by DS3's, DS3's SpEffect and projectile are added with groups from other Elden Ring files ({string.Join(",", merged.Select(e => e.Type))})",
            merged.Select(e => e.Type).SequenceEqual([96, 67, 2, 0]) && BitConverter.ToInt32(merged[0].GetParameterBytes(false), 0) == 800_000_500 && merged.All(e => e.Group is not null));
        var sound = fx.SoundEvent(1, 900_128_000);
        Check("enemy sounds: a ported sound plays through Elden Ring's one-shot sound event, naming itself as the sound to play",
            sound is { Type: 129 } && BitConverter.ToInt32(sound.Value.Bytes, 4) == 900_128_000 && BitConverter.ToInt32(sound.Value.Bytes, 24) == 900_128_000
            && BitConverter.ToInt32(sound.Value.Bytes, 12) == -1);

        // The NpcParam row.
        var ds3Npc = Param([.. Enumerable.Range(0, 4).Select(k => ($"spEffectId{k}", S32))]);
        var erNpc = Param([.. Enumerable.Range(0, 4).Select(k => ($"spEffectID{k}", S32)), ("SfxResBankId", S32), ("SoundBankId", S32), ("SoundAddBankId", S32)]);
        var ds3Row = Add(ds3Npc, 128000, "Jailer", ("spEffectId0", 11900), ("spEffectId1", 7000), ("spEffectId2", 11880), ("spEffectId3", -1));
        var row = Add(erNpc, 70050000, "Jailer", ("spEffectID0", 1234), ("spEffectID1", -1), ("spEffectID2", -1), ("spEffectID3", -1), ("SfxResBankId", -1), ("SoundBankId", 4300), ("SoundAddBankId", -1));
        fx.UseForTest(sp);
        fx.ApplyNpc(row, ds3Row, plan);
        Check("NpcParam: DS3's own effects fill the template's free slots (the lantern glow, the drain), scaling is skipped, and the row names the character's effect binder",
            Enumerable.Range(0, 4).Select(k => ParamDefaults.Int(row, $"spEffectID{k}")).SequenceEqual([1234, 700_011_900, 700_011_880, -1])
            && ParamDefaults.Int(row, "SfxResBankId") == 7005 && session.Built is { NpcEffects: 2, NpcEffectsSkipped: 1 });
        Check("NpcParam: without a DS3 sound bank the template's bank stays", ParamDefaults.Int(row, "SoundBankId") == 4300 && ParamDefaults.Int(row, "SoundAddBankId") == -1);

        // DS3's map scripts: Pontiff's sword auras are set by m37's script on his entity; the Jailer's script takes its
        // blue lights off (the entity comes from the event's parameters), and puts the orange eye glow back on.
        var jailerEvent = new EMEVD.Event(13905220);
        jailerEvent.Instructions.Add(new EMEVD.Instruction(ScriptEffects.Bank, ScriptEffects.SetId, Ints(0, 11890)));
        jailerEvent.Instructions.Add(new EMEVD.Instruction(ScriptEffects.Bank, ScriptEffects.ClearId, Ints(0, 11900)));
        jailerEvent.Instructions.Add(new EMEVD.Instruction(ScriptEffects.Bank, ScriptEffects.ClearId, Ints(0, 11899)));
        jailerEvent.Parameters.AddRange([new EMEVD.Parameter(0, 0, 0, 4), new EMEVD.Parameter(1, 0, 0, 4), new EMEVD.Parameter(2, 0, 0, 4)]);
        var jailerEvent2 = new EMEVD.Event(13905225);
        jailerEvent2.Instructions.Add(new EMEVD.Instruction(ScriptEffects.Bank, ScriptEffects.ClearId, Ints(0, 11890)));
        jailerEvent2.Instructions.Add(new EMEVD.Instruction(ScriptEffects.Bank, ScriptEffects.SetId, Ints(0, 11899)));
        jailerEvent2.Parameters.AddRange([new EMEVD.Parameter(0, 0, 4, 4), new EMEVD.Parameter(1, 0, 4, 4)]);
        var starter = new EMEVD.Event(0);
        starter.Instructions.Add(new EMEVD.Instruction(2000, 0, Ints(0, 13905220, 3900800)));
        starter.Instructions.Add(new EMEVD.Instruction(2000, 0, Ints(1, 13905225, 0, 3900800)));
        starter.Instructions.Add(new EMEVD.Instruction(ScriptEffects.Bank, ScriptEffects.SetId, Ints(3700850, 10640)));
        starter.Instructions.Add(new EMEVD.Instruction(ScriptEffects.Bank, ScriptEffects.SetId, Ints(3700850, 10641)));
        starter.Instructions.Add(new EMEVD.Instruction(ScriptEffects.Bank, ScriptEffects.SetId, Ints(999, 12345)));
        var m39 = new EMEVD();
        m39.Events.AddRange([starter, jailerEvent, jailerEvent2]);
        var placed = new Dictionary<int, int> { [223000] = 3, [514000] = 1, [223010] = 1 };
        var scripts = ScriptEffects.From([m39], new Dictionary<int, HashSet<int>> { [3900800] = [223000], [3700850] = [514000] }, placed,
            new Dictionary<int, int> { [3900800] = 223000, [3700850] = 514000 });
        Check($"scripts: SpEffects set and cleared are found by the DS3 NpcParam row of the enemy they name, entities from the event's starting calls too ({scripts.Resolved} of {scripts.Instructions}, {scripts.Unresolved} not an enemy)",
            scripts.Instructions == 8 && scripts.Resolved == 7 && scripts.Unresolved == 1
            && scripts.Set(514000).SetEquals([10640, 10641]) && scripts.Set(223000).SetEquals([11890, 11899]) && scripts.Cleared(223000).SetEquals([11890, 11899, 11900]));
        var jailerFx = scripts.Apply(223000, [5330, 11900, 11899, 11902]);
        Check("scripts: a swap on one of three placed Jailers changes nothing on their shared row",
            jailerFx.Dropped.Count == 0 && jailerFx.Added.Count == 0 && jailerFx.Effects.SequenceEqual([5330, 11900, 11899, 11902]));
        var lone = ScriptEffects.From([m39], new Dictionary<int, HashSet<int>> { [3900800] = [223000] }, new Dictionary<int, int> { [223000] = 1 },
            new Dictionary<int, int> { [3900800] = 223000 }).Apply(223000, [5330, 11900, 11899, 11902]);
        Check("scripts: on a row whose only placed enemy the scripts switch a light off for good, the light is left out; one they put back (11899) stays",
            lone.Dropped.SequenceEqual([11900]) && lone.Added.Count == 0 && lone.Effects.SequenceEqual([5330, 11899, 11902]));
        var pontiffFx = scripts.Apply(514000, [5404, 10642]);
        Check("scripts: Pontiff's row gains the sword auras his script puts on him and never takes off",
            pontiffFx.Added.SequenceEqual([10640, 10641]) && pontiffFx.Dropped.Count == 0 && pontiffFx.Effects.SequenceEqual([5404, 10642, 10640, 10641]));
        // One effect of a category: the Jailer's three lights in category 100, orange 11899 has the highest priority.
        Add(ds3Sp, 11899, "Jailer - Weapon VFX", ("spCategory", 100), ("categoryPriority", 4));
        Add(ds3Sp, 11902, "Jailer - Eye VFX", ("spCategory", 100), ("categoryPriority", 2));
        Add(ds3Sp, 11903, "Jailer - Lantern light", ("spCategory", 100), ("categoryPriority", 1));
        var (kept, lost) = ChrEffects.OnePerCategory([11903, 11899, 11902, 7000, 11899, 424242], id => ds3Sp[id]);
        Check("category: of the Jailer's three lights in one category only the highest priority (orange 11899) stays; effects without a category stay",
            kept.SequenceEqual([11899, 7000, 424242]) && lost.SequenceEqual([11903, 11902]));
        var aura = Add(ds3Sp, 10640, "Pontiff - Heat Up", ("EffectVfx0", 4000), ("maxHpRate", 1f), ("physDefRate", 1f));
        var pontiffNpc = Add(ds3Npc, 514000, "Pontiff", ("spEffectId0", 7000), ("spEffectId1", -1), ("spEffectId2", -1), ("spEffectId3", -1));
        var pontiffRow = Add(erNpc, 71050000, "Pontiff", ("spEffectID0", 5400), ("spEffectID1", -1), ("spEffectID2", -1), ("spEffectID3", -1),
            ("SfxResBankId", -1), ("SoundBankId", 3560), ("SoundAddBankId", 3570));
        var pontiffPlan = plan with { Ds3 = new ChrPort.Ds3Chr(5140, "Pontiff Sulyvahn", true, [514000]), NewChr = 7105 };
        var pontiffFx2 = ChrEffects.ForTest(ctx, null, ds3Sp, events, new FxrPort.ChrConverter(ds3Fx, () => []), layout, planned, swing, erEvents, scripts);
        pontiffFx2.Open(pontiffPlan, [], [], []);
        pontiffFx2.UseForTest(sp);
        pontiffFx2.ApplyNpc(pontiffRow, pontiffNpc, pontiffPlan);
        Check("NpcParam: an effect DS3's script puts on the boss goes in a free slot of its ported row; the report lists it",
            ParamDefaults.Int(pontiffRow, "spEffectID1") == 700_010_640 && pontiffFx2.Characters[7105].ScriptAdded.SequenceEqual([10640, 10641]));

        // Glowing materials: DS3's emissive map on Elden Ring's glowing character material, with its colours white.
        var glowMatbin = new MATBIN { ShaderPath = @"N:\GR\data\Material\mtd\Chr\shader\C[AMSN]_Glow.spx", SourcePath = @"N:\GR\data\Material\mtd\Chr\matxml\C[c3570]_Body_Glow.matxml" };
        foreach (var type in new[] { "C_AMSN__Glow_snp_Texture2D_2_AlbedoMap_0", "C_AMSN__Glow_snp_Texture2D_0_MetallicMap_0", "C_AMSN__Glow_snp_Texture2D_7_NormalMap_4", "C_AMSN__Glow_snp_Texture2D_1_EmissiveMap" })
            glowMatbin.Samplers.Add(new MATBIN.Sampler { Type = type, Path = @"N:\GR\data\Model\chr\c3570\tex\c3570_x.tif" });
        glowMatbin.Params.Add(new MATBIN.Param { Name = "C_AMSN__Glow_snp_0_color_1", Type = MATBIN.ParamType.Float5, Value = new[] { 1f, 1f, 1f, 1f, 1f } });
        glowMatbin.Params.Add(new MATBIN.Param { Name = "C_AMSN__Glow_snp_0_float_1", Type = MATBIN.ParamType.Float, Value = 0.15f });
        glowMatbin.Params.Add(new MATBIN.Param { Name = "g_GlowScale", Type = MATBIN.ParamType.Float4, Value = new[] { 1f, 1f, 1f, 0.8f } });
        var tinted = new MATBIN { ShaderPath = glowMatbin.ShaderPath };
        tinted.Samplers.AddRange(glowMatbin.Samplers);
        tinted.Params.Add(new MATBIN.Param { Name = "C_AMSN__Glow_snp_0_color_1", Type = MATBIN.ParamType.Float5, Value = new[] { 0.41f, 0.59f, 0.67f, 1f, 3f } });
        var detailMatbin = new MATBIN { ShaderPath = "C[DetailBlend]_Glow.spx" };
        detailMatbin.Samplers.Add(new MATBIN.Sampler { Type = "Detail_BlendMask", Path = "" });
        detailMatbin.Samplers.AddRange(glowMatbin.Samplers);
        Check("glow: Elden Ring's plain glowing shader with white colours ranks first; a tinted one after it; a material with a slot the port can't fill (a blend mask) is no candidate",
            ChrGlow.Score(glowMatbin) == 6 && ChrGlow.Score(tinted) == 5 && ChrGlow.Score(detailMatbin) is null && ChrGlow.Score(new MATBIN()) is null);
        var glowTemplate = new ChrGlow.Template("C[c3570]_Body_Glow", "C[AMSN]_Glow", glowMatbin.Samplers.Select(x => x.Type).ToList(),
            new FLVER2.Material(), new FLVER2.GXList());
        var glowModel = new FLVER2();
        glowModel.Header.Version = 0x20014;
        glowModel.Nodes.Add(new FLVER.Node { Name = "Master" });
        glowModel.GXLists.Add(new FLVER2.GXList());
        glowModel.Materials.Add(new FLVER2.Material { Name = "#08#lantern", MTD = @"N:\FDP\data\Material\mtd\character\C[ARSN]_em_Glow.mtd", Textures =
        [
            new FLVER2.Texture("g_DiffuseTexture", @"N:\FDP\chr\c2230\tex\c2230_weapon_a.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
            new FLVER2.Texture("g_EmissiveTexture", @"N:\FDP\chr\c2230\tex\c2230_weapon_e.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
        ] });
        glowModel.Materials.Add(new FLVER2.Material { Name = "body", MTD = @"N:\FDP\data\Material\mtd\character\C[ARSN].mtd", Textures =
        [
            new FLVER2.Texture("g_DiffuseTexture", @"N:\FDP\chr\c2230\tex\c2230_body_a.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
            new FLVER2.Texture("g_EmissiveTexture", @"N:\FDP\data\Model\SYSTEX_DummyBurn_em.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
        ] });
        var glowTemplateModel = new FLVER2();
        glowTemplateModel.Header.Version = 0x2001A;
        glowTemplateModel.Nodes.Add(new FLVER.Node { Name = "Master" });
        glowTemplateModel.GXLists.Add(new FLVER2.GXList());
        glowTemplateModel.Materials.Add(new FLVER2.Material { Name = "body", MTD = @"N:\GR\data\Material\mtd\Chr\matxml\C[c3900]_Body.matxml" });
        var glowMesh = new FLVER2.Mesh { MaterialIndex = 0, NodeIndex = 0 };
        glowMesh.Vertices.Add(new FLVER.Vertex());
        glowMesh.FaceSets.Add(new FLVER2.FaceSet { Indices = [0, 0, 0] });
        glowTemplateModel.Meshes.Add(glowMesh);
        glowTemplateModel.BufferLayouts.Add(new FLVER2.BufferLayout { new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position, 0) });
        var glowMade = ChrModel.Convert(glowModel, glowTemplateModel, 2230, 7071, plainMaterials: true, erNodes: false, glow: glowTemplate);
        Check($"glow: a DS3 material with a glow map goes on the glowing material with its own GX list and samplers; a SYSTEX dummy glow map is no glow ({ChrModel.Glowing})",
            ChrModel.Glowing == 1 && glowMade.Count == 2 && glowMade[0] is { TemplateMatbin: "C[c3570]_Body_Glow", Emissive: "c7071_weapon_e" }
            && glowMade[1] is { TemplateMatbin: ChrMaterial.PlainSource, Emissive: null }
            && glowModel.Materials[0].GXIndex == 1 && glowModel.GXLists.Count == 2 && glowModel.Materials[0].Textures.Count == 4 && glowModel.Materials[1].GXIndex == 0);
        int named = glowMade[0].Apply(glowMatbin);
        Check("glow: the copy names DS3's colour and glow textures, clears the slots DS3 has nothing for, and its glow colour is white with Elden Ring's strength kept",
            named == 2 && glowMatbin.Samplers[3].Path.EndsWith(@"\c7071_weapon_e.tif") && glowMatbin.Samplers[0].Path.EndsWith(@"\c7071_weapon_a.tif") && glowMatbin.Samplers[1].Path == ""
            && ChrGlow.Whiten(tinted) == 1 && ((float[])tinted.Params[0].Value).SequenceEqual([1f, 1f, 1f, 1f, 3f])
            && ((float[])glowMatbin.Params[2].Value).SequenceEqual([1f, 1f, 1f, 0.8f]) && (float)glowMatbin.Params[1].Value == 0.15f);
        var noGlow = new FLVER2();
        noGlow.Header.Version = 0x20014;
        noGlow.Nodes.Add(new FLVER.Node { Name = "Master" });
        noGlow.GXLists.Add(new FLVER2.GXList());
        noGlow.Materials.Add(glowModel.Materials[0]);
        noGlow.Materials[0].MTD = @"N:\FDP\data\Material\mtd\character\C[ARSN]_em_Glow.mtd";
        noGlow.Materials[0].Textures =
        [
            new FLVER2.Texture("g_DiffuseTexture", @"N:\FDP\chr\c2230\tex\c2230_weapon_a.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
            new FLVER2.Texture("g_EmissiveTexture", @"N:\FDP\chr\c2230\tex\c2230_weapon_e.tif", System.Numerics.Vector2.One, 0, 0, 0, 0, 0),
        ];
        var plainMade = ChrModel.Convert(noGlow, glowTemplateModel, 2230, 7071, plainMaterials: true, erNodes: false, glow: null);
        Check("glow: with no glowing material (fallback DARKSIGN_ENEMY_GLOW=0, or none found) the material is plain as before",
            plainMade.Single() is { TemplateMatbin: ChrMaterial.PlainSource, Emissive: null } && noGlow.GXLists.Count == 1 && ChrModel.Glowing == 0);


        // DS3's encrypted sound files.
        var plain = new byte[0x40];
        "FSB5"u8.CopyTo(plain);
        BitConverter.GetBytes(3).CopyTo(plain, 8);
        plain[0x3F] = 0xA5;
        var key = "FDPrVuT4fAFvdHJYAgyMzRF4EcBAnKg"u8.ToArray();
        var encrypted = plain.Select((b, i) =>
        {
            int x = b ^ key[i % key.Length];
            x = ((x & 0xF0) >> 4) | ((x & 0x0F) << 4);
            x = ((x & 0xCC) >> 2) | ((x & 0x33) << 2);
            return (byte)(((x & 0xAA) >> 1) | ((x & 0x55) << 1));
        }).ToArray();
        Check("sound files: an encrypted DS3 FSB decrypts to the plain one, a plain one is not taken for encrypted",
            Fsb5.Decrypt(encrypted) is { } back && back.AsSpan().SequenceEqual(plain) && Fsb5.Decrypt(plain) is null);
        return failures;
    }
}
