using System.Buffers.Binary;
using System.Text;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// A character's compiled behaviour script (action\script\cXXXX.hks) as DS3 and Elden Ring ship it: Havok Script
/// format 0x51/0x0E, big-endian, not the 0x50 AI format <see cref="HksChunk"/> reads. The reader keeps every byte it
/// doesn't need as it is, so a file read and written unchanged comes back byte for byte (checked on Elden Ring's
/// c0000.hks, 1.27 MB, and c3061.hks, and on the port's c7006 and c7112 clones).
/// <list type="bullet">
/// <item>header: "\x1bLuaQ", 0x0E and 8 more bytes, then a big-endian type count and (id, name length, name) per type;</item>
/// <item>function: 17 bytes (u32, u32 parameter count, u8, u32 slot count, u32), u32 instruction count, padding with
/// '_' to a multiple of 4 from the start of the file, the instructions, u32 constant count + constants (u8 type: 0 nil,
/// 1 boolean (u8), 3 number (f32), 4 string (u64 length with the terminating zero, bytes)), then two u32, u32 local
/// count, u32 upvalue count, two u32 (lines), the source path and the function's name (strings as above), a u32 line
/// number per instruction, the locals (name, u32 start, u32 end), the upvalue names, then u32 nested function count +
/// the nested functions;</item>
/// <item>12 bytes after the main function.</item>
/// </list>
/// Layout after katalash's DSLuaDecompiler (MIT, LuaFile.cs, "Lua51Hks", which credits horkrux for the function
/// header); the '_' padding and the header's type table length were read off the files above.
/// </summary>
public sealed class BehaviourScript
{
    private static readonly byte[] Magic = [0x1B, (byte)'L', (byte)'u', (byte)'a', 0x51, 0x0E];

    public byte[] Header = [];
    public byte[] Footer = [];
    public Function Main = new();

    public sealed class Function
    {
        /// <summary>The name the compiler recorded ("Event20003_onUpdate"; the main chunk has none).</summary>
        public string? Name;
        public byte[] Head = [];
        public uint Instructions;
        public byte[] Code = [];
        public List<Constant> Constants = [];
        /// <summary>Debug data from the two u32 after the constants up to the nested function count, kept as read.</summary>
        public byte[] Tail = [];
        public List<Function> Functions = [];

        public IEnumerable<Function> All() => new[] { this }.Concat(Functions.SelectMany(f => f.All()));
    }

    public sealed class Constant(byte type, byte[] raw, string? text)
    {
        public byte Type { get; } = type;
        /// <summary>The value's bytes as read, for nil, boolean and number constants.</summary>
        public byte[] Raw { get; } = raw;
        public string? Text { get; set; } = text;
    }

    public static bool Is(byte[] bytes) => bytes.Length > 32 && bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    public static BehaviourScript Read(byte[] bytes)
    {
        if (!Is(bytes))
            throw new InvalidDataException("not a compiled behaviour script (no \\x1bLuaQ 0x0E header)");
        int pos = 14;
        int types = U32(bytes, ref pos, 64, "type");
        for (int i = 0; i < types; i++)
        {
            pos += 4;
            int length = U32(bytes, ref pos, 256, "type name length");
            pos += length;
        }
        var script = new BehaviourScript { Header = bytes[..pos] };
        script.Main = ReadFunction(bytes, ref pos, 0);
        script.Footer = bytes[pos..];
        if (script.Footer.Length > 64)
            throw new InvalidDataException($"behaviour script has {script.Footer.Length} bytes after the main function");
        return script;
    }

