using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HKLib.Serialization.hk2018.Binary;
using HKLib.Serialization.hk2018.Binary.Util;
using HavokType = HKLib.Reflection.hk2018.HavokType;

namespace DarkSign.Porter.Havok;

/// <summary>
/// Makes HKLib's binary tagfiles carry their hkRelArray fields in the patch list (PTCH), as Elden Ring's own files do.
///
/// An hkRelArray (the vertices, planes, faces and indices of a convex hull: hknpConvexShape and every shape built on
/// it, hknpConvexPolytopeShape, hknpBoxShape, hknpCapsuleShape...) is written into the DATA section as the number of
/// the item that holds the elements. When the game loads the file in place, it turns every field listed in PTCH into
/// what it means in memory: for an hkRelArray, the element count (u16) and the distance from the field to the
/// elements (u16). HKLib (The12thAvenger/HKLib, MIT) writes the item number but never lists hkRelArray fields in
/// PTCH, so the game reads the item number as the count and 0 as the distance, and finds the "vertices" on top of the
/// shape itself. Build 1.0.410 (doors, levers and crates with DS3 convex hulls, PR #177) crashed loading every DS3
/// map that way: eldenring.exe+0x188434b, the convex shape's set-up after loading, `movaps` from the misaligned
/// address shape + 0x3A + 0 (dumps 24548 and 21180, 2026-11-12; field +0x3A count 15, +0x3C offset 0).
/// Vanilla aeg219_000_l (Elden Ring's door) lists hkRelArray&lt;hkVector4f&gt; (vertices), hkRelArray&lt;hkVector4&gt;
/// (planes), hkRelArray&lt;Face&gt; and hkRelArray&lt;hkUint8&gt; in its PTCH, with the item number in each field.
///
/// HKLib's readers resolve the item number themselves, so reading a written file back never showed the problem.
/// <see cref="Install"/> wraps HKLib's array writer so every hkRelArray it writes is registered as a patch the way
/// HKLib already registers hkArray and pointer fields; it runs once for the whole porter (module initializer), so
/// every stage that writes a tagfile is covered. <see cref="Check"/> reads a written file and reports any hkRelArray
/// field the game could not resolve.
/// </summary>
public static class HavokRelArrays
{
    private static readonly object Gate = new();
    private static Action<HavokBinaryWriter, HavokType, object, BinarySerializeContext>? _original;

    /// <summary>True once HKLib's array writer registers hkRelArray patches.</summary>
    public static bool Installed { get; private set; }

    /// <summary>Why <see cref="Install"/> could not hook HKLib (null when it did).</summary>
    public static string? Failure { get; private set; }

#pragma warning disable CA2255 // the hook must be in place before any stage writes a tagfile
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void InstallOnLoad() => Install();

