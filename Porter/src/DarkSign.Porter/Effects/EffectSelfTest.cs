using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// effects-selftest: checks the visual-row and bullet ports on small params built in memory (no game files), so CI
/// catches a broken field mapping: IDs renumbered, effects bound only once converted, DS3 references that mean
/// nothing in Elden Ring cleared, bit fields regrouped, chains of bullets followed and ended.
/// </summary>
public static class EffectSelfTest
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

    private static PARAM.Row Add(PARAM param, int id, params (string Field, object Value)[] values)
    {
        var row = new PARAM.Row(id, "", param.AppliedParamdef);
        foreach (var (field, value) in values)
            row[field].Value = Convert.ChangeType(value, row[field].Value.GetType());
        param.Rows.Add(row);
        return row;
    }

    /// <summary>A one-triangle effect model: one mesh per material, with or without tangents.</summary>
    private static byte[] Model(int version, bool tangents, params (string Mtd, (string Slot, string Path)[] Textures)[] materials)
    {
        var flver = new FLVER2();
        flver.Header.Version = version;
        flver.Nodes.Add(new FLVER.Node { Name = "Master" });
        var layout = new FLVER2.BufferLayout
        {
            new FLVER.LayoutMember(FLVER.LayoutType.Float3, FLVER.LayoutSemantic.Position, 0),
            new FLVER.LayoutMember(FLVER.LayoutType.UByte4, FLVER.LayoutSemantic.Normal, 0),
        };
        if (tangents)
            layout.Add(new FLVER.LayoutMember(FLVER.LayoutType.UByte4, FLVER.LayoutSemantic.Tangent, 0));
        layout.Add(new FLVER.LayoutMember(FLVER.LayoutType.UByte4Norm, FLVER.LayoutSemantic.VertexColor, 1));
        layout.Add(new FLVER.LayoutMember(FLVER.LayoutType.Short2, FLVER.LayoutSemantic.UV, 0));
        flver.BufferLayouts.Add(layout);
        if (version >= 0x2001A)
        {
            flver.GXLists.Add([]);
            flver.Skeletons = new FLVER2.SkeletonSet { BaseSkeleton = [new FLVER2.SkeletonSet.Bone(0)], AllSkeletons = [new FLVER2.SkeletonSet.Bone(0)] };
        }
        foreach (var (mtd, textures) in materials)
        {
            var material = new FLVER2.Material("m", mtd, 0) { GXIndex = version >= 0x2001A ? 0 : -1 };
            foreach (var (slot, path) in textures)
                material.Textures.Add(new FLVER2.Texture(slot, path, System.Numerics.Vector2.One, default, default, 0, 0, 0));
            var mesh = new FLVER2.Mesh { MaterialIndex = flver.Materials.Count, NodeIndex = 0, BoundingBox = new FLVER2.Mesh.BoundingBoxes() };
            for (int i = 0; i < 3; i++)
            {
                var vertex = new FLVER.Vertex();
                vertex.Positions.Add(new System.Numerics.Vector3(i, 0, 0));
                vertex.Normals.Add(System.Numerics.Vector3.UnitY);
                vertex.NormalWs.Add(0);
                if (tangents)
                    vertex.Tangents.Add(new System.Numerics.Vector4(1, 0, 0, 1));
                vertex.Colors.Add(new FLVER.VertexColor(1f, 1f, 1f, 1f));
                vertex.UVs.Add(System.Numerics.Vector3.Zero);
                mesh.Vertices.Add(vertex);
            }
            mesh.FaceSets.Add(new FLVER2.FaceSet { Indices = [0, 1, 2] });
            mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
            flver.Materials.Add(material);
            flver.Meshes.Add(mesh);
        }
        return flver.Write();
    }

    public static int Run()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }
        const PARAMDEF.DefType S32 = PARAMDEF.DefType.s32, U8 = PARAMDEF.DefType.u8, S8 = PARAMDEF.DefType.s8, F32 = PARAMDEF.DefType.f32, S16 = PARAMDEF.DefType.s16;

        // Visual rows.
        var ds3Vfx = Param([("midstSfxId", S32), ("InitSfxId", S32), ("finishSfxId", S32), ("midstSeId", S32), ("midstDmyId", S16), ("effectType", U8),
            ("soulParamIdForWepEnchant1", U8), ("isInvisibleWeapon", U8), .. Enumerable.Range(0, 16).Select(k => ($"SpSfxId{k}", S32))]);
        var erVfx = Param([("midstSfxId", S32), ("initSfxId", S32), ("finishSfxId", S32), ("midstSeId", S32), ("midstDmyId", S16), ("effectType", U8),
            ("soulParamIdForWepEnchant", U8), ("isInvisibleWeapon", U8), ("isUseOffsetEnchantSfxSize", U8), ("decalId1", S32),
            .. Enumerable.Range(0, 8).SelectMany(k => new[] { ($"enchantStartDmyId_{k}", S32), ($"enchantEndDmyId_{k}", S32) })]);
        for (int i = 0; i < 3; i++)
            Add(erVfx, 10 + i, ("midstSfxId", -1), ("initSfxId", -1), ("finishSfxId", -1), ("midstSeId", -1), ("decalId1", -1), ("effectType", 1),
                ("isUseOffsetEnchantSfxSize", 1), ("enchantStartDmyId_0", 10300), ("enchantEndDmyId_0", -1), ("enchantStartDmyId_1", 11300));
        Add(ds3Vfx, 500, ("midstSfxId", 440151), ("InitSfxId", 440210), ("midstSeId", 777), ("midstDmyId", 220), ("effectType", 1), ("soulParamIdForWepEnchant1", 8));
        Add(ds3Vfx, 501, ("midstSfxId", 402000), ("effectType", 2), ("SpSfxId0", 206), ("SpSfxId1", 206));
        Add(ds3Vfx, 502, ("midstSeId", 5));
        var vfx = new VfxPort(erVfx, ds3Vfx);
        int a = vfx.Port(500), b = vfx.Port(501);
        Check(a >= VfxPort.FirstNewId && b == a + 1 && vfx.Port(500) == a, "visual rows: new IDs from the base, the same DS3 row ported once");
        Check(vfx.Port(502) == -1 && vfx.Port(999) == -1, "visual rows: a row that shows nothing, or that DS3 lacks, is not ported");
        var rowA = erVfx[a];
        Check(ParamDefaults.Int(rowA, "midstSfxId") == -1 && ParamDefaults.Int(rowA, "initSfxId") == -1, "visual rows: no DS3 effect number before the effects are converted");
        var converted = new FxrPort.Result();
        converted.Effects[440151] = FxrPort.IdBase + 440151;
        vfx.Bind(converted);
        Check(ParamDefaults.Int(rowA, "midstSfxId") == FxrPort.IdBase + 440151 && ParamDefaults.Int(rowA, "initSfxId") == -1,
            "visual rows: a converted effect gets its new ID, an unconverted one stays empty");
        Check(ParamDefaults.Int(rowA, "midstSeId") == -1 && ParamDefaults.Int(rowA, "soulParamIdForWepEnchant") == 0 && ParamDefaults.Int(rowA, "isUseOffsetEnchantSfxSize") == 0
              && ParamDefaults.Int(rowA, "midstDmyId") == 220, "visual rows: DS3 sound and tint references cleared, dummy poly kept, no size offset");
        Check(ParamDefaults.Int(rowA, "enchantStartDmyId_0") == 10300 && ParamDefaults.Int(rowA, "enchantStartDmyId_1") == 11300,
            "visual rows: an enchant without a span of its own takes Elden Ring's usual blade span");
        var rowB = erVfx[b];
        Check(ParamDefaults.Int(rowB, "enchantStartDmyId_0") == 206 && ParamDefaults.Int(rowB, "enchantEndDmyId_0") == 206 && ParamDefaults.Int(rowB, "enchantStartDmyId_1") == -1,
            "visual rows: DS3's own span is kept in order (start, end)");
        Check(vfx.Port(501) == b && ParamDefaults.Int(rowB, "midstSfxId") == -1, "visual rows: bound rows keep an unconverted effect empty");

        // Bullets.
        var ds3Bullets = Param(("atkBullet_Id", S32), ("sfxIdBullet", S32), ("sfxIdHit", S32), ("sfxIdFlick", S32), ("life", F32), ("hitBulletId", S32),
            ("spEffectId0", S32), ("shootAngleXZ", U8), ("isPenetrate", U8), ("FollowType0", U8), ("FollowType1", U8), ("FollowType2", U8),
            ("EmittePosType0", U8), ("EmittePosType1", U8), ("EmittePosType2", U8), ("IsAttackSfx", U8), ("bulletEmitter_bulletId", S32),
            ("bulletEmitter_ShootInterval0", F32), ("magicDamageDamp", U8), ("spEffectIdForShooter", S32));
        var erBullets = Param(("atkId_Bullet", S32), ("sfxId_Bullet", S32), ("sfxId_Hit", S32), ("sfxId_Flick", S32), ("life", F32), ("HitBulletID", S32),
            ("spEffectId0", S32), ("shootAngleXZ", S8), ("isPenetrateChr", U8), ("isPenetrateObj", U8), ("FollowType", U8), ("EmittePosType", U8),
            ("isAttackSFX", U8), ("intervalCreateBulletId", S32), ("intervalCreateTimeMin", F32), ("spelDamageDamp", S8), ("spEffectIDForShooter", S32),
            ("seId_Bullet1", S32), ("assetNo_Hit", S32), ("spBulletDistUpRate", F32));
        for (int i = 0; i < 3; i++)
            Add(erBullets, 1 + i, ("atkId_Bullet", 1), ("life", 3f), ("isPenetrateChr", 1), ("isPenetrateObj", 1), ("spBulletDistUpRate", 1f), ("seId_Bullet1", 123), ("assetNo_Hit", 9));
        Add(ds3Bullets, 100063200, ("atkBullet_Id", 2513106), ("sfxIdBullet", 440034), ("sfxIdHit", 440033), ("life", 0.3f), ("hitBulletId", 100063201),
            ("shootAngleXZ", 226), ("EmittePosType1", 1), ("FollowType0", 1), ("FollowType2", 1), ("IsAttackSfx", 1), ("magicDamageDamp", 50), ("spEffectId0", 6904));
        Add(ds3Bullets, 100063201, ("sfxIdHit", 440033), ("hitBulletId", 100063200), ("bulletEmitter_bulletId", 100063201), ("bulletEmitter_ShootInterval0", 0.06f), ("isPenetrate", 1));
        var attacks = new List<int>();
        var bullets = new BulletPort(erBullets, ds3Bullets, null, id => { attacks.Add(id); return id + 7; });
        int wave = bullets.Port(100063200);
        var w = erBullets[wave];
        int hit = ParamDefaults.Int(w, "HitBulletID");
        Check(wave >= BulletPort.FirstNewId && hit > wave && bullets.Count == 2, "bullets: a bullet and the bullet it turns into on hit are ported under new IDs");
        var h = erBullets[hit];
        Check(ParamDefaults.Int(h, "HitBulletID") == wave && ParamDefaults.Int(h, "intervalCreateBulletId") == hit, "bullets: bullets that name each other, or themselves, end");
        Check(ParamDefaults.Int(w, "atkId_Bullet") == 2513113 && attacks.SequenceEqual([2513106]) && ParamDefaults.Int(h, "atkId_Bullet") == -1, "bullets: the attack is ported, none stays none");
        Check(Convert.ToSingle(w["life"].Value) == 0.3f && Convert.ToSingle(w["spBulletDistUpRate"].Value) == 1f, "bullets: DS3's values over Elden Ring's usual values for Elden Ring-only fields");
        Check(ParamDefaults.Int(w, "shootAngleXZ") == -30 && ParamDefaults.Int(w, "spelDamageDamp") == 50, "bullets: unsigned DS3 bytes read as Elden Ring's signed ones (226 = -30), renamed fields copied");
        Check(ParamDefaults.Int(w, "FollowType") == 5 && ParamDefaults.Int(w, "EmittePosType") == 2 && ParamDefaults.Int(w, "isAttackSFX") == 1, "bullets: DS3's one-bit fields regrouped into Elden Ring's numbers");
        Check(ParamDefaults.Int(w, "isPenetrateChr") == 0 && ParamDefaults.Int(h, "isPenetrateChr") == 1 && ParamDefaults.Int(h, "isPenetrateObj") == 1, "bullets: DS3's penetrate byte sets both Elden Ring bits");
        Check(ParamDefaults.Int(w, "seId_Bullet1") == -1 && ParamDefaults.Int(w, "assetNo_Hit") == -1 && ParamDefaults.Int(w, "spEffectId0") == -1 && ParamDefaults.Int(w, "spEffectIDForShooter") == -1,
            "bullets: sounds, objects and unported SpEffects cleared, never DS3 numbers");
        Check(ParamDefaults.Int(w, "sfxId_Bullet") == -1 && ParamDefaults.Int(w, "sfxId_Hit") == -1, "bullets: no DS3 effect number before the effects are converted");
        converted.Effects[440034] = FxrPort.IdBase + 440034;
        bullets.Bind(converted);
        Check(ParamDefaults.Int(w, "sfxId_Bullet") == FxrPort.IdBase + 440034 && ParamDefaults.Int(w, "sfxId_Hit") == -1 && ParamDefaults.Int(w, "sfxId_Flick") == -1,
            "bullets: a converted effect gets its new ID, an unconverted one stays empty");
        Check(bullets.Port(5) == -1, "bullets: a bullet DS3 lacks is not ported");

        // Resident effects on the weapon model.
        var ds3Weapons = Param([.. Enumerable.Range(0, 8).Select(k => ($"weaponVfx{k}", S32))]);
        var friede = Add(ds3Weapons, 10180000, ("weaponVfx0", 303151), ("weaponVfx1", 303151), ("weaponVfx2", -1), ("weaponVfx4", 300), ("weaponVfx5", 301));
        var resident = Ds3EffectRefs.Resident(friede).ToList();
        Check(resident.SequenceEqual([(1, 303151, 300), (2, 303151, 301)]), "resident effects: DS3's weaponVfx0-3 are the effects, weaponVfx4-7 their dummy polys");
        var erWeapons = Param(("residentSfxId_1", S32), ("residentSfxId_2", S32), ("residentSfx_DmyId_1", S32), ("residentSfx_DmyId_2", S32));
        var ported = Add(erWeapons, 80000000, ("residentSfxId_1", -1), ("residentSfxId_2", -1), ("residentSfx_DmyId_1", -1), ("residentSfx_DmyId_2", -1));
        var plan = new WeaponPlan(10180000, "Friede's Great Scythe", 50, 1, "t", 80000000, 9000, "port");
        Check(MovesetPort.ResidentEffects([plan], ds3Weapons, erWeapons, converted) == 0 && ParamDefaults.Int(ported, "residentSfxId_1") == -1, "resident effects: an unconverted effect leaves the row as it was");
        converted.Effects[303151] = FxrPort.IdBase + 303151;
        Check(MovesetPort.ResidentEffects([plan], ds3Weapons, erWeapons, converted) == 1 && ParamDefaults.Int(ported, "residentSfxId_2") == FxrPort.IdBase + 303151
              && ParamDefaults.Int(ported, "residentSfx_DmyId_2") == 301, "resident effects: converted effects go on the same dummy polys of the ported weapon");

        // Effect models.
        var erModel = Model(0x2001A, true, (@"N:\GR\data\Material\mtd\sfx\matxml\S[A]_add_Soft.matxml", [("M_A__snp_Texture2D_0_AlbedoMap_0", @"N:\GR\data\Sfx\Tex\s00001_a.tif")]),
            (@"N:\GR\data\Material\mtd\sfx\matxml\S[ARSN]_em.matxml", [("S_ARSN__snp_Texture2D_2_AlbedoMap_0", ""), ("S_ARSN__snp_Texture2D_7_NormalMap_4", ""), ("S_ARSN__snp_Texture2D_1_EmissiveMap", "")]));
        var own = Model(0x2001A, true, (@"N:\GR\data\Material\mtd\sfx\matxml\s80010_S[AMSN]_em.matxml", [("x_AlbedoMap_0", "")]));
        var modelPort = new EffectModelPort([("s80201.flver", erModel), ("s80010.flver", own)]);
        Check(modelPort.TemplateCount == 2, "effect models: Elden Ring's general effect materials are the templates, not one model's own");
        Check(EffectModelPort.Candidates(@"N:\FDP\data\Material\mtd\sfx\S[ARSN]_glow_al _Soft.mtd").SequenceEqual(["s[arsn]_glow_al_soft", "s[amsn]_glow_al_soft", "s[arsn]_al", "s[amsn]_al"])
              && EffectModelPort.Candidates("S[A]_add.mtd").Contains("s[a]_add_soft") && EffectModelPort.Candidates("S[ARSN]_e.mtd").Contains("s[amsn]_em"),
            "effect models: a DS3 material takes Elden Ring's of the same name, then the nearest one");
        var frost = Model(0x20014, false, (@"N:\FDP\data\Material\mtd\sfx\S[A]_add_Soft.mtd", [("g_DiffuseTexture", @"N:\FDP\data\Sfx\Tex\s11655.tif")]),
            (@"N:\FDP\data\Material\mtd\sfx\S[ARSN]_em_Soft.mtd", [("g_DiffuseTexture", @"N:\FDP\data\Sfx\Tex\s08120_a.tif"), ("g_BumpmapTexture", @"N:\FDP\data\Sfx\Tex\s08120_n.tif"),
                ("g_EmissiveTexture", @"N:\FDP\data\Sfx\Tex\s00004.tif"), ("g_DisplacementTexture", @"N:\LiveTokyo\data\model\common\tex\dummy128.tga")]));
        Check(EffectModelPort.TextureIds(frost).Order().SequenceEqual([4, 8120, 11655]), "effect models: a model's effect textures are found from its materials (not shared dummies)");
        Check(modelPort.Unsupported(frost) is null && modelPort.Unsupported(Model(0x20014, false, (@"N:\FDP\data\Material\mtd\map\M[ARSN].mtd", [("g_DiffuseTexture", "")]))) is not null,
            "effect models: a model is convertible only when every material has an Elden Ring equivalent");
        var portedModel = FLVER2.Read(modelPort.Convert(frost, t => t == 4 ? null : 900000 + t));
        Check(portedModel.Header.Version == 0x2001A && portedModel.Skeletons?.AllSkeletons.Count == portedModel.Nodes.Count, "effect models: Elden Ring's FLVER version, with a skeleton set");
        Check(portedModel.Materials[0].MTD.EndsWith("S[A]_add_Soft.matxml") && portedModel.Materials[1].MTD.EndsWith("S[ARSN]_em.matxml"), "effect models: materials are Elden Ring's");
        Check(portedModel.Materials[0].Textures.Single().Path == @"N:\GR\data\Sfx\Tex\s911655.tif"
              && portedModel.Materials[1].Textures.Select(t => t.Path).SequenceEqual([@"N:\GR\data\Sfx\Tex\s908120_a.tif", @"N:\GR\data\Sfx\Tex\s908120_n.tif", ""]),
            "effect models: textures go into the Elden Ring slots of the same kind under their new names; an unported one stays the default");
        Check(portedModel.Meshes.All(m => m.Vertices.All(v => v.Tangents.Count == 1)) && portedModel.Meshes[0].Vertices[1].Position.X == 1f, "effect models: DS3's geometry, with the tangent Elden Ring's layout carries");

        Console.WriteLine(failures == 0 ? "effects-selftest: all checks passed" : $"effects-selftest: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
