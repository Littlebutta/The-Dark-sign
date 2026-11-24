using SoulsFormats;
// divmove <div00> <div01> <id>... : moves files by binder ID from div01 to div00, renamed into div00's folder; bytes and IDs kept.
var d0 = BND4.Read(DCX.Decompress(File.ReadAllBytes(args[0]), out var c0));
var d1 = BND4.Read(DCX.Decompress(File.ReadAllBytes(args[1]), out var c1));
var sample = d0.Files.First(f => f.Name.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)).Name;
var folder = sample[..sample.LastIndexOf('\\')];
Console.WriteLine($"div00 folder: {folder}");
foreach (var id in args.Skip(2).Select(int.Parse))
{
    var f = d1.Files.Single(x => x.ID == id);
    var old = f.Name;
    if (d0.Files.Any(x => x.ID == id)) throw new Exception($"div00 already has {id}");
    d1.Files.Remove(f);
    f.Name = folder + "\\" + old[(old.LastIndexOf('\\') + 1)..];
    d0.Files.Add(f);
    Console.WriteLine($"{id}: {old} -> {f.Name} ({f.Bytes.Length} bytes)");
}
d0.Files.Sort((a, b) => a.ID.CompareTo(b.ID));
File.WriteAllBytes(args[0], DCX.Compress(d0.Write(), c0));
File.WriteAllBytes(args[1], DCX.Compress(d1.Write(), c1));
Console.WriteLine($"div00 {d0.Files.Count} files, div01 {d1.Files.Count} files");