    /// <summary>Hooks HKLib's binary array writer (idempotent). Returns false, with <see cref="Failure"/>, if HKLib changed shape.</summary>
    public static bool Install()
    {
        lock (Gate)
        {
            if (Installed)
                return true;
            try
            {
                var handlers = typeof(HavokBinarySerializer).Assembly
                    .GetType("HKLib.Serialization.hk2018.Binary.FormatHandlers.FormatHandler")
                    ?.GetField("FormatHandlers", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.GetValue(null) as IDictionary ?? throw new MissingFieldException("FormatHandler.FormatHandlers");
                object entry = handlers[HavokType.TypeKind.Array] ?? throw new MissingFieldException("FormatHandlers[Array]");
                var tuple = entry.GetType();
                var read = (Delegate)tuple.GetField("Item1")!.GetValue(entry)!;
                var write = (Delegate)tuple.GetField("Item2")!.GetValue(entry)!;
                if (write.Method.Name == nameof(Write) && write.Method.DeclaringType == typeof(HavokRelArrays))
                {
                    Installed = true;
                    return true;
                }
                _original = (Action<HavokBinaryWriter, HavokType, object, BinarySerializeContext>)Delegate.CreateDelegate(
                    typeof(Action<HavokBinaryWriter, HavokType, object, BinarySerializeContext>), write.Target, write.Method);
                var hook = Delegate.CreateDelegate(write.GetType(), typeof(HavokRelArrays).GetMethod(nameof(Write), BindingFlags.Static | BindingFlags.NonPublic)!);
                handlers[HavokType.TypeKind.Array] = Activator.CreateInstance(tuple, read, hook);
                Installed = true;
                Failure = null;
            }
            catch (Exception ex)
            {
                Failure = $"{ex.GetType().Name}: {ex.Message}";
            }
            return Installed;
        }
    }

    // Same signature as HKLib's FormatHandler.WriteFormat delegate.
    private static void Write(HavokBinaryWriter writer, HavokType type, object value, BinarySerializeContext context)
    {
        // HKLib writes 0 for an empty or missing hkRelArray; only a field that names an item is a patch (as for hkArray).
        if (type.Name == "hkRelArray" && value is IList { Count: > 0 })
            context.RegisterPatch(type, writer.Position);
        _original!(writer, type, value, context);
    }

    /// <summary>One hkRelArray field of a binary tagfile, as the game would resolve it.</summary>
    public sealed record Field(string Type, long Offset, int Item, long ItemOffset, int Count, bool Patched)
    {
        /// <summary>Distance from the field to its elements, which must fit the u16 the game stores.</summary>
        public long Distance => ItemOffset - Offset;
    }

    /// <summary>The hkRelArray fields a binary tagfile's patch list (PTCH) names, with the item each one points at.</summary>
    public static IReadOnlyList<Field> Fields(byte[] tagfile)
    {
        var file = Tagfile.Parse(tagfile);
        var result = new List<Field>();
        foreach (var (typeIndex, offsets) in file.Patches)
        {
            if (file.TypeNames[typeIndex] != "hkRelArray")
                continue;
            foreach (var offset in offsets)
            {
                int item = (int)BinaryPrimitives.ReadUInt32LittleEndian(tagfile.AsSpan((int)(file.Data + offset), 4));
                var (itemOffset, count) = item > 0 && item < file.Items.Count ? file.Items[item] : (-1, 0);
                result.Add(new Field(file.TypeNames[typeIndex], offset, item, itemOffset, count, true));
            }
        }
        return result;
    }

    /// <summary>
    /// Problems the game would have with a binary tagfile's hkRelArray fields: fewer patched fields than the file's
    /// objects hold (<paramref name="expectedFields"/>, the number of non-empty hkRelArrays written), a field naming no
    /// item, or elements further than a u16 from their field. Empty when the file is fine.
    /// </summary>
    public static List<string> Check(byte[] tagfile, int expectedFields)
    {
        var problems = new List<string>();
        var fields = Fields(tagfile);
        if (fields.Count != expectedFields)
            problems.Add($"{fields.Count} hkRelArray fields in the patch list, the objects hold {expectedFields}");
        foreach (var f in fields)
        {
            if (f.ItemOffset < 0)
                problems.Add($"hkRelArray at DATA+0x{f.Offset:X} names item {f.Item}, which the file lacks");
            else if (f.Distance <= 0 || f.Distance > ushort.MaxValue)
                problems.Add($"hkRelArray at DATA+0x{f.Offset:X}: its {f.Count} elements are {f.Distance} bytes away (the game stores 0-65535)");
        }
        return problems;
    }

    /// <summary>
    /// The parts of a Havok 2018 binary tagfile (TAG0) the checks need: where DATA starts, the type names (TSTR + TNA1),
    /// the items (ITEM: offset relative to DATA and count) and the patch list (PTCH: type index and DATA-relative
    /// offsets). Section layout as HKLib's HavokBinarySerializer reads it.
    /// </summary>
    public sealed class Tagfile
    {
        public long Data { get; private init; }
        public List<string> TypeNames { get; } = ["NULL"];
        public List<(long Offset, int Count)> Items { get; } = [];
        public List<(int Type, List<long> Offsets)> Patches { get; } = [];

        public static Tagfile Parse(byte[] bytes)
        {
            var top = Sections(bytes, 0, bytes.Length);
            var (tagStart, tagEnd) = top.TryGetValue("TAG0", out var t) ? t : throw new InvalidDataException("not a binary tagfile (no TAG0)");
            var tag = Sections(bytes, tagStart, tagEnd);
            var file = new Tagfile { Data = tag["DATA"].Start };
            var type = Sections(bytes, tag["TYPE"].Start, tag["TYPE"].End);
            var strings = new List<string>();
            var (ss, se) = type["TSTR"];
            for (int p = ss; p < se;)
            {
                int z = Array.IndexOf(bytes, (byte)0, p, se - p);
                if (z < 0) z = se;
                strings.Add(System.Text.Encoding.ASCII.GetString(bytes, p, z - p));
                p = z + 1;
            }
            var (ns, _) = type.TryGetValue("TNA1", out var tna1) ? tna1 : type["TNAM"];
            int pos = ns;
            int count = (int)VarUInt(bytes, ref pos);
            for (int i = 1; i < count; i++)
            {
                file.TypeNames.Add(strings[(int)VarUInt(bytes, ref pos)]);
                int parameters = (int)VarUInt(bytes, ref pos);
                for (int j = 0; j < parameters; j++)
                {
                    VarUInt(bytes, ref pos);
                    VarUInt(bytes, ref pos);
                }
            }
            var index = Sections(bytes, tag["INDX"].Start, tag["INDX"].End);
            for (int p = index["ITEM"].Start; p + 12 <= index["ITEM"].End; p += 12)
                file.Items.Add((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4)), (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 8))));
            for (int p = index["PTCH"].Start; p + 8 <= index["PTCH"].End;)
            {
                int typeIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p));
                int n = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4));
                p += 8;
                var offsets = new List<long>(n);
                for (int k = 0; k < n; k++, p += 4)
                    offsets.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p)));
                file.Patches.Add((typeIndex, offsets));
            }
            return file;
        }

        private static Dictionary<string, (int Start, int End)> Sections(byte[] bytes, int start, int end)
        {
            var result = new Dictionary<string, (int, int)>();
            for (int p = start; p + 8 <= end;)
            {
                int length = (int)(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p)) & 0x3FFFFFFF);
                if (length < 8)
                    break;
                result.TryAdd(System.Text.Encoding.ASCII.GetString(bytes, p + 4, 4), (p + 8, p + length));
                p += length;
            }
            return result;
        }

        // Havok's variable-length unsigned integer (HKLib's HavokVarUInt): the high bits of the first byte say the length.
        private static ulong VarUInt(byte[] bytes, ref int pos)
        {
            int length = HavokVarUInt.GetLength(bytes[pos]);
            var raw = bytes.AsSpan(pos, length).ToArray();
            pos += length;
            return HavokVarUInt.ToUInt64(raw);
        }
    }
}
