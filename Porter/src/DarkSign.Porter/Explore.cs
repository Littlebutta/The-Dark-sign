using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>Read-only commands that summarize game files, so porting rules can be designed from short reports.</summary>
public static class Explore
{
    /// <summary>Lists the FMG text files inside a msgbnd with their binder IDs and entry counts.</summary>
    public static int FmgList(PorterContext ctx, PorterGame game, string msgbndPath)
    {
        using var archives = ctx.OpenArchives(game);
        var bnd = BND4.Read(archives.Read(msgbndPath));
        foreach (var file in bnd.Files)
        {
            var fmg = FMG.Read(file.Bytes);
            var sample = fmg.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Text));
            Console.WriteLine($"{file.ID}\t{Leaf(file.Name)}\t{fmg.Entries.Count}\t{sample?.ID}: {Short(sample?.Text)}");
        }
        return 0;
    }

    /// <summary>Summarizes every FLVER and TPF in a binder (e.g. a weapon's partsbnd).</summary>
    public static int FlverInfo(PorterContext ctx, PorterGame game, string binderPath)
    {
        using var archives = ctx.OpenArchives(game);
        var bnd = BND4.Read(archives.Read(binderPath));
        foreach (var file in bnd.Files)
        {
            Console.WriteLine($"file {file.ID} {Leaf(file.Name)} ({file.Bytes.Length} bytes)");
            if (FLVER2.Is(file.Bytes))
            {
                var flver = FLVER2.Read(file.Bytes);
                Console.WriteLine($"  flver version 0x{flver.Header.Version:X} unicode={flver.Header.Unicode} nodes={flver.Nodes.Count} meshes={flver.Meshes.Count} gxlists={flver.GXLists.Count} dummies={flver.Dummies.Count}");
                foreach (var m in flver.Materials)
                {
                    Console.WriteLine($"  material '{m.Name}' mtd={m.MTD} gx={m.GXIndex} index={m.Index}");
                    foreach (var t in m.Textures)
                        Console.WriteLine($"    tex {t.ParamName} = {t.Path} scale={t.TilingScale}");
                }
                foreach (var gx in flver.GXLists)
                    Console.WriteLine($"  gxlist [{string.Join(", ", gx.Select(i => $"{i.ID}:{i.Unk04}:{i.Data.Length}"))}]");
                for (int i = 0; i < flver.BufferLayouts.Count; i++)
                    Console.WriteLine($"  layout {i}: {string.Join(" ", flver.BufferLayouts[i].Select(l => $"{l.Semantic}{l.Index}:{l.Type}"))}");
                foreach (var mesh in flver.Meshes)
                    Console.WriteLine($"  mesh mat={mesh.MaterialIndex} verts={mesh.Vertices.Count} buffers=[{string.Join(",", mesh.VertexBuffers.Select(b => b.LayoutIndex))}] facesets={mesh.FaceSets.Count} dynamic={mesh.Dynamic}");
            }
            else if (TPF.Is(file.Bytes))
            {
                var tpf = TPF.Read(file.Bytes);
                Console.WriteLine($"  tpf platform={tpf.Platform} encoding={tpf.Encoding} flag2={tpf.Flag2}");
                foreach (var t in tpf.Textures)
                    Console.WriteLine($"    {t.Name} format={t.Format} type={t.Type} mips={t.Mipmaps} flags1={t.Flags1} bytes={t.Bytes.Length}");
            }
        }
        return 0;
    }

    /// <summary>
    /// Finds talk-script commands whose two arguments are a ShopLineupParam range (e.g. 100500, 100599),
    /// which is how merchants open their shops.
    /// </summary>
    public static int Shops(PorterContext ctx, PorterGame game)
    {
        using var archives = ctx.OpenArchives(game);
        var shopIds = ctx.LoadParams(game).Get("ShopLineupParam")!.Rows.Select(r => r.ID).ToHashSet();
        var talkFiles = archives.Entries.Where(e => e.Path?.StartsWith("/script/talk/") == true && e.Path.EndsWith(".talkesdbnd.dcx")).OrderBy(e => e.Path);
        foreach (var entry in talkFiles)
        {
            var bnd = BND4.Read(DCX.Decompress(archives.ReadRaw(entry)));
            foreach (var file in bnd.Files)
            {
                var esd = ESD.Read(file.Bytes);
                var found = new SortedSet<string>();
                foreach (var cmd in AllCommands(esd))
                {
                    if (cmd.Arguments.Count != 2)
                        continue;
                    var a = EsdExpr.ReadInt(cmd.Arguments[0]);
                    var b = EsdExpr.ReadInt(cmd.Arguments[1]);
                    if (a is int start && b is int end && end >= start && end - start < 1000 && shopIds.Contains(start))
                        found.Add($"cmd {cmd.CommandBank}:{cmd.CommandID} ({start}, {end})");
                }
                foreach (var f in found)
                    Console.WriteLine($"{Leaf(entry.Path!)}\t{Leaf(file.Name)}\t{f}");
            }
        }
        return 0;
    }

    public static IEnumerable<ESD.CommandCall> AllCommands(ESD esd)
    {
        foreach (var state in esd.StateGroups.Values.SelectMany(g => g.Values))
        {
            foreach (var c in state.EntryCommands.Concat(state.ExitCommands).Concat(state.WhileCommands))
                yield return c;
            foreach (var c in state.Conditions.SelectMany(Flatten).SelectMany(c => c.PassCommands))
                yield return c;
        }
    }

    private static IEnumerable<ESD.Condition> Flatten(ESD.Condition c) => c.Subconditions.SelectMany(Flatten).Prepend(c);

    private static string Leaf(string path) => path.Replace('\\', '/').Split('/')[^1];
    private static string Short(string? s) => s is null ? "" : (s.Length > 40 ? s[..40] + "…" : s).Replace('\n', ' ');
}

/// <summary>Reads and writes the constant integer expressions used as ESD command arguments.</summary>
public static class EsdExpr
{
    // An ESD expression is bytecode ending in 0xA1. A lone integer is either one byte (0x00-0x7F, value - 64)
    // or 0x82 followed by a little-endian int32.
    public static int? ReadInt(byte[] expr)
    {
        if (expr.Length == 2 && expr[0] <= 0x7F && expr[1] == 0xA1)
            return expr[0] - 64;
        if (expr.Length == 6 && expr[0] == 0x82 && expr[5] == 0xA1)
            return BitConverter.ToInt32(expr, 1);
        return null;
    }

    public static byte[] WriteInt(int value)
    {
        if (value >= -64 && value <= 63)
            return [(byte)(value + 64), 0xA1];
        var bytes = new byte[6];
        bytes[0] = 0x82;
        BitConverter.GetBytes(value).CopyTo(bytes, 1);
        bytes[5] = 0xA1;
        return bytes;
    }
}
