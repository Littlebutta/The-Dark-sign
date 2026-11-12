using System.Text;

namespace DarkSign.Porter.Enemies;

/// <summary>
/// A compiled Havok Script (HKS) AI file as DS3 and Elden Ring ship them in their .luabnd binders. Both games write
/// the same layout, byte for byte in the header (checked on 2026-11-12 against DS3's m39 223000_battle.lua and Elden
/// Ring's aicommon goal_list.lua), so a DS3 script loads in Elden Ring as it is; only the globals it names have to
/// exist there. Layout, learned from those files (it is the stripped FromSoftware variant of HKS, not hksc's):
/// <list type="bullet">
/// <item>header: "\x1bLua", version 0x50 and 17 more fixed bytes (the same in both games);</item>
/// <item>function: u64 name length + name (main "=(none)", nested ones empty), u32 line defined, u8, u8 parameter
/// count, u8, u8 frame size, 12 more bytes, u32 constant count + constants (u8 type: 0 nil, 1 boolean (u8),
/// 3 number (f64), 4 string (u64 length with the terminating zero, bytes)), u32 nested function count + the nested
/// functions, u32 instruction count + 32-bit instructions.</item>
/// </list>
/// The reader is only as deep as the port needs: names, string constants, and adding numeric globals to a flat list
/// of LOADK / SETGLOBAL pairs (Elden Ring's goal_list.lua is nothing else: "GOAL_X = 123000" for each goal).
/// Community reference for the format family: katalash's DSLuaDecompiler (HKS reader for DS3, Sekiro and Elden Ring).
/// </summary>
public sealed class HksChunk
{
    public const int HeaderLength = 22;
    private static readonly byte[] Magic = [0x1B, (byte)'L', (byte)'u', (byte)'a', 0x50];

    public byte[] Header = [];
    public Function Main = new();

    public sealed class Function
    {
        public string Name = "";
        public byte[] Fixed = new byte[20];
        public List<Constant> Constants = [];
        public List<Function> Functions = [];
        public List<uint> Instructions = [];

        public IEnumerable<Function> All() => new[] { this }.Concat(Functions.SelectMany(f => f.All()));
    }

    public readonly record struct Constant(byte Type, object? Value)
    {
        public static Constant Text(string s) => new(4, s);
        public static Constant Number(double d) => new(3, d);
        public string? AsString => Type == 4 ? (string?)Value : null;
        public double? AsNumber => Type == 3 ? (double?)Value : null;
    }

    public static bool Is(byte[] bytes) => bytes.Length > HeaderLength && bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    public static HksChunk Read(byte[] bytes)
    {
        if (!Is(bytes))
            throw new InvalidDataException("not a compiled HKS file (no \\x1bLuaP header)");
        var chunk = new HksChunk { Header = bytes[..HeaderLength] };
        int pos = HeaderLength;
        chunk.Main = ReadFunction(bytes, ref pos);
        if (pos != bytes.Length)
            throw new InvalidDataException($"HKS file has {bytes.Length - pos} bytes after the main function");
        return chunk;
    }

    private static Function ReadFunction(byte[] b, ref int pos)
    {
        var f = new Function();
        long nameLength = BitConverter.ToInt64(b, pos);
        pos += 8;
        if (nameLength < 0 || nameLength > 4096)
            throw new InvalidDataException($"HKS function name length {nameLength} at {pos - 8}");
        f.Name = nameLength == 0 ? "" : Encoding.Latin1.GetString(b, pos, (int)nameLength - 1);
        pos += (int)nameLength;
        f.Fixed = b[pos..(pos + 20)];
        pos += 20;
        int constants = Count(b, ref pos, 1_000_000, "constants");
        for (int i = 0; i < constants; i++)
        {
            byte type = b[pos++];
            switch (type)
            {
                case 0:
                    f.Constants.Add(new Constant(0, null));
                    break;
                case 1:
                    f.Constants.Add(new Constant(1, b[pos++] != 0));
                    break;
                case 3:
                    f.Constants.Add(new Constant(3, BitConverter.ToDouble(b, pos)));
                    pos += 8;
                    break;
                case 4:
                    long length = BitConverter.ToInt64(b, pos);
                    pos += 8;
                    if (length < 1 || length > 1 << 20)
                        throw new InvalidDataException($"HKS string length {length} at {pos - 8}");
                    f.Constants.Add(Constant.Text(Encoding.Latin1.GetString(b, pos, (int)length - 1)));
                    pos += (int)length;
                    break;
                default:
                    throw new InvalidDataException($"HKS constant type {type} at {pos - 1} is not read by the port");
            }
        }
        int functions = Count(b, ref pos, 100_000, "functions");
        for (int i = 0; i < functions; i++)
            f.Functions.Add(ReadFunction(b, ref pos));
        int instructions = Count(b, ref pos, 10_000_000, "instructions");
        for (int i = 0; i < instructions; i++, pos += 4)
            f.Instructions.Add(BitConverter.ToUInt32(b, pos));
        return f;
    }