    private static Function ReadFunction(byte[] b, ref int pos, int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("behaviour script functions nested too deep");
        var f = new Function { Head = Slice(b, pos, 17) };
        pos += 17;
        f.Instructions = (uint)U32(b, ref pos, 10_000_000, "instruction");
        pos += (4 - pos % 4) % 4;
        f.Code = Slice(b, pos, checked((int)f.Instructions * 4));
        pos += f.Code.Length;
        int constants = U32(b, ref pos, 1_000_000, "constant");
        for (int i = 0; i < constants; i++)
        {
            byte type = b[pos++];
            f.Constants.Add(type switch
            {
                0 => new Constant(0, [], null),
                1 => new Constant(1, Slice(b, pos++, 1), null),
                3 => new Constant(3, Slice(b, (pos += 4) - 4, 4), null),
                4 => new Constant(4, [], ReadString(b, ref pos) ?? throw new InvalidDataException($"empty string constant at {pos}")),
                _ => throw new InvalidDataException($"behaviour script constant type {type} at {pos - 1}"),
            });
        }
        int tail = pos;
        pos += 8;
        int locals = U32(b, ref pos, 1_000_000, "local");
        int upvalues = U32(b, ref pos, 1_000_000, "upvalue");
        pos += 8;
        ReadString(b, ref pos);
        f.Name = ReadString(b, ref pos);
        pos += checked((int)f.Instructions * 4);
        for (int i = 0; i < locals; i++)
        {
            ReadString(b, ref pos);
            pos += 8;
        }
        for (int i = 0; i < upvalues; i++)
            ReadString(b, ref pos);
        f.Tail = Slice(b, tail, pos - tail);
        int children = U32(b, ref pos, 100_000, "function");
        for (int i = 0; i < children; i++)
            f.Functions.Add(ReadFunction(b, ref pos, depth + 1));
        return f;
    }

    public byte[] Write()
    {
        var o = new List<byte>(Header.Length * 4 + 4096);
        o.AddRange(Header);
        WriteFunction(o, Main);
        o.AddRange(Footer);
        return [.. o];
    }

    private static void WriteFunction(List<byte> o, Function f)
    {
        o.AddRange(f.Head);
        AddU32(o, f.Instructions);
        while (o.Count % 4 != 0)
            o.Add((byte)'_');
        o.AddRange(f.Code);
        AddU32(o, (uint)f.Constants.Count);
        foreach (var c in f.Constants)
        {
            o.Add(c.Type);
            if (c.Type == 4)
            {
                var s = Encoding.UTF8.GetBytes(c.Text!);
                AddU64(o, (ulong)s.Length + 1);
                o.AddRange(s);
                o.Add(0);
            }
            else
                o.AddRange(c.Raw);
        }
        o.AddRange(f.Tail);
        AddU32(o, (uint)f.Functions.Count);
        foreach (var child in f.Functions)
            WriteFunction(o, child);
    }

    /// <summary>Every named function, by name (the first of a name wins).</summary>
    public Dictionary<string, Function> Named()
    {
        var named = new Dictionary<string, Function>(StringComparer.Ordinal);
        foreach (var f in Main.All())
            if (f.Name is { Length: > 0 } name)
                named.TryAdd(name, f);
        return named;
    }

    private static string? ReadString(byte[] b, ref int pos)
    {
        if (pos + 8 > b.Length)
            throw new InvalidDataException($"behaviour script ends inside a string length at {pos}");
        ulong length = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(pos));
        pos += 8;
        if (length == 0)
            return null;
        if (length > 1 << 20 || pos + (int)length > b.Length)
            throw new InvalidDataException($"behaviour script string length {length} at {pos - 8}");
        var s = Encoding.UTF8.GetString(b, pos, (int)length - 1);
        pos += (int)length;
        return s;
    }

    private static int U32(byte[] b, ref int pos, int max, string what)
    {
        if (pos + 4 > b.Length)
            throw new InvalidDataException($"behaviour script ends inside a {what} count at {pos}");
        uint n = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos));
        pos += 4;
        if (n > max)
            throw new InvalidDataException($"behaviour script {what} count {n} at {pos - 4}");
        return (int)n;
    }

    private static byte[] Slice(byte[] b, int pos, int length)
    {
        if (pos < 0 || pos + length > b.Length)
            throw new InvalidDataException($"behaviour script ends at {b.Length}, wanted {length} bytes at {pos}");
        return b[pos..(pos + length)];
    }

    private static void AddU32(List<byte> o, uint v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(s, v);
        o.AddRange(s.ToArray());
    }

    private static void AddU64(List<byte> o, ulong v)
    {
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(s, v);
        o.AddRange(s.ToArray());
    }
}
