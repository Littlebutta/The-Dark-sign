using System.Numerics;
using Nva = SmithboxFormats.NVA;

namespace DarkSign.Porter.Maps;

/// <summary>
/// A map's navmesh list (.nva): which navmesh files the map has, how they join, and the gates between them. Elden
/// Ring loads a navmesh only when the list names it, so a ported map needs a list made from DS3's; with the host
/// colosseum's own list the game never loaded a single DS3 navmesh, and enemies had nothing to path on.
///
/// What the two games' lists hold was worked out by Smithbox's Navigation tool (vawser and contributors), which
/// surveyed every map of both; JortPob (infernoplus and contributors) showed what the smallest list Elden Ring
/// accepts looks like. The rules followed here:
/// - Elden Ring names an entry by its position: NameID = AABBCC × 1000 + position in a legacy map. DS3 names it by its
///   model: (AA × 10 + BB) × 1,000,000 + ModelID. Connections name entries by NameID, so they are renumbered.
/// - Elden Ring has two sets of every navmesh: the n set in the first half of the list and the o set (large
///   characters) in the second, NameID + 2,000,000,000, files n… and o… in the binder. DS3 has one. The o set here is
///   a copy of the n set, as JortPob's is.
/// - DS3 entries place their mesh with a position, rotation and scale; Elden Ring's sit at the identity with the mesh
///   in map space. <see cref="NavmeshPort"/> moves each mesh, so the entries here are at the identity.
/// - Gate nodes (the points where two navmeshes meet, with the walking cost between a navmesh's gates) have the
///   same layout in both games and name navmeshes by position in the list, so they carry over; the o set's point
///   at the o half.
/// - FaceDataIndex is the running total of the face counts in list order; Unk4C = 1 marks an entry without faces.
/// - Elden Ring's list has a map UID (Section 11), kept from the host map's list.
/// Left out: node banks (faces an event script closes and opens, e.g. behind a door; their entity IDs are DS3's),
/// level connectors (joins to other maps) and DS3's jump and drop links, which Elden Ring keeps as edges in the mesh.
/// </summary>
public static class NavmeshList
{
    /// <summary>What JortPob writes as the map UID when there is no host list to take one from.</summary>
    public const int FallbackMapUid = 1726789910;

    public const uint OSetOffset = 2_000_000_000u;

    /// <summary>The NameID Elden Ring gives the n entry at a position of a legacy map's list.</summary>
    public static int NameId(MapId er, int position) => (er.Area * 10000 + er.Block * 100 + er.Region) * 1000 + position;

    public static int OSet(int nNameId) => unchecked((int)((uint)nNameId + OSetOffset));

    /// <summary>The NameID DS3 gives an entry, by its rule (Smithbox: 1,527 of 1,529 entries follow it).</summary>
    public static int Ds3NameId(MapId ds3, int modelId) => (ds3.Area * 10 + ds3.Block) * 1_000_000 + modelId;

