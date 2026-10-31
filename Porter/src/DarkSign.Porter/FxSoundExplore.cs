using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// explore-fx-sound: a read-only report for porting DS3 weapon effects and sounds, so the converters can be designed
/// from facts. It lists both games' effect binders and sound files, which TAE event types carry effect or sound IDs
/// (first parameters checked against each game's FXR IDs), how DS3's sound files name their samples, and which
/// Wwise event name format Elden Ring's TAE sound IDs hash to.
/// </summary>
public static class FxSoundExplore
{
    public static int Run(PorterContext ctx)
    {
        var report = new StringBuilder();
        var fxrIds = new Dictionary<PorterGame, Dictionary<int, string>>();
        var events = new Dictionary<PorterGame, Dictionary<int, List<byte[]>>>();
        foreach (var game in new[] { PorterGame.DarkSouls3, PorterGame.EldenRing })
        {
            using var archives = ctx.OpenArchives(game);
            report.AppendLine($"# {game.ShortName}");
            ListFiles(report, archives, ctx.GameDir(game));
            fxrIds[game] = Effects(report, archives);
            events[game] = TaeEvents(archives);
        }

        // Event types: which first parameters are effect IDs, per game.
        report.AppendLine("\n# TAE event types (type, count DS3/ER, param bytes DS3/ER, first ints that are FXR IDs DS3/ER, samples)");
        foreach (var type in events.Values.SelectMany(e => e.Keys).Distinct().Order())
        {
            var ds3 = events[PorterGame.DarkSouls3].GetValueOrDefault(type) ?? [];
            var er = events[PorterGame.EldenRing].GetValueOrDefault(type) ?? [];
            report.AppendLine($"{type,4}  {ds3.Count,6}/{er.Count,-6} {Lengths(ds3),-8}/{Lengths(er),-8} " +
                              $"fx {FxShare(ds3, fxrIds[PorterGame.DarkSouls3])}/{FxShare(er, fxrIds[PorterGame.EldenRing])}  " +
                              $"ds3 {Sample(ds3)}  er {Sample(er)}");
        }

        Sounds(report, ctx, events);
        WeaponSfx(report, ctx);

        var path = ctx.WorkPath("reports", "explore", "fx-sound.txt");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"report: {path} ({report.Length / 1024} KB)");
        return 0;
    }

    private static void ListFiles(StringBuilder report, GameArchives archives, string gameDir)
    {
        var archived = archives.Entries.Where(e => e.Path is not null && (e.Path.StartsWith("/sfx/") || e.Path.StartsWith("/sound/") || e.Path.StartsWith("/sd/")))
            .Select(e => (Path: e.Path!, Size: (long)e.Header.PaddedFileSize));
        var loose = new[] { "sfx", "sound", "sd" }.Select(d => Path.Combine(gameDir, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Select(f => (Path: "loose:/" + Path.GetRelativePath(gameDir, f).Replace('\\', '/'), Size: new FileInfo(f).Length));
        foreach (var g in archived.Concat(loose).GroupBy(f => Path.GetDirectoryName(f.Path)!.Replace('\\', '/') + " *" + Ext(f.Path)).OrderBy(g => g.Key))
            report.AppendLine($"  {g.Key}: {g.Count()} files, {g.Sum(f => f.Size) / 1048576} MB, e.g. " +
                              string.Join(" ", g.OrderByDescending(f => f.Size).Take(6).Select(f => Path.GetFileName(f.Path))));
    }

    private static string Ext(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".dcx", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return Path.GetExtension(name).ToLowerInvariant();
    }

    // Every FXR ID in the game's effect binders, with the binder it is in; the binders' contents are summarised.
    private static Dictionary<int, string> Effects(StringBuilder report, GameArchives archives)
    {
        var ids = new Dictionary<int, string>();
        foreach (var entry in archives.Entries.Where(e => e.Path?.EndsWith(".ffxbnd.dcx", StringComparison.OrdinalIgnoreCase) == true).OrderBy(e => e.Path))
        {
            BND4 bnd;
            try { bnd = BND4.Read(archives.Read(entry.Path!)); }
            catch (Exception e) { report.AppendLine($"  {entry.Path}: unreadable ({e.Message})"); continue; }
            var name = Path.GetFileName(entry.Path!);
            var fxrs = bnd.Files.Where(f => f.Name.EndsWith(".fxr", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var f in fxrs)
                if (int.TryParse(Path.GetFileNameWithoutExtension(ItemText.Leaf(f.Name)).TrimStart('f'), out var id))
                    ids.TryAdd(id, name);
            if (!name.Contains("common", StringComparison.OrdinalIgnoreCase) && fxrs.Count < 2000)
                continue;
            var versions = fxrs.GroupBy(f => f.Bytes.Length > 6 ? BitConverter.ToUInt16(f.Bytes, 4) : -1).Select(g => $"v{g.Key}x{g.Count()}");
            report.AppendLine($"  {entry.Path}: {bnd.Files.Count} files (" +
                              string.Join(", ", bnd.Files.GroupBy(f => Ext(f.Name)).Select(g => $"{g.Key} {g.Count()}")) +
                              $"), FXR {string.Join(" ", versions)}, folders " +
                              string.Join(" ", bnd.Files.Select(f => Path.GetDirectoryName(f.Name.Replace('\\', '/'))?.Replace('\\', '/')).Distinct().Take(6)) +
                              $", e.g. {string.Join(" ", bnd.Files.Take(4).Select(f => ItemText.Leaf(f.Name)))}");
        }
        report.AppendLine($"  {ids.Count} FXR IDs in all effect binders");
        return ids;
    }

    private static Dictionary<int, List<byte[]>> TaeEvents(GameArchives archives)
    {
        TAE.ValidateEventBank = false;
        var byType = new Dictionary<int, List<byte[]>>();
        var main = BND4.Read(archives.Read("/chr/c0000.anibnd.dcx"));
        foreach (var file in main.Files.Where(f => f.Name.EndsWith(".tae", StringComparison.OrdinalIgnoreCase)))
        {
            TAE tae;
            try { tae = TAE.Read(file.Bytes); }
            catch { continue; }
            foreach (var e in tae.Animations.SelectMany(a => a.Events))
            {
                if (!byType.TryGetValue(e.Type, out var list))
                    byType[e.Type] = list = [];
                list.Add(e.GetParameterBytes(false));
            }
        }
        return byType;
    }

    private static string Lengths(List<byte[]> e) => e.Count == 0 ? "-" : string.Join(",", e.Select(b => b.Length).Distinct().Order().Take(3));

    private static string FxShare(List<byte[]> e, Dictionary<int, string> ids)
    {
        var withInt = e.Where(b => b.Length >= 4).Select(b => BitConverter.ToInt32(b, 0)).Where(v => v > 0).ToList();
        return withInt.Count == 0 ? "-" : $"{100 * withInt.Count(ids.ContainsKey) / withInt.Count}%";
    }

    private static string Sample(List<byte[]> e) => string.Join(" ", e.DistinctBy(Convert.ToHexString).Take(2)
        .Select(b => Convert.ToHexString(b, 0, Math.Min(b.Length, 16))));

    private static void Sounds(StringBuilder report, PorterContext ctx, Dictionary<PorterGame, Dictionary<int, List<byte[]>>> events)
    {
        // DS3: FMOD sound banks. The sample names show how sounds are keyed.
        report.AppendLine("\n# DS3 sound banks (FSB5)");
        using (var archives = ctx.OpenArchives(PorterGame.DarkSouls3))
        {
            var files = archives.Entries.Where(e => e.Path?.StartsWith("/sound/") == true).Select(e => (e.Path!, (Func<byte[]>)(() => archives.Read(e.Path!))))
                .Concat(LooseFiles(ctx.GameDir(PorterGame.DarkSouls3), "sound")).ToList();
            foreach (var (path, read) in files.Where(f => f.Item1.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Item1).Take(40))
            {
                try { report.AppendLine($"  {path}: {Fsb5Summary(read())}"); }
                catch (Exception e) { report.AppendLine($"  {path}: {e.Message}"); }
            }
            foreach (var (path, read) in files.Where(f => f.Item1.EndsWith(".fev", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Item1).Take(5))
            {
                var bytes = read();
                report.AppendLine($"  {path}: {bytes.Length} bytes, magic {Encoding.ASCII.GetString(bytes, 0, 4)}, strings e.g. {string.Join(" ", Strings(bytes).Take(30))}");
            }
        }

        // Elden Ring: Wwise banks. Event IDs are FNV-1 hashes of the event name; find the name format TAE sound IDs use.
        report.AppendLine("\n# Elden Ring sound banks (Wwise)");
        var erDir = ctx.GameDir(PorterGame.EldenRing);
        var eventIds = new HashSet<uint>();
        var banks = new List<(string Path, int Version, Dictionary<int, int> Types, long Size, bool Media)>();
        using (var archives = ctx.OpenArchives(PorterGame.EldenRing))
        {
            var files = archives.Entries.Where(e => e.Path?.StartsWith("/sd/") == true && e.Path.EndsWith(".bnk")).Select(e => (e.Path!, (Func<byte[]>)(() => archives.Read(e.Path!))))
                .Concat(LooseFiles(erDir, "sd").Where(f => f.Item1.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)));
            foreach (var (path, read) in files)
            {
                try
                {
                    var bytes = read();
                    var (version, types, media) = ReadBank(bytes, eventIds);
                    banks.Add((path, version, types, bytes.Length, media));
                }
                catch (Exception e) { report.AppendLine($"  {path}: {e.Message}"); }
            }
        }
        report.AppendLine($"  {banks.Count} banks, versions {string.Join(",", banks.Select(b => b.Version).Distinct())}, {eventIds.Count} events");
        foreach (var b in banks.OrderByDescending(b => b.Types.GetValueOrDefault(4)).Take(12))
            report.AppendLine($"  {b.Path}: {b.Size / 1024} KB, embedded media {b.Media}, objects {string.Join(" ", b.Types.OrderBy(t => t.Key).Select(t => $"{t.Key}:{t.Value}"))}");
        var wemDirs = LooseFiles(erDir, "sd").Where(f => f.Item1.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)).GroupBy(f => Path.GetDirectoryName(f.Item1)).Take(3);
        foreach (var g in wemDirs)
            report.AppendLine($"  wem {g.Key}: {g.Count()} files");

        foreach (var (game, gameEvents) in events)
        {
            report.AppendLine($"\n# {game.ShortName} sound event IDs vs Elden Ring event names (type: name format hit counts)");
            foreach (var type in new[] { 128, 129, 130, 131, 132, 133, 137, 138, 139 })
            {
                var pairs = (gameEvents.GetValueOrDefault(type) ?? []).Where(b => b.Length >= 8)
                    .Select(b => (Kind: BitConverter.ToInt32(b, 0), Id: BitConverter.ToInt32(b, 4))).Distinct().Take(400).ToList();
                if (pairs.Count == 0)
                    continue;
                var hits = new SortedDictionary<string, int>();
                foreach (var (kind, id) in pairs)
                    foreach (var letter in "acfgmopsvxbiyz")
                        foreach (var format in new[] { "{0}{1:D9}", "Play_{0}{1:D9}", "{0}{1}", "Play_{0}{1}", "Play_{0}{1:D10}" })
                        {
                            var name = string.Format(format, letter, id);
                            if (eventIds.Contains(Fnv1(name)))
                                hits[format.Replace("{0}", "?") + $" kind{kind}={letter}"] = hits.GetValueOrDefault(format.Replace("{0}", "?") + $" kind{kind}={letter}") + 1;
                        }
                report.AppendLine($"  {type}: {pairs.Count} (kind,id) e.g. {string.Join(" ", pairs.Take(4).Select(p => $"{p.Kind}:{p.Id}"))}; " +
                                  (hits.Count == 0 ? "no name matched" : string.Join(", ", hits.OrderByDescending(h => h.Value).Take(6).Select(h => $"{h.Key} x{h.Value}"))));
            }
        }
    }

    private static IEnumerable<(string, Func<byte[]>)> LooseFiles(string gameDir, string folder)
    {
        var dir = Path.Combine(gameDir, folder);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => ("loose:" + Path.GetRelativePath(gameDir, f), (Func<byte[]>)(() => File.ReadAllBytes(f))))
            : [];
    }

    private static string Fsb5Summary(byte[] bytes)
    {
        int at = IndexOf(bytes, "FSB5"u8.ToArray());
        if (at < 0)
            return $"{bytes.Length} bytes, no FSB5 header";
        int version = BitConverter.ToInt32(bytes, at + 4), samples = BitConverter.ToInt32(bytes, at + 8);
        int headerSize = BitConverter.ToInt32(bytes, at + 12), nameSize = BitConverter.ToInt32(bytes, at + 16), mode = BitConverter.ToInt32(bytes, at + 24);
        int baseSize = version == 0 ? 0x40 : 0x3C;
        var names = new List<string>();
        int nameTable = at + baseSize + headerSize;
        for (int i = 0; i < Math.Min(samples, 12) && nameSize > 0; i++)
        {
            int start = nameTable + BitConverter.ToInt32(bytes, nameTable + 4 * i), end = start;
            while (end < bytes.Length && bytes[end] != 0) end++;
            names.Add(Encoding.ASCII.GetString(bytes, start, end - start));
        }
        return $"{bytes.Length / 1024} KB, FSB5 v{version} at {at}, {samples} samples, codec {mode}, names e.g. {string.Join(" ", names)}";
    }

    private static (int Version, Dictionary<int, int> Types, bool Media) ReadBank(byte[] bytes, HashSet<uint> eventIds)
    {
        int version = -1;
        bool media = false;
        var types = new Dictionary<int, int>();
        for (int at = 0; at + 8 <= bytes.Length;)
        {
            var tag = Encoding.ASCII.GetString(bytes, at, 4);
            int size = BitConverter.ToInt32(bytes, at + 4), body = at + 8;
            if (tag == "BKHD")
                version = BitConverter.ToInt32(bytes, body);
            else if (tag == "DIDX" && size > 0)
                media = true;
            else if (tag == "HIRC")
            {
                int count = BitConverter.ToInt32(bytes, body), p = body + 4;
                for (int i = 0; i < count && p + 9 <= body + size; i++)
                {
                    int type = bytes[p], objSize = BitConverter.ToInt32(bytes, p + 1);
                    uint id = BitConverter.ToUInt32(bytes, p + 5);
                    types[type] = types.GetValueOrDefault(type) + 1;
                    if (type == 4)
                        eventIds.Add(id);
                    p += 5 + objSize;
                }
            }
            if (size < 0)
                break;
            at = body + size;
        }
        return (version, types, media);
    }

    // Wwise's ID hash: 32-bit FNV-1 of the lower-case name.
    public static uint Fnv1(string name)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.ASCII.GetBytes(name.ToLowerInvariant()))
            hash = (hash * 16777619) ^ b;
        return hash;
    }

    private static int IndexOf(byte[] bytes, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= Math.Min(bytes.Length, 1 << 20); i++)
            if (bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                return i;
        return -1;
    }

    private static IEnumerable<string> Strings(byte[] bytes)
    {
        var current = new StringBuilder();
        foreach (var b in bytes)
        {
            if (b >= 0x20 && b < 0x7F) { current.Append((char)b); continue; }
            if (current.Length >= 6)
                yield return current.ToString();
            current.Clear();
        }
    }

    // Effect fields of the DS3 weapons that get ported (resident effects, trails), with their most common values.
    private static void WeaponSfx(StringBuilder report, PorterContext ctx)
    {
        report.AppendLine("\n# Weapon effect fields (field: distinct values, most common)");
        foreach (var game in new[] { PorterGame.DarkSouls3, PorterGame.EldenRing })
        {
            var weapons = ctx.LoadParams(game).Get("EquipParamWeapon")!;
            var fields = weapons.Rows.FirstOrDefault()?.Cells.Select(c => c.Def.InternalName)
                .Where(n => n.Contains("sfx", StringComparison.OrdinalIgnoreCase) || n.Contains("Sfx") || n.Contains("Trail", StringComparison.OrdinalIgnoreCase)) ?? [];
            foreach (var field in fields)
            {
                var values = weapons.Rows.Select(r => r[field].Value?.ToString() ?? "").ToList();
                report.AppendLine($"  {game.ShortName} {field}: {values.Distinct().Count()} distinct, " +
                                  string.Join(" ", values.GroupBy(v => v).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key}x{g.Count()}")));
            }
        }
    }
}
