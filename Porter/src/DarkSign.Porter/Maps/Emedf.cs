using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkSign.Porter.Maps;

/// <summary>
/// One game's event instruction definitions, as DarkScript3 publishes them (an .emedf.json file: instruction banks,
/// each instruction's name and its arguments' names and types). Gives every instruction's argument layout, so
/// an instruction's packed bytes can be read argument by argument.
/// </summary>
public sealed class Emedf
{
    public sealed record Arg(string Name, int Type, long Default, int Offset)
    {
        public int Size => SizeOf(Type);
    }

    public sealed record Instr(int Bank, int Id, string Name, IReadOnlyList<Arg> Args, int Length)
    {
        public override string ToString() => $"{Bank}[{Id:D2}] {Name}";

        /// <summary>The argument that starts at a byte offset, or null.</summary>
        public int ArgAt(long offset)
        {
            for (int i = 0; i < Args.Count; i++)
                if (Args[i].Offset == offset)
                    return i;
            return -1;
        }
    }

    private readonly Dictionary<(int, int), Instr> _byId = new();
    private readonly Dictionary<string, Instr> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Instr> All => _byId.Values;
    public Instr? Get(int bank, int id) => _byId.GetValueOrDefault((bank, id));
    public Instr? Get(string name) => _byName.GetValueOrDefault(name);

    // EMEDF argument types: 0 u8, 1 u16, 2 u32, 3 s8, 4 s16, 5 s32, 6 f32, 8 string offset (u32).
    public static int SizeOf(int type) => type switch { 0 or 3 => 1, 1 or 4 => 2, _ => 4 };

    public static Emedf Parse(string json)
    {
        // DarkScript3's files carry // comments and trailing commas.
        var text = string.Join('\n', json.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        text = Regex.Replace(text, @",(\s*[\]}])", "$1");
        using var doc = JsonDocument.Parse(text);
        var e = new Emedf();
        foreach (var cls in doc.RootElement.GetProperty("main_classes").EnumerateArray())
        {
            int bank = cls.GetProperty("index").GetInt32();
            foreach (var ins in cls.GetProperty("instrs").EnumerateArray())
            {
                int id = ins.GetProperty("index").GetInt32();
                var name = ins.GetProperty("name").GetString() ?? $"{bank}[{id}]";
                var args = new List<Arg>();
                int offset = 0;
                foreach (var a in ins.GetProperty("args").EnumerateArray())
                {
                    int type = a.GetProperty("type").GetInt32();
                    int size = SizeOf(type);
                    offset = (offset + size - 1) / size * size;
                    long def = a.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Number
                        ? (d.TryGetInt64(out var l) ? l : (long)d.GetDouble()) : 0;
                    args.Add(new Arg(a.GetProperty("name").GetString() ?? "", type, def, offset));
                    offset += size;
                }
                var instr = new Instr(bank, id, name, args, (offset + 3) / 4 * 4);
                e._byId[(bank, id)] = instr;
                e._byName.TryAdd(name, instr);
            }
        }
        return e;
    }

    /// <summary>Reads one argument's value (as a long, floats as their bit pattern) from packed instruction bytes.</summary>
    public static long Read(byte[] data, Arg a)
    {
        if (a.Offset + a.Size > data.Length)
            return a.Default;
        return a.Type switch
        {
            0 => data[a.Offset],
            1 => BitConverter.ToUInt16(data, a.Offset),
            2 or 8 => BitConverter.ToUInt32(data, a.Offset),
            3 => (sbyte)data[a.Offset],
            4 => BitConverter.ToInt16(data, a.Offset),
            5 => BitConverter.ToInt32(data, a.Offset),
            6 => BitConverter.ToInt32(data, a.Offset), // float bits
            _ => 0,
        };
    }

    public static void Write(byte[] data, Arg a, long value)
    {
        switch (a.Size)
        {
            case 1: data[a.Offset] = (byte)value; break;
            case 2: BitConverter.TryWriteBytes(data.AsSpan(a.Offset), (short)value); break;
            default: BitConverter.TryWriteBytes(data.AsSpan(a.Offset), (int)value); break;
        }
    }

    /// <summary>The default packed bytes of an instruction (every argument at its default value).</summary>
    public static byte[] Defaults(Instr instr)
    {
        var data = new byte[instr.Length];
        foreach (var a in instr.Args)
            Write(data, a, a.Type == 6 ? BitConverter.SingleToInt32Bits(a.Default) : a.Default);
        return data;
    }
}
