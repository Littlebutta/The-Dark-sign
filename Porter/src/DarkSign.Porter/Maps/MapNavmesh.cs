using System.Numerics;
using HKLib.hk2018;
using SoulsFormats;
using Nva = SmithboxFormats.NVA;

namespace DarkSign.Porter.Maps;

/// <summary>
/// A map's navigation files: the navmesh binder (.nvmhktbnd.dcx, a BND4 holding one Havok AI file per navmesh) and the
/// navmesh list (.nva.dcx) that tells the game which of them to load and how they join. Both are made from DS3's:
/// every navmesh the DS3 list names is converted with <see cref="NavmeshPort"/>, moved into map space by its list
/// entry, and written twice (Elden Ring's n and o sets, see <see cref="NavmeshList"/>); the list is rebuilt with
/// Elden Ring's names. Up to launcher 1.0.299 only the binder was written and the list stayed the host colosseum's,
/// which names none of DS3's navmeshes: the game loaded no navmesh at all for a DS3 map.
///
/// The binder's layout follows JortPob (infernoplus and contributors): n files from ID 10000, o files from ID 20000,
/// named n/o + map digits + _ + six-digit ModelID. The binder's format and the folder in its file names are the
/// host slot's own, and so is any .compendium it holds.
/// </summary>
public static class MapNavmesh
{
    public static string Ds3BinderPath(MapId id) => $"/map/{id}/{id}.nvmhktbnd.dcx";
    public static string Ds3ListPath(MapId id) => $"/map/{id}/{id}.nva.dcx";
    public static string ErBinderPath(MapId id) => $"/map/{id.AreaFolder}/{id}/{id}.nvmhktbnd.dcx";
    public static string ErListPath(MapId id) => $"/map/{id.AreaFolder}/{id}/{id}.nva.dcx";

    public const int NSetFirstId = 10000, OSetFirstId = 20000;

