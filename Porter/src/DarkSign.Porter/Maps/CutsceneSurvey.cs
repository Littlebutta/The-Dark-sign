using System.Text;
using HKLib.hk2018;
using SoulsAssetPipeline.Animation;
using SoulsFormats;

namespace DarkSign.Porter.Maps;

/// <summary>
/// `cutscene-survey [ds3 cutscene names...] [er:cutscene names...]`: what the cutscene port reads and writes, from the
/// games on this PC. For each DS3 remo binder (/remo/sAA_BB_NNNN.remobnd.dcx) and Elden Ring cutscene binder
/// (/cutscene/sAA_BB_NNNN.cutscenebnd.dcx) asked for: the binder layout (IDs, names, compression), the MQB timeline
/// (resources, cuts, timelines, events and their parameters) and each Havok file's skeleton and animation. Also the
/// cutscene lines of the scripts (DS3 m45_00 and Elden Ring m10_00) and Elden Ring's cutscene params. Read-only.
/// Without names it surveys DS3's Painted World cutscenes and Elden Ring's s10_00 ones.
/// </summary>
public static class CutsceneSurvey
{
    public static int Run(PorterContext ctx, IReadOnlyList<string> args)
    {
        using var ds3 = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var er = ctx.OpenArchives(PorterGame.EldenRing);
        var ds3Names = args.Where(a => !a.StartsWith("er:", StringComparison.OrdinalIgnoreCase)).ToList();
        var erNames = args.Where(a => a.StartsWith("er:", StringComparison.OrdinalIgnoreCase)).Select(a => a[3..]).ToList();
        if (ds3Names.Count == 0)
            ds3Names = ["s45_00_0000", "s45_00_0010", "s45_00_0030", "s40_00_0000"];
        if (erNames.Count == 0)
            erNames = ["s10_00_0050", "s10_00_0010", "s10_00_0030", "s14_00_0000"];

        var r = new StringBuilder();
        void Section(string title, Func<IEnumerable<string>> lines)
        {
            r.AppendLine($"--- {title}");
            try
            {
                foreach (var line in lines())
                    r.AppendLine("  " + line);
            }
            catch (Exception ex)
            {
                r.AppendLine($"  error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Section("DS3 remo binders", () => List(ds3, "/remo/"));
        Section("ER cutscene binders", () => List(er, "/cutscene/"));
        foreach (var name in ds3Names)
            Section($"DS3 {name}", () => Binder(ds3, CutscenePort.Ds3Path(name), isEr: false));
        foreach (var name in erNames)
            Section($"ER {name}", () => Binder(er, CutscenePort.ErPath(name), isEr: true));
        Section("DS3 m45_00 script cutscene lines", () => ScriptLines(ds3, "/event/m45_00_00_00.emevd.dcx"));
        Section("DS3 common_func cutscene lines", () => ScriptLines(ds3, "/event/common_func.emevd.dcx"));
        Section("ER m10_00 script cutscene lines", () => ScriptLines(er, "/event/m10_00_00_00.emevd.dcx"));
        Section("ER cutscene params", () => Params(ctx));

        var log = ctx.WorkPath("logs", $"cutscene-survey-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, r.ToString());
        Console.WriteLine(r.ToString());
        Console.WriteLine($"report written to {log}");
        return 0;
    }

    private static IEnumerable<string> List(GameArchives game, string folder)
    {
        var files = game.Entries.Where(e => e.Path is not null && e.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path!).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        yield return $"{files.Count} named files under {folder}";
        foreach (var chunk in files.Chunk(8))
            yield return string.Join(" ", chunk.Select(ItemText.Leaf));
    }

    private static IEnumerable<string> Binder(GameArchives game, string path, bool isEr)
    {
        if (!game.Contains(path))
        {
            yield return $"missing {path}";
            yield break;
        }
        var (bytes, compression) = game.ReadWithCompression(path);
        var bnd = BND4.Read(bytes);
        yield return $"{path}: compression {compression.Type}, BND4 version {bnd.Version}, format {bnd.Format}, unicode {bnd.Unicode}, "
                     + $"extended {bnd.Extended}, bigEndian {bnd.BigEndian}, unk04 {bnd.Unk04}, unk05 {bnd.Unk05}, {bnd.Files.Count} files";
        foreach (var f in bnd.Files)
        {
            var data = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
            var head = string.Concat(data.Take(16).Select(b => b is >= 32 and < 127 ? (char)b : '.'));
            yield return $"[{f.ID}] {f.Name} flags {f.Flags} {data.Length} bytes{(DCX.Is(f.Bytes) ? " (dcx)" : "")} \"{head}\"";
        }
        foreach (var f in bnd.Files)
        {
            var data = DCX.Is(f.Bytes) ? DCX.Decompress(f.Bytes) : f.Bytes;
            IEnumerable<string> lines;
            if (MQB.IsRead(data, out MQB? mqb))
                lines = Mqb(mqb);
            else if (f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase))
                lines = isEr ? ErHkx(data) : Ds3Hkx(data);
            else
                continue;
            yield return $"== {ItemText.Leaf(f.Name)}";
            foreach (var line in Safe(lines))
                yield return "  " + line;
        }
    }

    private static IEnumerable<string> Safe(IEnumerable<string> lines)
    {
        using var e = lines.GetEnumerator();
        while (true)
        {
            string line;
            bool failed = false;
            try
            {
                if (!e.MoveNext())
                    yield break;
                line = e.Current;
            }
            catch (Exception ex)
            {
                line = $"error: {ex.GetType().Name}: {ex.Message}";
                failed = true;
            }
            yield return line;
            if (failed)
                yield break;
        }
    }

    public static string Value(MQB.Parameter p) => p.Value switch
    {
        null => "null",
        byte[] b => $"bytes[{b.Length}] {Convert.ToHexString(b.Take(32).ToArray())}",
        System.Collections.IEnumerable list and not string => "[" + string.Join(",", list.Cast<object>().Take(8)) + "]",
        _ => p.Value.ToString() ?? "",
    };

    private static string Params(IEnumerable<MQB.Parameter> ps) =>
        string.Join("; ", ps.Select(p => $"{p.Name}:{p.Type}{(p.MemberCount > 1 ? "x" + p.MemberCount : "")}={Value(p)}{(p.Sequences.Count > 0 ? $" ({p.Sequences.Count} seq)" : "")}"));

    private static IEnumerable<string> Mqb(MQB mqb)
    {
        yield return $"MQB version {mqb.Version} (0x{(uint)mqb.Version:X}), name \"{mqb.Name}\", {mqb.Framerate} fps, bigEndian {mqb.BigEndian}, "
                     + $"directory \"{mqb.ResourceDirectory}\", {mqb.Resources.Count} resources, {mqb.Cuts.Count} cuts";
        for (int i = 0; i < mqb.Resources.Count; i++)
        {
            var res = mqb.Resources[i];
            yield return $"res {i} \"{res.Name}\" parent {res.ParentIndex} unk48 {res.Unk48} path \"{res.Path}\" | {Params(res.Parameters)}";
        }
        foreach (var cut in mqb.Cuts)
        {
            yield return $"cut \"{cut.Name}\" {cut.Duration} frames, unk44 {cut.Unk44}, {cut.Timelines.Count} timelines";
            for (int t = 0; t < cut.Timelines.Count; t++)
            {
                var tl = cut.Timelines[t];
                yield return $"  timeline {t} unk10 {tl.Unk10}, {tl.Events.Count} events | {Params(tl.Parameters)}";
                foreach (var ev in tl.Events.Take(12))
                    yield return $"    event id {ev.ID} res {ev.ResourceIndex} ({(ev.ResourceIndex >= 0 && ev.ResourceIndex < mqb.Resources.Count ? mqb.Resources[ev.ResourceIndex].Name : "-")}) "
                                 + $"start {ev.StartFrame} dur {ev.Duration} unk08 {ev.Unk08} unk14 {ev.Unk14} unk18 {ev.Unk18} unk1C {ev.Unk1C} unk20 {ev.Unk20} unk28 {ev.Unk28} "
                                 + $"transforms {ev.Transforms.Count} | {Params(ev.Parameters)}";
                if (tl.Events.Count > 12)
                    yield return $"    ... {tl.Events.Count - 12} more events";
            }
        }
        // Round trip: the port rewrites MQBs with SoulsFormats, so it must give back the same bytes.
        byte[] again = mqb.Write();
        var reread = MQB.Read(again);
        yield return $"round trip: {again.Length} bytes, {reread.Resources.Count} resources, {reread.Cuts.Count} cuts";
    }

    private static IEnumerable<string> Ds3Hkx(byte[] data)
    {
        var hkx = HKX.Read(data, HKX.HKXVariation.HKXDS3, false);
        yield return "objects: " + string.Join(", ", hkx.DataSection.Objects.GroupBy(o => o.GetType().Name).Select(g => $"{g.Key} x{g.Count()}"));
        foreach (var sk in hkx.DataSection.Objects.OfType<HKX.HKASkeleton>())
        {
            int n = (int)sk.Bones.Size;
            var names = Enumerable.Range(0, n).Select(i => sk.Bones[i].Name.GetString()).ToList();
            var parents = Enumerable.Range(0, (int)sk.ParentIndices.Size).Select(i => (int)sk.ParentIndices[i].data).ToList();
            var roots = Enumerable.Range(0, n).Where(i => i >= parents.Count || parents[i] < 0).Select(i => names[i]).ToList();
            yield return $"skeleton \"{sk.Name.GetString()}\": {n} bones, {roots.Count} roots: {string.Join(" ", roots.Take(60))}";
            yield return $"  first bones: {string.Join(" ", names.Take(40))}";
        }
        foreach (var a in hkx.DataSection.Objects.OfType<HKX.HKASplineCompressedAnimation>())
            yield return $"spline animation: {a.Duration:0.###} s, {a.FrameCount} frames, {a.TransformTrackCount} transform tracks, {a.FloatTrackCount} float tracks, {a.BlockCount} blocks";
        foreach (var a in hkx.DataSection.Objects.OfType<HKX.HKAInterleavedUncompressedAnimation>())
            yield return $"interleaved animation: {a.Duration:0.###} s, {a.TransformTrackCount} transform tracks, {a.FloatTrackCount} float tracks";
        foreach (var b in hkx.DataSection.Objects.OfType<HKX.HKAAnimationBinding>())
            yield return $"binding: {b.TransformTrackToBoneIndices.Size} track->bone, {b.FloatTrackToFloatSlotIndices.Size} float, blend {b.BlendHint}, skeleton \"{b.OriginalSkeletonName}\"";
        foreach (var f in hkx.DataSection.Objects.OfType<HKX.HKADefaultAnimatedReferenceFrame>())
            yield return $"reference frame: {f.Duration:0.###} s, {f.ReferenceFrameSamples.Size} samples";
    }

    private static IEnumerable<string> ErHkx(byte[] data)
    {
        IHavokObject root;
        lock (AnimPort.HavokLock)
            root = AnimPort.ReadEr(data, null);
        if (root is not hkRootLevelContainer container)
        {
            yield return $"root: {root.GetType().Name}";
            yield break;
        }
        foreach (var v in container.m_namedVariants)
            yield return $"variant \"{v.m_name}\" class \"{v.m_className}\" -> {v.m_variant?.GetType().Name}";
        foreach (var anims in container.m_namedVariants.Select(v => v.m_variant).OfType<hkaAnimationContainer>())
        {
            foreach (var sk in anims.m_skeletons)
            {
                var roots = sk!.m_bones.Where((b, i) => i >= sk.m_parentIndices.Count || sk.m_parentIndices[i] < 0).Select(b => b.m_name).ToList();
                yield return $"skeleton \"{sk.m_name}\": {sk.m_bones.Count} bones, {roots.Count} roots: {string.Join(" ", roots.Take(60))}";
                yield return $"  first bones: {string.Join(" ", sk.m_bones.Take(40).Select(b => b.m_name))}";
            }
            foreach (var a in anims.m_animations)
                yield return $"animation {a!.GetType().Name}: {a.m_duration:0.###} s, {a.m_numberOfTransformTracks} transform tracks, {a.m_numberOfFloatTracks} float tracks, "
                             + $"annotations {a.m_annotationTracks.Count}, extracted motion {a.m_extractedMotion?.GetType().Name ?? "none"}"
                             + (a is hkaSplineCompressedAnimation s ? $", {s.m_numFrames} frames, {s.m_numBlocks} blocks, {s.m_maxFramesPerBlock} per block" : "");
            foreach (var b in anims.m_bindings)
                yield return $"binding: {b!.m_transformTrackToBoneIndices.Count} track->bone, {b.m_floatTrackToFloatSlotIndices.Count} float, blend {b.m_blendHint}, skeleton \"{b.m_originalSkeletonName}\"";
        }
    }

    private static IEnumerable<string> ScriptLines(GameArchives game, string path)
    {
        if (!game.Contains(path))
        {
            yield return $"missing {path}";
            yield break;
        }
        var script = EMEVD.Read(game.Read(path));
        foreach (var e in script.Events)
            for (int i = 0; i < e.Instructions.Count; i++)
            {
                var ins = e.Instructions[i];
                if (ins.Bank != 2002 && !(ins.Bank == 3 && ins.ID == 31))
                    continue;
                var ps = e.Parameters.Where(p => p.InstructionIndex == i).Select(p => $"@{p.TargetStartByte}<-X{p.SourceStartByte}");
                yield return $"event {e.ID} line {i}: {ins.Bank}[{ins.ID:D2}] {Convert.ToHexString(ins.ArgData)} {string.Join(" ", ps)}";
            }
    }

    private static IEnumerable<string> Params(PorterContext ctx)
    {
        var store = ctx.LoadParams(PorterGame.EldenRing);
        foreach (var name in new[] { "CutsceneMapIdParam", "SoundCutsceneParam", "CutSceneTextureLoadParam", "CutsceneGparamTimeParam", "CutsceneTimezoneConvertParam" })
        {
            var param = store.Get(name);
            if (param is null)
            {
                yield return $"{name}: missing";
                continue;
            }
            yield return $"{name}: {param.Rows.Count} rows, ids {string.Join(" ", param.Rows.Take(30).Select(r => r.ID))}";
            foreach (var row in param.Rows.Where(r => r.ID / 10_000 == 1000 || r.ID / 10_000 == 1400).Take(8))
                yield return $"  {row.ID}: {string.Join(", ", row.Cells.Where(c => !c.Def.InternalName.StartsWith("disableParam") && !c.Def.InternalName.StartsWith("reserve") && !c.Def.InternalName.StartsWith("pad")).Select(c => $"{c.Def.InternalName}={c.Value}"))}";
        }
    }
}