    /// <summary>
    /// How a DS3 entry places its mesh: scale, then the rotation (radians, as Y × Z × X), then the position. Smithbox
    /// found DS3's gate nodes on their meshes only when the entries are read so (99 % within 1 m).
    /// </summary>
    public static Matrix4x4 Placement(Nva.Navmesh entry)
    {
        var r = entry.Rotation;
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Y)
                       * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Z)
                       * Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X);
        return Matrix4x4.CreateScale(entry.Scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(entry.Position);
    }

    /// <summary>One DS3 entry as it goes into the Elden Ring list.</summary>
    /// <param name="ModelId">The number in its files' names (n45_00_00_00_000100), DS3's unless two entries shared one.</param>
    /// <param name="Faces">The faces of its converted mesh; 0 when it has no file.</param>
    /// <param name="Edges">The edges of its converted mesh.</param>
    public sealed record Entry(int ModelId, int Faces, int Edges);

    public sealed record Result(Nva List, IReadOnlyList<string> Notes);

    /// <param name="ds3">DS3's list.</param>
    /// <param name="entries">One per DS3 entry, in order.</param>
    /// <param name="toMap">DS3 world space to the ported map's space, for the gate nodes.</param>
    /// <param name="host">The host slot's own list, for the map UID and the unknown per-entry values; may be null.</param>
    public static Result Convert(Nva ds3, MapId er, IReadOnlyList<Entry> entries, Matrix4x4 toMap, Nva? host)
    {
        if (entries.Count != ds3.Navmeshes.Count)
            throw new ArgumentException($"{entries.Count} entries for {ds3.Navmeshes.Count} DS3 navmeshes");
        var notes = new List<string>();
        int count = entries.Count;
        if (count > 999)
            throw new InvalidDataException($"{count} navmeshes; a NameID has room for 999");

        var list = new Nva();
        if (host?.Entries11 is { Count: > 0 } uid)
            foreach (var e in uid)
                list.Entries11.Add(new Nva.Entry11 { Unk00 = e.Unk00, Unk04 = e.Unk04, Unk08 = e.Unk08, Unk0C = e.Unk0C });
        else
            list.Entries11.Add(new Nva.Entry11 { Unk00 = FallbackMapUid });
        if (host is not null)
        {
            list.Section12Version = host.Section12Version;
            if (host.Navmeshes is { } hostMeshes)
                list.Navmeshes.Version = hostMeshes.Version;
        }
        var hostN = host?.Navmeshes?.FirstOrDefault(e => (uint)e.NameID < OSetOffset);
        var hostO = host?.Navmeshes?.FirstOrDefault(e => (uint)e.NameID >= OSetOffset) ?? hostN;

        // DS3 NameID -> position, for the connections. A NameID two entries share names the first.
        var position = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
            if (ds3.Navmeshes[i].NameID != -1 && !position.TryAdd(ds3.Navmeshes[i].NameID, i))
                notes.Add($"DS3 NameID {ds3.Navmeshes[i].NameID} is used twice; connections go to the first");

        int gates = 0, droppedGates = 0;
        foreach (bool oSet in new[] { false, true })
            for (int i = 0; i < count; i++)
            {
                var source = ds3.Navmeshes[i];
                var template = oSet ? hostO : hostN;
                var entry = new Nva.Navmesh
                {
                    Position = Vector3.Zero, PositionW = template?.PositionW ?? 1,
                    Rotation = Vector3.Zero, RotationW = template?.RotationW ?? 0,
                    Scale = Vector3.One, ScaleW = template?.ScaleW ?? 0,
                    NameID = oSet ? OSet(NameId(er, i)) : NameId(er, i),
                    ModelID = entries[i].ModelId,
                    FaceCount = entries[i].Faces,
                    Unk3C = template?.Unk3C ?? 0,
                    Unk4C = entries[i].Faces == 0 ? 1 : 0,
                    Unk44 = template?.Unk44 ?? 2.4f,
                    // 0xFF01: not one quarter of a split open world mesh.
                    Unk50 = 0xFF01,
                    Unk54 = template?.Unk54 ?? 0, Unk58 = template?.Unk58 ?? 0, Unk5C = template?.Unk5C ?? 0,
                };
                foreach (var gate in source.GateNodes)
                {
                    // A gate leads to a navmesh by its position in the list; one that leads nowhere, or to an entry
                    // without faces, would send a character to a navmesh that isn't there.
                    int target = gate.ConnectedNavmeshIndex;
                    if (entries[i].Faces == 0 || target < 0 || target >= count || entries[target].Faces == 0)
                    {
                        if (!oSet)
                            droppedGates++;
                        continue;
                    }
                    entry.GateNodes.Add(new Nva.GateNode
                    {
                        Position = Vector3.Transform(gate.Position, toMap),
                        ConnectedNavmeshIndex = (short)(oSet ? target + count : target),
                        NodeSubID = gate.NodeSubID,
                        NeighbourGateNodeCosts = [.. gate.NeighbourGateNodeCosts],
                        Unk14 = gate.Unk14,
                    });
                    if (!oSet)
                        gates++;
                }
                // Dropping a gate moves the ones after it up, so a list that lost any has no gates at all: the game
                // then joins navmeshes by their connections alone, as it does in JortPob's maps.
                list.Navmeshes.Add(entry);
            }
        if (droppedGates > 0)
        {
            foreach (var entry in list.Navmeshes)
                entry.GateNodes.Clear();
            notes.Add($"{droppedGates} of {gates + droppedGates} gate nodes lead to a navmesh without faces; the list is written without gate nodes");
            gates = 0;
        }

        int total = 0;
        foreach (var entry in list.Navmeshes)
        {
            entry.FaceDataIndex = total;
            total += entry.FaceCount;
        }

        int connectors = 0, droppedConnectors = 0, links = 0, badLinks = 0;
        foreach (bool oSet in new[] { false, true })
            foreach (var source in ds3.Connectors)
            {
                if (!position.TryGetValue(source.MainNameID, out int main) || !position.TryGetValue(source.TargetNameID, out int target)
                    || entries[main].Faces == 0 || entries[target].Faces == 0)
                {
                    if (!oSet)
                        droppedConnectors++;
                    continue;
                }
                var connector = new Nva.Connector
                {
                    MainNameID = oSet ? OSet(NameId(er, main)) : NameId(er, main),
                    TargetNameID = oSet ? OSet(NameId(er, target)) : NameId(er, target),
                    Unk14 = source.Unk14, Unk1C = source.Unk1C,
                };
                foreach (var c in source.NavMeshConnections)
                {
                    // An index past the mesh it names would have the game read outside that mesh.
                    if (c.FaceIndex < 0 || c.FaceIndex >= entries[main].Faces || c.EdgeIndex < 0 || c.EdgeIndex >= entries[main].Edges
                        || c.OppositeFaceIndex < 0 || c.OppositeFaceIndex >= entries[target].Faces
                        || c.OppositeEdgeIndex < 0 || c.OppositeEdgeIndex >= entries[target].Edges)
                    {
                        if (!oSet)
                            badLinks++;
                        continue;
                    }
                    connector.NavMeshConnections.Add(new Nva.NavMeshConnection
                    {
                        FaceIndex = c.FaceIndex, EdgeIndex = c.EdgeIndex, OppositeFaceIndex = c.OppositeFaceIndex, OppositeEdgeIndex = c.OppositeEdgeIndex,
                    });
                }
                // Graph connections join the cluster graphs DS3's files have and Elden Ring's don't.
                if (connector.NavMeshConnections.Count == 0)
                    continue;
                list.Connectors.Add(connector);
                if (!oSet)
                {
                    connectors++;
                    links += connector.NavMeshConnections.Count;
                }
            }
        if (droppedConnectors > 0)
            notes.Add($"{droppedConnectors} connections name a navmesh that isn't in the list or has no faces; left out");

        if (badLinks > 0)
            notes.Add($"{badLinks} joined edges name a face or edge their mesh doesn't have; left out");
        int withFaces = entries.Count(e => e.Faces > 0);
        notes.Insert(0, $"{count} navmeshes ({withFaces} with faces, {entries.Sum(e => e.Faces)} faces) in each of the n and o sets, "
                        + $"{connectors} connections ({links} joined edges), {gates} gate nodes per set"
                        + (host is null ? "; no host list, map UID is JortPob's" : ""));
        if (ds3.NodeBanks is { Count: > 0 } banks)
            notes.Add($"{banks.Count} DS3 node banks ({banks.Sum(b => b.Faces.Count)} faces that events close and open) left out");
        if (ds3.LevelConnectors is { Count: > 0 } levels)
            notes.Add($"{levels.Count} DS3 joins to other maps left out");
        return new Result(list, notes);
    }

    /// <summary>
    /// A list in a few lines, for the port-maps log and map-survey: what a later look at either game's files needs
    /// without the files at hand (sections, the first entries' values, how the entries are named).
    /// </summary>
    public static IEnumerable<string> Describe(Nva list)
    {
        var meshes = list.Navmeshes ?? new Nva.NavmeshSection(0);
        int nSet = meshes.Count(e => (uint)e.NameID < OSetOffset);
        yield return $"version {(uint)list.Version}, {meshes.Count} entries (section version {meshes.Version}; {nSet} below NameID 2,000,000,000), "
                     + $"{meshes.Sum(e => e.FaceCount)} faces, {meshes.Count(e => e.FaceCount == 0)} without faces, {meshes.Count(e => e.Unk4C != 0)} with Unk4C set, "
                     + $"{meshes.Sum(e => e.GateNodes.Count)} gate nodes, {meshes.Count(e => e.Rotation.Length() > 1e-4f)} turned, "
                     + $"{meshes.Count(e => e.Position.Length() > 1e-4f)} moved";
        yield return $"sections: face data {list.FaceDatas?.Count.ToString() ?? "none"}, node banks {list.NodeBanks?.Count.ToString() ?? "none"}, "
                     + $"3: {list.Entries3?.Count.ToString() ?? "none"}, connectors {list.Connectors?.Count.ToString() ?? "none"} "
                     + $"({list.Connectors?.Sum(c => c.NavMeshConnections.Count) ?? 0} edges, {list.Connectors?.Sum(c => c.GraphConnections.Count) ?? 0} graph), "
                     + $"level connectors {list.LevelConnectors?.Count.ToString() ?? "none"}, 9: {list.Entries9?.Count.ToString() ?? "none"}, "
                     + $"10: {list.Entries10?.Count.ToString() ?? "none"}, 11: {(list.Entries11 is null ? "none" : string.Join(";", list.Entries11.Select(e => $"{e.Unk00},{e.Unk04},{e.Unk08},{e.Unk0C}")))}, "
                     + $"12: {(list.HasSection12 ? $"version {list.Section12Version}" : "none")}, 13: {list.Entries13?.Count.ToString() ?? "none"}";
        foreach (var e in meshes.Take(2).Concat(meshes.Skip(Math.Max(2, nSet)).Take(1)))
            yield return $"entry NameID {(uint)e.NameID} ModelID {e.ModelID} at {e.Position} w {e.PositionW} turn {e.Rotation} w {e.RotationW} scale {e.Scale} w {e.ScaleW} "
                         + $"faces {e.FaceCount} faceDataIndex {e.FaceDataIndex} Unk3C {e.Unk3C} Unk44 {e.Unk44} Unk4C {e.Unk4C} Unk50 0x{e.Unk50:X} "
                         + $"Unk54 {e.Unk54} Unk58 {e.Unk58} Unk5C {e.Unk5C} gates {e.GateNodes.Count} connected [{string.Join(",", e.ConnectedNavmeshes.Take(4))}]";
        if (list.FaceDatas is { Count: > 0 } faceData)
            yield return $"face data values: {string.Join(", ", faceData.GroupBy(f => (f.Unk00, f.Unk04)).OrderByDescending(g => g.Count()).Take(6).Select(g => $"({g.Key.Unk00},{g.Key.Unk04}) x{g.Count()}"))}";
        if (list.Connectors is { Count: > 0 } connectors)
            yield return $"connector {(uint)connectors[0].MainNameID} -> {(uint)connectors[0].TargetNameID} Unk14 {connectors[0].Unk14} Unk1C {connectors[0].Unk1C}";
        if (list.NodeBanks is { Count: > 0 } banks)
            yield return $"node bank {banks[0].BankID} entity {banks[0].EntityID} Unk0C {banks[0].Unk0C} faces {banks[0].Faces.Count}"
                         + (banks[0].Faces.Count > 0 ? $" (first: face {banks[0].Faces[0].FaceIndex} of {(uint)banks[0].Faces[0].CollisionID})" : "");
    }
}
