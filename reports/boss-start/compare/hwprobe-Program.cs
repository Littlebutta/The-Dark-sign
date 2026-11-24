using System.Globalization;
using SoulsFormats;
using DarkSign.Porter;

// hwprobe: the animations DS3's High Wall script (m30_00_00_00) forces on characters, directly (2003[18]) or through
// common_func events whose ForceAnimation takes the animation from a parameter, with each character's model, the
// clip's length and frames, and the TAE entry's IsLoopByDefault.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
TAE.ValidateEventBank = false;
var ex = "D:/The Dark sign/TheDarkSign/Work/extract/DS3";
var m30 = EMEVD.Read(File.ReadAllBytes($"{ex}/event/m30_00_00_00.emevd"));
var cf = EMEVD.Read(File.ReadAllBytes($"{ex}/event/common_func.emevd"));
var msb = MSB3.Read($"{ex}/map/mapstudio/m30_00_00_00.msb");
var models = msb.Parts.Enemies.Where(e => e.EntityID > 0).GroupBy(e => e.EntityID).ToDictionary(g => g.Key, g => (g.First().ModelName, g.First().Name));
var arc = GameArchives.Open(PorterGame.DarkSouls3, "D:/The Dark sign/DARK SOULS III/Game", new ExternalData("D:/The Dark sign/TheDarkSign/Work/cache"));

// common_func events: which parameter byte offsets feed a ForceAnimation's entity (arg 0) and anim (arg 4), and loop.
var forced = new List<(long Event, int Entity, int Anim, int Loop, string Via)>();
var cfMap = new Dictionary<long, List<(int EntSrc, int AnimSrc, int AnimConst, byte Loop)>>();
foreach (var ev in cf.Events)
    for (int i = 0; i < ev.Instructions.Count; i++)
    {
        var ins = ev.Instructions[i];
        if (ins.Bank != 2003 || ins.ID != 18) continue;
        int ent = ev.Parameters.FirstOrDefault(p => p.InstructionIndex == i && p.TargetStartByte == 0)?.SourceStartByte is long e ? (int)e : -1;
        int anim = ev.Parameters.FirstOrDefault(p => p.InstructionIndex == i && p.TargetStartByte == 4)?.SourceStartByte is long a ? (int)a : -1;
        if (!cfMap.TryGetValue(ev.ID, out var l)) cfMap[ev.ID] = l = new();
        l.Add((ent, anim, BitConverter.ToInt32(ins.ArgData, 4), ins.ArgData[8]));
    }
foreach (var ev in m30.Events)
    foreach (var ins in ev.Instructions)
    {
        if (ins.Bank == 2003 && ins.ID == 18)
        {
            int ent = BitConverter.ToInt32(ins.ArgData, 0);
            if (ent != 0) forced.Add((ev.ID, ent, BitConverter.ToInt32(ins.ArgData, 4), ins.ArgData[8], "direct"));
        }
        if (ins.Bank == 2000 && ins.ID == 6)
        {
            // Args: event ID (uint), then the parameters.
            uint target = BitConverter.ToUInt32(ins.ArgData, 0);
            if (!cfMap.TryGetValue(target, out var uses)) continue;
            byte[] p = ins.ArgData[4..];
            int At(int o) => o >= 0 && o + 4 <= p.Length ? BitConverter.ToInt32(p, o) : 0;
            foreach (var u in uses)
            {
                int ent = u.EntSrc >= 0 ? At(u.EntSrc) : 0;
                int anim = u.AnimSrc >= 0 ? At(u.AnimSrc) : u.AnimConst;
                if (ent != 0 && anim >= 0) forced.Add((ev.ID, ent, anim, u.Loop, $"common_func {target}"));
            }
        }
    }

var taeCache = new Dictionary<string, (Dictionary<long, TAE.Animation> Tae, BND4 Bnd)>();
(Dictionary<long, TAE.Animation>, BND4)? Chr(string model)
{
    if (taeCache.TryGetValue(model, out var c)) return c;
    try
    {
        var bnd = BND4.Read(arc.Read($"/chr/{model}.anibnd.dcx"));
        var d = new Dictionary<long, TAE.Animation>();
        foreach (var f in bnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
            try { foreach (var a in TAE.Read(f.Bytes).Animations) d.TryAdd(a.ID, a); } catch { }
        return taeCache[model] = (d, bnd);
    }
    catch (Exception e) { Console.Error.WriteLine($"{model}: {e.Message}"); return null; }
}
Console.WriteLine("event\tvia\tentity\tpart\tmodel\tanim\tscript loop\tTAE entry\tIsLoopByDefault\tclip\tduration s\tframes");
foreach (var f in forced.Distinct().OrderBy(f => f.Anim).ThenBy(f => f.Entity))
{
    var (model, part) = models.TryGetValue(f.Entity, out var m) ? m : ("?", "?");
    string entry = "-", loopDef = "-", clip = "-", dur = "-", frames = "-";
    if (model != "?" && Chr(model) is var (tae, bnd))
    {
        if (tae.TryGetValue(f.Anim, out var a))
        {
            long fileAnim = f.Anim;
            switch (a.MiniHeader)
            {
                case TAE.Animation.AnimMiniHeader.Standard s:
                    entry = s.ImportsHKX ? $"Standard, imports file of {s.ImportHKXSourceAnimID}" : "Standard";
                    loopDef = s.IsLoopByDefault.ToString();
                    if (s.ImportsHKX) fileAnim = s.ImportHKXSourceAnimID;
                    break;
                case TAE.Animation.AnimMiniHeader.ImportOtherAnim io:
                    entry = $"ImportOtherAnim {io.ImportFromAnimID}";
                    fileAnim = io.ImportFromAnimID;
                    if (tae.TryGetValue(io.ImportFromAnimID, out var src) && src.MiniHeader is TAE.Animation.AnimMiniHeader.Standard ss)
                        loopDef = $"{ss.IsLoopByDefault} (of {io.ImportFromAnimID})";
                    break;
            }
            string name = $"a{fileAnim / 1_000_000:D3}_{fileAnim % 1_000_000:D6}.hkx";
            var file = bnd.Files.FirstOrDefault(x => x.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            clip = file is null ? $"{name} (not in binder)" : name;
            if (file is not null)
                try
                {
                    var hkx = SoulsAssetPipeline.Animation.HKX.Read(file.Bytes, SoulsAssetPipeline.Animation.HKX.HKXVariation.HKXDS3, false);
                    var an = hkx.DataSection.Objects.OfType<SoulsAssetPipeline.Animation.HKX.HKASplineCompressedAnimation>().FirstOrDefault();
                    dur = $"{an?.Duration:0.###}"; frames = $"{an?.FrameCount}";
                }
                catch (Exception e) { dur = "unreadable " + e.Message; }
        }
        else entry = "no TAE entry";
    }
    Console.WriteLine($"{f.Event}\t{f.Via}\t{f.Entity}\t{part}\t{model}\t{f.Anim}\t{f.Loop}\t{entry}\t{loopDef}\t{clip}\t{dur}\t{frames}");
}