    private static int Count(byte[] b, ref int pos, int max, string what)
    {
        int n = BitConverter.ToInt32(b, pos);
        pos += 4;
        if (n < 0 || n > max)
            throw new InvalidDataException($"HKS {what} count {n} at {pos - 4}");
        return n;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Header);
        WriteFunction(w, Main);
        return ms.ToArray();
    }

    private static void WriteFunction(BinaryWriter w, Function f)
    {
        if (f.Name.Length == 0)
            w.Write(0L);
        else
        {
            w.Write((long)f.Name.Length + 1);
            w.Write(Encoding.Latin1.GetBytes(f.Name));
            w.Write((byte)0);
        }
        w.Write(f.Fixed);
        w.Write(f.Constants.Count);
        foreach (var c in f.Constants)
        {
            w.Write(c.Type);
            switch (c.Type)
            {
                case 1: w.Write((byte)((bool)c.Value! ? 1 : 0)); break;
                case 3: w.Write((double)c.Value!); break;
                case 4:
                    var s = Encoding.Latin1.GetBytes((string)c.Value!);
                    w.Write((long)s.Length + 1);
                    w.Write(s);
                    w.Write((byte)0);
                    break;
            }
        }
        w.Write(f.Functions.Count);
        foreach (var child in f.Functions)
            WriteFunction(w, child);
        w.Write(f.Instructions.Count);
        foreach (var i in f.Instructions)
            w.Write(i);
    }

    public static int Op(uint instruction) => (int)(instruction & 0x3F);
    public static int Bx(uint instruction) => (int)((instruction >> 6) & 0x3FFFF);
    public static int A(uint instruction) => (int)(instruction >> 24);

    /// <summary>The globals the file assigns (SETGLOBAL targets in every function).</summary>
    public IEnumerable<string> AssignedGlobals() => Main.All()
        .SelectMany(f => f.Instructions.Where(i => Op(i) == 7).Select(i => Bx(i) < f.Constants.Count ? f.Constants[Bx(i)].AsString : null))
        .OfType<string>().Distinct(StringComparer.Ordinal);

    /// <summary>The globals the file reads (GETGLOBAL sources in every function).</summary>
    public IEnumerable<string> ReadGlobals() => Main.All()
        .SelectMany(f => f.Instructions.Where(i => Op(i) == 5).Select(i => Bx(i) < f.Constants.Count ? f.Constants[Bx(i)].AsString : null))
        .OfType<string>().Distinct(StringComparer.Ordinal);

    /// <summary>The globals the main chunk assigns when the file loads (functions and constants it defines).</summary>
    public IEnumerable<string> DefinedGlobals() => Main.Instructions.Where(i => Op(i) == 7)
        .Select(i => Bx(i) < Main.Constants.Count ? Main.Constants[Bx(i)].AsString : null).OfType<string>().Distinct(StringComparer.Ordinal);

    /// <summary>CLOSURE (opcode 34): A = register, Bx = index of the nested function.</summary>
    public const int ClosureOp = 34;

    /// <summary>
    /// The functions the main chunk defines as globals ("function Name(...)": CLOSURE Ra, F(n) then SETGLOBAL Ra, K(name)),
    /// by name; the last definition wins, as when the file runs.
    /// </summary>
    public Dictionary<string, Function> GlobalFunctions()
    {
        var result = new Dictionary<string, Function>(StringComparer.Ordinal);
        var code = Main.Instructions;
        var inRegister = new Dictionary<int, Function>();
        foreach (var i in code)
        {
            if (Op(i) == ClosureOp && Bx(i) < Main.Functions.Count)
                inRegister[A(i)] = Main.Functions[Bx(i)];
            else if (Op(i) == 7 && Bx(i) < Main.Constants.Count && Main.Constants[Bx(i)].AsString is { } name)
            {
                if (inRegister.TryGetValue(A(i), out var f))
                    result[name] = f;
                else
                    result.Remove(name);
            }
        }
        return result;
    }

    /// <summary>Whether two functions are the same code: instructions, constants and nested functions (names aside).</summary>
    public static bool SameCode(Function a, Function b) =>
        a.Fixed.AsSpan().SequenceEqual(b.Fixed) && a.Instructions.SequenceEqual(b.Instructions) && a.Constants.SequenceEqual(b.Constants)
        && a.Functions.Count == b.Functions.Count && a.Functions.Zip(b.Functions).All(p => SameCode(p.First, p.Second));

    /// <summary>The globals a function and the functions nested in it read.</summary>
    public static IEnumerable<string> ReadGlobals(Function function) => function.All()
        .SelectMany(f => f.Instructions.Where(i => Op(i) == 5).Select(i => Bx(i) < f.Constants.Count ? f.Constants[Bx(i)].AsString : null))
        .OfType<string>().Distinct(StringComparer.Ordinal);

    /// <summary>Upvalue count of a function (byte 4 of its fixed block; 0 for every top-level function in both games' AI files).</summary>
    public static int Upvalues(Function f) => f.Fixed[4];

    /// <summary>
    /// A chunk that only defines the given top-level functions, in the layout both games' common files use for their
    /// main chunk: CLOSURE R0, F(n); SETGLOBAL R0, K(n) for each, then RETURN. The header and the main function's fixed
    /// block are the source file's. Functions with upvalues can't be lifted out and throw.
    /// </summary>
    public static HksChunk Lifted(HksChunk source, IReadOnlyList<(string Name, Function Function)> functions)
    {
        var main = new Function { Name = source.Main.Name, Fixed = (byte[])source.Main.Fixed.Clone() };
        for (int n = 0; n < functions.Count; n++)
        {
            var (name, f) = functions[n];
            if (Upvalues(f) != 0)
                throw new InvalidDataException($"{name} uses upvalues and can't be lifted out of its file");
            main.Constants.Add(Constant.Text(name));
            main.Functions.Add(f);
            main.Instructions.Add((uint)ClosureOp | ((uint)n << 6));
            main.Instructions.Add(SetGlobal(n));
        }
        main.Instructions.Add(Return);
        return new HksChunk { Header = (byte[])source.Header.Clone(), Main = main };
    }

    /// <summary>Every string constant in every function.</summary>
    public IEnumerable<string> Strings() => Main.All().SelectMany(f => f.Constants).Select(c => c.AsString).OfType<string>();

    // ---- goal_list.lua: a flat list of "GOAL_X = number" ----

    /// <summary>
    /// LOADK R0, K(n) as goal_list writes it. Instructions are 32-bit: opcode in bits 0-5, an 18-bit constant index
    /// (Bx) in bits 6-23, register A in bits 24-31 (read off DS3's 223000_battle main chunk: GETGLOBAL R1 K1 is
    /// 0x01000045, LOADK R2 K2 is 0x02000081).
    /// </summary>
    public static uint LoadK(int constant) => 1u | ((uint)constant << 6);

    /// <summary>SETGLOBAL R0, K(n) (opcode 7).</summary>
    public static uint SetGlobal(int constant) => 7u | ((uint)constant << 6);

    /// <summary>RETURN with no values, the last instruction of every main chunk in both games' AI files.</summary>
    public const uint Return = 0x801B;

    /// <summary>
    /// The numeric globals a goal list assigns (name -> value), read from its LOADK / SETGLOBAL pairs. Throws when the
    /// main function is anything else, so a changed file format is noticed instead of being half-read.
    /// </summary>
    public Dictionary<string, double> NumericGlobals()
    {
        var f = Main;
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var code = f.Instructions;
        if (code.Count == 0 || code[^1] != Return || code.Count % 2 != 1 || f.Functions.Count > 0)
            throw new InvalidDataException("not a flat list of global assignments (goal_list layout)");
        for (int i = 0; i + 1 < code.Count; i += 2)
        {
            if (Op(code[i]) != 1 || Op(code[i + 1]) != 7 || A(code[i]) != A(code[i + 1]))
                throw new InvalidDataException($"instruction {i} is not LOADK + SETGLOBAL");
            int k = Bx(code[i]), g = Bx(code[i + 1]);
            if (k >= f.Constants.Count || g >= f.Constants.Count || f.Constants[k].AsNumber is not { } value || f.Constants[g].AsString is not { } name)
                throw new InvalidDataException($"instruction {i} names a constant the list doesn't have");
            result[name] = value;
        }
        return result;
    }

    /// <summary>Adds "name = value" assignments in front of the final RETURN, in the goal list's own pattern.</summary>
    public int AddNumericGlobals(IEnumerable<(string Name, double Value)> globals)
    {
        var have = NumericGlobals();
        var f = Main;
        var tail = f.Instructions[^1];
        f.Instructions.RemoveAt(f.Instructions.Count - 1);
        int added = 0;
        foreach (var (name, value) in globals)
        {
            if (have.ContainsKey(name))
                continue;
            have[name] = value;
            int g = f.Constants.Count;
            f.Constants.Add(Constant.Text(name));
            f.Constants.Add(Constant.Number(value));
            if (g + 1 >= 1 << 18)
                throw new InvalidDataException("the goal list would hold more constants than LOADK can reach");
            f.Instructions.Add(LoadK(g + 1));
            f.Instructions.Add(SetGlobal(g));
            added++;
        }
        f.Instructions.Add(tail);
        return added;
    }
}