    /// <summary>Report lines for the port-maps log, the first the summary.</summary>
    public static IReadOnlyList<string> Port(PorterContext ctx, GameArchives ds3, GameArchives er, MapId ds3Id, MapId erId, MapPorter.Options options,
        MapId? hostId = null, MSB3? ds3Msb = null)
    {
        var source = Ds3BinderPath(ds3Id);
        if (!ds3.Contains(source))
            return [$"navmesh: DS3 has no {ItemText.Leaf(source)}; skipped"];
        var hostPath = ErBinderPath(hostId ?? erId);
        if (!er.Contains(hostPath))
            return [$"navmesh: Elden Ring has no {ItemText.Leaf(hostPath)} to copy the format from; skipped"];

        var (hostBytes, compression) = er.ReadWithCompression(hostPath);
        var host = BND4.Read(hostBytes);
        var ds3Binder = BND4.Read(ds3.Read(source));
        var lines = new List<string>();

        Nva? ds3List = null, hostList = null;
        var listCompression = compression;
        if (options.HostNavmeshList)
            lines.Add("navmesh list: the host's own, untouched (--host-navmesh-list): the game loads no DS3 navmesh");
        else if (!ds3.Contains(Ds3ListPath(ds3Id)))
            lines.Add($"navmesh list: DS3 has no {ItemText.Leaf(Ds3ListPath(ds3Id))}; the host's stays and the game loads no DS3 navmesh");
        else
        {
            try
            {
                ds3List = Nva.Read(ds3.Read(Ds3ListPath(ds3Id)));
            }
            catch (Exception ex)
            {
                lines.Add($"navmesh list: DS3's {ItemText.Leaf(Ds3ListPath(ds3Id))} can't be read ({ex.Message}); the host's stays and the game loads no DS3 navmesh");
            }
            var hostListPath = ErListPath(hostId ?? erId);
            if (ds3List is not null && er.Contains(hostListPath))
                try
                {
                    var (bytes, c) = er.ReadWithCompression(hostListPath);
                    listCompression = c;
                    hostList = Nva.Read(bytes);
                }
                catch (Exception ex)
                {
                    lines.Add($"navmesh list: the host's {ItemText.Leaf(hostListPath)} can't be read ({ex.Message}); built without it");
                }
        }

        // What the two games' own files look like, for whoever reads the log without the games at hand.
        foreach (var line in Describe(host))
            lines.Add($"navmesh: host {hostId ?? erId}: {line}");
        if (hostList is not null)
            foreach (var line in NavmeshList.Describe(hostList))
                lines.Add($"navmesh: host list: {line}");
        if (ds3List is not null)
            foreach (var line in NavmeshList.Describe(ds3List))
                lines.Add($"navmesh: DS3 list: {line}");

        var points = PlacementPoints(ds3Msb);
        var settings = new Settings(!options.BinaryCollision, options.Ds3NavmeshFlags, hostId, points);
        var result = Convert(ds3Binder, host, ds3Id, erId, settings, ds3List, hostList, Matrix4x4.Identity);
        // DS3's list entries are in world space; a map with a MapOffset event has its parts in the map's own space,
        // which the port keeps. Whichever space puts more of the map's characters over a navmesh face is the map's.
        if (ds3List is not null && MapOffset(ds3Msb) is { } offset && Matrix4x4.Invert(offset, out var toMap))
        {
            var moved = Convert(ds3Binder, host, ds3Id, erId, settings, ds3List, hostList, toMap);
            lines.Add($"navmesh: the DS3 map has a map offset; characters over a navmesh face: {result.Covered} of {points.Count} as listed, "
                      + $"{moved.Covered} with the offset taken off; using {(moved.Covered > result.Covered ? "the latter" : "the former")}");
            if (moved.Covered > result.Covered)
                result = moved;
        }

        File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts(ErBinderPath(erId))), DCX.Compress(result.Binder.Write(), compression));
        if (result.List is not null)
            File.WriteAllBytes(ctx.ModPath(MapPorter.ModParts(ErListPath(erId))), DCX.Compress(result.List.Write(), listCompression));
        lines.Insert(0, $"navmesh: {result.Summary}");
        lines.AddRange(result.Notes.Select(n => $"navmesh: {n}"));
        return lines;
    }

    /// <param name="Points">Where DS3 placed characters, to check the navmeshes lie under them.</param>
    public sealed record Settings(bool Xml, bool KeepFaceData = false, MapId? HostId = null, IReadOnlyList<Vector3>? Points = null);

    public sealed record Result(BND4 Binder, Nva? List, string Summary, int Converted, IReadOnlyList<string> Notes, int Covered);

    /// <summary>DS3's MapOffset event as a matrix (map space to world space), or null without one.</summary>
    public static Matrix4x4? MapOffset(MSB3? msb)
    {
        var offset = msb?.Events.MapOffsets.FirstOrDefault(o => o.Position != Vector3.Zero || o.Degree != 0);
        return offset is null ? null
            : Matrix4x4.CreateRotationY(offset.Degree * MathF.PI / 180) * Matrix4x4.CreateTranslation(offset.Position);
    }

    private static List<Vector3> PlacementPoints(MSB3? msb) =>
        msb is null ? [] : msb.Parts.Enemies.Select(e => e.Position).Concat(msb.Parts.Players.Select(p => p.Position)).ToList();

    /// <summary>
    /// The DS3 binder's navmeshes in a copy of the host binder (its own navmeshes left out), and the Elden Ring list
    /// for them. Without a DS3 list (<paramref name="ds3List"/> null) the files are converted where they are and no
    /// list is made, which is what every build up to 1.0.299 did.
    /// </summary>
    public static Result Convert(BND4 ds3, BND4 host, MapId ds3Id, MapId erId, Settings settings, Nva? ds3List = null, Nva? hostList = null,
        Matrix4x4? toMap = null)
    {
        var output = new BND4
        {
            Version = host.Version, Format = host.Format, BigEndian = host.BigEndian, BitBigEndian = host.BitBigEndian,
            Unicode = host.Unicode, Extended = host.Extended, Unk04 = host.Unk04, Unk05 = host.Unk05,
        };
        var hostHkx = host.Files.FirstOrDefault(f => f.Name.Contains(".hkx", StringComparison.OrdinalIgnoreCase));
        DCX.CompressionInfo? hostEntryCompression = null;
        if (hostHkx is not null && DCX.Is(hostHkx.Bytes))
        {
            DCX.Decompress(hostHkx.Bytes, out var entryCompression);
            hostEntryCompression = entryCompression;
        }
        var folder = hostHkx is null ? $@"N:\GR\data\INTERROOT_win64\map\{erId}\navimesh\bind6\" : hostHkx.Name[..^ItemText.Leaf(hostHkx.Name).Length];
        if (settings.HostId is { } hid && hid != erId)
            folder = folder.Replace(hid.ToString(), erId.ToString(), StringComparison.OrdinalIgnoreCase);
        var flags = hostHkx?.Flags ?? Binder.FileFlags.Flag1;
        var extension = hostHkx is null ? ".hkx" : ItemText.Leaf(hostHkx.Name)[ItemText.Leaf(hostHkx.Name).IndexOf(".hkx", StringComparison.OrdinalIgnoreCase)..];
        foreach (var keep in host.Files.Where(f => f.Name.Contains(".compendium", StringComparison.OrdinalIgnoreCase)))
            output.Files.Add(keep);

        var layout = HostLayout(host);
        byte[] Packed(byte[] bytes) => hostEntryCompression is null ? bytes : DCX.Compress(bytes, hostEntryCompression);
        byte[] Unpacked(BinderFile file) => DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes) : file.Bytes;
        var hkxFiles = ds3.Files.Where(f => ItemText.Leaf(f.Name).Contains(".hkx", StringComparison.OrdinalIgnoreCase)).ToList();
        var format = settings.Xml ? "XML" : "binary";
        var failed = new List<string>();
        var notes = new List<string>();
        string Failures() => failed.Count == 0 ? "" : $"; {failed.Count} failed: {string.Join("; ", failed.Take(5))}{(failed.Count > 5 ? "; ..." : "")}";

        if (ds3List is null)
        {
            int converted = 0, faces = 0;
            var nextId = output.Files.Count == 0 ? 0 : output.Files.Max(f => f.ID) + 1;
            foreach (var file in hkxFiles)
            {
                var leaf = ItemText.Leaf(file.Name);
                try
                {
                    var navmesh = NavmeshPort.Convert(Unpacked(file), settings.Xml, null, settings.KeepFaceData, layout);
                    output.Files.Add(new BinderFile(flags, nextId++, folder + leaf.Replace(ds3Id.Digits, erId.Digits, StringComparison.OrdinalIgnoreCase), Packed(navmesh.Bytes)));
                    converted++;
                    faces += navmesh.Faces;
                }
                catch (Exception ex)
                {
                    failed.Add($"{leaf} ({ex.Message})");
                }
            }
            return new Result(output, null, $"{converted} navmeshes, {faces} faces ({format} tagfiles), no list{Failures()}", converted, notes, 0);
        }

        var world = toMap ?? Matrix4x4.Identity;
        static string Stem(string name) => ItemText.Leaf(name).Split('.')[0].ToLowerInvariant();
        var byName = new Dictionary<string, BinderFile>();
        foreach (var file in hkxFiles)
            byName.TryAdd(Stem(file.Name), file);

        var usedModels = new HashSet<int>();
        int nextModel = ds3List.Navmeshes.Select(e => e.ModelID).Append(0).Max() + 1;
        var entries = new List<NavmeshList.Entry>();
        var meshes = new List<hkaiNavMesh?>();
        var named = new HashSet<string>();
        var faceData = new SortedDictionary<int, int>();
        int rotated = 0, missing = 0, renumbered = 0, linkSetups = 0, done = 0;
        long written = 0;
        for (int i = 0; i < ds3List.Navmeshes.Count; i++)
        {
            var entry = ds3List.Navmeshes[i];
            var stem = $"n{ds3Id.Digits}_{entry.ModelID:D6}";
            int model = entry.ModelID;
            // Two entries with one ModelID are one DS3 file placed twice; here every placement is its own file.
            if (model < 0 || model > 999_999 || !usedModels.Add(model))
            {
                while (!usedModels.Add(nextModel))
                    nextModel++;
                model = nextModel;
                renumbered++;
            }
            NavmeshPort.Result? navmesh = null;
            if (entry.ModelID >= 0 && byName.TryGetValue(stem, out var file))
            {
                named.Add(stem);
                try
                {
                    navmesh = NavmeshPort.Convert(Unpacked(file), settings.Xml, NavmeshList.Placement(entry) * world, settings.KeepFaceData, layout);
                    if (entry.Rotation.Length() > 1e-4f || Vector3.Distance(entry.Scale, Vector3.One) > 1e-4f)
                        rotated++;
                    foreach (var (value, count) in navmesh.FaceData)
                        faceData[value] = faceData.GetValueOrDefault(value) + count;
                    linkSetups += navmesh.LinkSetups;
                    foreach (var note in navmesh.Notes)
                        notes.Add($"{stem}: {note}");
                    done++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{stem} ({ex.Message})");
                }
            }
            else
                missing++;
            entries.Add(new NavmeshList.Entry(model, navmesh?.Faces ?? 0, navmesh?.Edges ?? 0));
            meshes.Add(navmesh?.Mesh);
            if (navmesh is null)
                continue;
            written += navmesh.Bytes.LongLength * 2;
            var packed = Packed(navmesh.Bytes);
            foreach (var (set, firstId) in new[] { ("n", NSetFirstId), ("o", OSetFirstId) })
                output.Files.Add(new BinderFile(flags, firstId + i, $"{folder}{set}{erId.Digits}_{model:D6}{extension}", packed));
        }
        output.Files.Sort((a, b) => a.ID.CompareTo(b.ID));

        var list = NavmeshList.Convert(ds3List, erId, entries, world, hostList);
        notes.InsertRange(0, list.Notes.Skip(1));
        var unnamed = hkxFiles.Count(f => !named.Contains(Stem(f.Name)));
        if (unnamed > 0)
            notes.Add($"{unnamed} DS3 navmesh files aren't in DS3's list; left out");
        if (missing > 0)
            notes.Add($"{missing} list entries have no file; kept as entries without faces");
        if (renumbered > 0)
            notes.Add($"{renumbered} entries shared a ModelID or had none; given new ones");
        notes.Add($"{rotated} of {done} navmeshes were turned or scaled into place, the rest only moved");
        if (faceData.Keys.Any(k => k != 0))
            notes.Add($"DS3 face data ({(settings.KeepFaceData ? "kept (--navmesh-flags ds3)" : "cleared: every face is plain ground")}): "
                      + string.Join(", ", faceData.OrderByDescending(k => k.Value).Take(12).Select(k => $"0x{k.Key:X} x{k.Value}")));
        if (linkSetups > 0)
            notes.Add($"{linkSetups} DS3 jump and drop links left out (enemies won't jump or drop between ledges)");

        // Two checks of the placement, on the map's own data: gate nodes lie on their navmesh's edges, and DS3's
        // characters stand over a navmesh face. Low numbers mean the navmeshes are not where the map is.
        int gates = 0, onMesh = 0;
        for (int i = 0; i < ds3List.Navmeshes.Count; i++)
            if (meshes[i] is { } mesh && mesh.m_edges.Count > 0)
                foreach (var gate in ds3List.Navmeshes[i].GateNodes)
                {
                    gates++;
                    if (DistanceToEdges(mesh, Vector3.Transform(gate.Position, world)) < 1f)
                        onMesh++;
                }
        if (gates > 0)
            notes.Add($"placement check: {onMesh} of {gates} gate nodes lie on their navmesh (within 1 m)");
        int covered = 0;
        if (settings.Points is { Count: > 0 } points)
        {
            var boxes = meshes.Where(m => m is not null).SelectMany(m => NavFaceTree.FaceBounds(m!)).ToArray();
            covered = points.AsParallel().Count(p => boxes.Any(b =>
                p.X >= b.Min.X - 0.5f && p.X <= b.Max.X + 0.5f && p.Z >= b.Min.Z - 0.5f && p.Z <= b.Max.Z + 0.5f
                && p.Y >= b.Min.Y - 1.5f && p.Y <= b.Max.Y + 2.5f));
            notes.Add($"placement check: {covered} of {points.Count} DS3 characters and start points stand over a navmesh face");
        }

        return new Result(output, list.List, $"{list.Notes[0]} ({format} tagfiles, {written / 1048576.0:F0} MB for the game to read){Failures()}", done, notes, covered);
    }

    /// <summary>The face and edge data layout of the host's first navmesh with faces; null when none reads.</summary>
    private static NavmeshPort.Layout? HostLayout(BND4 host)
    {
        var compendium = host.Files.FirstOrDefault(f => f.Name.Contains(".compendium", StringComparison.OrdinalIgnoreCase));
        byte[]? compendiumBytes = null;
        foreach (var file in host.Files.Where(f => f.Name.Contains(".hkx", StringComparison.OrdinalIgnoreCase)).Take(20))
            try
            {
                compendiumBytes ??= compendium is null ? null : DCX.Is(compendium.Bytes) ? DCX.Decompress(compendium.Bytes) : compendium.Bytes;
                var root = CollisionPort.Read(DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes) : file.Bytes, compendiumBytes);
                if (root.m_namedVariants.Select(v => v.m_variant).OfType<hkaiNavMesh>().FirstOrDefault(m => m.m_faces.Count > 0) is { } mesh)
                    return NavmeshPort.Layout.Of(mesh);
            }
            catch (Exception)
            {
                // An unreadable host file only means the minimum layout is used.
            }
        return null;
    }

    /// <summary>The distance from a point to the nearest edge of a mesh.</summary>
    private static float DistanceToEdges(hkaiNavMesh mesh, Vector3 point)
    {
        float best = float.MaxValue;
        foreach (var edge in mesh.m_edges)
        {
            var a4 = mesh.m_vertices[edge.m_a];
            var b4 = mesh.m_vertices[edge.m_b];
            var a = new Vector3(a4.X, a4.Y, a4.Z);
            var ab = new Vector3(b4.X, b4.Y, b4.Z) - a;
            float length = ab.LengthSquared();
            float t = length <= 0 ? 0 : Math.Clamp(Vector3.Dot(point - a, ab) / length, 0, 1);
            best = MathF.Min(best, Vector3.DistanceSquared(point, a + ab * t));
        }
        return MathF.Sqrt(best);
    }

    /// <summary>
    /// What is wrong with a written binder and list as a pair, for map-validate: the list is in two mirrored halves
    /// named by position, every entry with faces has its file, the files hold as many faces as the list says and a
    /// face tree, and connections and gates name entries that exist.
    /// </summary>
    public static List<string> Check(BND4 binder, Nva list, MapId erId, int sample = 12)
    {
        var problems = new List<string>();
        var entries = list.Navmeshes;
        if (entries is null || entries.Count == 0)
            return ["the list has no navmeshes"];
        if (list.Version != Nva.NVAVersion.EldenRing)
            problems.Add($"the list is version {(uint)list.Version}, not Elden Ring's 8");
        int half = entries.Count / 2;
        if (entries.Count % 2 != 0)
            problems.Add($"{entries.Count} entries: not an n half and an o half");
        var files = binder.Files.ToDictionary(f => ItemText.Leaf(f.Name).Split('.')[0].ToLowerInvariant(), f => f, StringComparer.Ordinal);
        int misnamed = 0, unmirrored = 0, noFile = 0, wrongFaces = 0, noTree = 0, read = 0, total = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            bool oSet = i >= half;
            int expected = oSet ? NavmeshList.OSet(NavmeshList.NameId(erId, i - half)) : NavmeshList.NameId(erId, i);
            if (entries[i].NameID != expected)
                misnamed++;
            if (!oSet && i + half < entries.Count && (entries[i + half].ModelID != entries[i].ModelID || entries[i + half].FaceCount != entries[i].FaceCount))
                unmirrored++;
            if (entries[i].FaceDataIndex != total)
                problems.Add($"entry {i}: FaceDataIndex {entries[i].FaceDataIndex}, the faces before it are {total}");
            total += entries[i].FaceCount;
            if (entries[i].FaceCount == 0)
                continue;
            if (!files.TryGetValue($"{(oSet ? "o" : "n")}{erId.Digits}_{entries[i].ModelID:D6}", out var file))
            {
                noFile++;
                continue;
            }
            if (read >= sample)
                continue;
            read++;
            var root = CollisionPort.Read(DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes) : file.Bytes);
            var mesh = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaiNavMesh>().FirstOrDefault();
            if (mesh is null || mesh.m_faces.Count != entries[i].FaceCount)
                wrongFaces++;
            var mediator = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaiStaticTreeNavMeshQueryMediator>().FirstOrDefault();
            if (mesh is null || NavFaceTree.Check(mediator?.m_tree, mesh) is not null)
                noTree++;
        }
        if (misnamed > 0)
            problems.Add($"{misnamed} entries aren't named by their position ({NavmeshList.NameId(erId, 0)} and up, the o half 2,000,000,000 higher)");
        if (unmirrored > 0)
            problems.Add($"{unmirrored} n entries have an o entry with another ModelID or face count");
        if (noFile > 0)
            problems.Add($"{noFile} entries with faces have no file in the binder");
        if (wrongFaces > 0)
            problems.Add($"{wrongFaces} of {read} sampled files hold another number of faces than their entry says");
        if (noTree > 0)
            problems.Add($"{noTree} of {read} sampled files have no face tree that holds their faces");
        var names = entries.Select(e => e.NameID).ToHashSet();
        int strayConnectors = list.Connectors?.Count(c => !names.Contains(c.MainNameID) || !names.Contains(c.TargetNameID)) ?? 0;
        if (strayConnectors > 0)
            problems.Add($"{strayConnectors} connections name a navmesh the list doesn't have");
        int strayGates = entries.Sum(e => e.GateNodes.Count(g => g.ConnectedNavmeshIndex < 0 || g.ConnectedNavmeshIndex >= entries.Count));
        if (strayGates > 0)
            problems.Add($"{strayGates} gate nodes lead to a position past the end of the list");
        return problems;
    }

    /// <summary>
    /// A navmesh binder in a few lines: its entries' IDs and names, and what its first navmesh file with faces holds
    /// (root objects, face and edge data). The port-maps log carries this for the host slot, so what Elden Ring's own
    /// files look like is on record without the game at hand.
    /// </summary>
    public static IEnumerable<string> Describe(BND4 binder)
    {
        var hkx = binder.Files.Where(f => f.Name.Contains(".hkx", StringComparison.OrdinalIgnoreCase)).ToList();
        yield return $"binder version {binder.Version} format {binder.Format}, {binder.Files.Count} files ({hkx.Count} hkx), "
                     + string.Join(", ", binder.Files.Take(3).Concat(binder.Files.Skip(3).TakeLast(2)).Select(f => $"[{f.ID}] {ItemText.Leaf(f.Name)} flags {f.Flags}{(DCX.Is(f.Bytes) ? " dcx" : "")}"));
        if (hkx.Count > 0)
            yield return $"folder {hkx[0].Name[..^ItemText.Leaf(hkx[0].Name).Length]}";
        var compendium = binder.Files.FirstOrDefault(f => f.Name.Contains(".compendium", StringComparison.OrdinalIgnoreCase));
        var compendiumBytes = compendium is null ? null : DCX.Is(compendium.Bytes) ? DCX.Decompress(compendium.Bytes) : compendium.Bytes;
        var faceData = new SortedDictionary<int, int>();
        var edgeData = new SortedDictionary<int, int>();
        string? first = null;
        int read = 0, unread = 0, userEdges = 0, streamingSets = 0, seeded = 0;
        foreach (var file in hkx.Take(40))
            try
            {
                var root = CollisionPort.Read(DCX.Is(file.Bytes) ? DCX.Decompress(file.Bytes) : file.Bytes, compendiumBytes);
                read++;
                var mesh = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaiNavMesh>().FirstOrDefault();
                if (mesh is null)
                    continue;
                int striding = Math.Max(1, mesh.m_faceDataStriding);
                for (int f = 0; f * striding < mesh.m_faceData.Count; f++)
                    faceData[mesh.m_faceData[f * striding]] = faceData.GetValueOrDefault(mesh.m_faceData[f * striding]) + 1;
                foreach (var value in mesh.m_edgeData)
                    edgeData[value] = edgeData.GetValueOrDefault(value) + 1;
                userEdges += mesh.m_faces.Sum(f => (int)f.m_numUserEdges);
                streamingSets += mesh.m_streamingSets.Count;
                if (mesh.m_clearanceCacheSeedingDataSet is not null)
                    seeded++;
                if (first is null && mesh.m_faces.Count > 0)
                    first = $"{ItemText.Leaf(file.Name)}: root [{string.Join(", ", root.m_namedVariants.Select(v => $"{v.m_name}:{v.m_className}"))}], "
                            + $"{mesh.m_faces.Count} faces, {mesh.m_edges.Count} edges, {mesh.m_vertices.Count} vertices, face data striding {mesh.m_faceDataStriding}, "
                            + $"edge data striding {mesh.m_edgeDataStriding}, flags {mesh.m_flags}, erosion {mesh.m_erosionRadius}, user data {mesh.m_userData}, "
                            + $"bounds {mesh.m_aabb.m_min} .. {mesh.m_aabb.m_max}, vertex 0 {(mesh.m_vertices.Count > 0 ? mesh.m_vertices[0].ToString() : "-")}, "
                            + $"face 0 cluster {mesh.m_faces[0].m_clusterIndex} padding {mesh.m_faces[0].m_padding}, edge 0 flags {(int)mesh.m_edges[0].m_flags}";
            }
            catch (Exception)
            {
                unread++;
            }
        if (first is not null)
            yield return first;
        yield return $"{read} of {Math.Min(40, hkx.Count)} sampled files read ({unread} not), {seeded} with clearance seeding, {streamingSets} streaming sets, {userEdges} user edges; "
                     + $"face data: {(faceData.Count == 0 ? "none" : string.Join(", ", faceData.OrderByDescending(k => k.Value).Take(10).Select(k => $"0x{k.Key:X} x{k.Value}")))}; "
                     + $"edge data: {(edgeData.Count == 0 ? "none" : string.Join(", ", edgeData.OrderByDescending(k => k.Value).Take(6).Select(k => $"0x{k.Key:X} x{k.Value}")))}";
    }
}
