using System.Globalization;
using SoulsFormats;
using HKLib.hk2018;
using HKLib.Serialization.hk2018.Binary;

// cmpdump tae <anibnd>... -- <ids>   : TAE entries (header, file reference, every event with words decoded)
// cmpdump clips <anibnd>... -- <ids> : animation files a00N_0<id>.hkx: duration, frames, blend hint, type (ER or DS3)
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
TAE.ValidateEventBank = false;
var mode = args[0];
var sep = Array.IndexOf(args, "--");
var files = args[1..sep];
var ids = args[(sep + 1)..].Select(long.Parse).ToHashSet();
foreach (var path in files)
{
    var bnd = BND4.Read(File.ReadAllBytes(path));
    Console.WriteLine($"==== {path} ({bnd.Files.Count} files)");
    if (mode == "tae")
        foreach (var f in bnd.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
        {
            TAE tae;
            try { tae = TAE.Read(f.Bytes); } catch (Exception ex) { Console.WriteLine($"{f.Name}: unreadable {ex.Message}"); continue; }
            foreach (var a in tae.Animations.Where(a => ids.Contains(a.ID % 1_000_000)))
            {
                Console.WriteLine($"## {Leaf(f.Name)} (binder ID {f.ID}) entry {a.ID}");
                switch (a.MiniHeader)
                {
                    case TAE.Animation.AnimMiniHeader.Standard s:
                        Console.WriteLine($"   header Standard: ImportsHKX {s.ImportsHKX} ImportHKXSourceAnimID {s.ImportHKXSourceAnimID} IsLoopByDefault {s.IsLoopByDefault} AllowDelayLoad {s.AllowDelayLoad}");
                        break;
                    case TAE.Animation.AnimMiniHeader.ImportOtherAnim io:
                        Console.WriteLine($"   header ImportOtherAnim: ImportFromAnimID {io.ImportFromAnimID} Unknown {io.Unknown}");
                        break;
                    default:
                        Console.WriteLine($"   header {a.MiniHeader?.GetType().Name}");
                        break;
                }
                Console.WriteLine($"   AnimFileName '{a.AnimFileName}', {a.Events.Count} events");
                foreach (var e in a.Events.OrderBy(e => e.StartTime).ThenBy(e => e.Type))
                {
                    var p = e.GetParameterBytes(false);
                    var words = string.Join(" ", Enumerable.Range(0, p.Length / 4).Select(i => Word(p, i * 4)));
                    string mark = e.Type switch { 0 => "  <== JumpTable (type 0)", 231 => "  <== type 231", _ => "" };
                    Console.WriteLine($"     type {e.Type,4} {e.StartTime,7:0.000}-{e.EndTime,7:0.000}  [{Convert.ToHexString(p)}]  {words}{mark}");
                    if (e.Type == 0 && p.Length >= 4)
                        Console.WriteLine($"          JumpTable id {BitConverter.ToInt32(p, 0)}, arg1 {(p.Length >= 8 ? BitConverter.ToInt32(p, 4) : 0)}, arg2 {(p.Length >= 12 ? BitConverter.ToSingle(p, 8) : 0)}");
                }
            }
        }
    else
    {
        var comp = bnd.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes;
        foreach (var f in bnd.Files.Where(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
        {
            var leaf = Leaf(f.Name);
            var m = System.Text.RegularExpressions.Regex.Match(leaf, @"^a(\d+)_(\d+)\.hkx$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || !ids.Contains(long.Parse(m.Groups[2].Value))) continue;
            Console.WriteLine($"  {leaf} (binder ID {f.ID}, {f.Bytes.Length} bytes): {Facts(f.Bytes, comp)}");
        }
    }
}

static string Leaf(string n) => n[(n.LastIndexOfAny(['\\', '/']) + 1)..];
static string Word(byte[] p, int o)
{
    int i = BitConverter.ToInt32(p, o);
    float fl = BitConverter.ToSingle(p, o);
    return Math.Abs(i) < 100_000_000 ? i.ToString() : float.IsFinite(fl) && Math.Abs(fl) < 1e6 && Math.Abs(fl) > 1e-6 ? $"{fl:0.###}f" : i.ToString();
}
static string Facts(byte[] bytes, byte[] comp)
{
    try
    {
        var ser = new HavokBinarySerializer();
        if (comp is not null) ser.LoadCompendium(new MemoryStream(comp));
        var root = (hkRootLevelContainer)ser.Read(new MemoryStream(bytes));
        var cont = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaAnimationContainer>().First();
        var a = cont.m_animations.FirstOrDefault();
        var b = cont.m_bindings.FirstOrDefault();
        int frames = a switch { hkaSplineCompressedAnimation s => s.m_numFrames, hkaInterleavedUncompressedAnimation u => u.m_transforms.Count / Math.Max(1, u.m_numberOfTransformTracks), _ => -1 };
        return $"ER: duration {a?.m_duration:0.###} s, frames {frames}, class {a?.GetType().Name}, type {a?.m_type}, tracks {a?.m_numberOfTransformTracks}, blend hint {b?.m_blendHint}, bound bones {b?.m_transformTrackToBoneIndices.Count}";
    }
    catch (Exception ex1)
    {
        try
        {
            var hkx = SoulsAssetPipeline.Animation.HKX.Read(bytes, SoulsAssetPipeline.Animation.HKX.HKXVariation.HKXDS3, false);
            var a = hkx.DataSection.Objects.OfType<SoulsAssetPipeline.Animation.HKX.HKASplineCompressedAnimation>().FirstOrDefault();
            var b = hkx.DataSection.Objects.OfType<SoulsAssetPipeline.Animation.HKX.HKAAnimationBinding>().FirstOrDefault();
            return $"DS3: duration {a?.Duration:0.###} s, frames {a?.FrameCount}, class {a?.GetType().Name}, type {a?.AnimationType}, tracks {a?.TransformTrackCount}, blend hint {b?.BlendHint}";
        }
        catch (Exception ex2) { return $"unreadable (ER: {ex1.Message}; DS3: {ex2.Message})"; }
    }
}
