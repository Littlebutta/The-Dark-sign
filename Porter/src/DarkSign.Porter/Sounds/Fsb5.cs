using System.Text;

namespace DarkSign.Porter;

/// <summary>
/// Reads FMOD sound banks (FSB5, DS3's .fsb) and decodes their Vorbis samples to 16-bit PCM. FMOD stores each sample
/// as bare Vorbis packets (u16 size + bytes) and drops the three Vorbis headers, keeping only a CRC32 of the setup
/// header. The setup headers come from Sounds/fsb5-vorbis-setups.bin (made by Porter/tools/gen_fsb5_vorbis_setups.py);
/// the identification and comment headers are rebuilt, everything is wrapped in an Ogg stream and decoded by NVorbis.
/// </summary>
public sealed class Fsb5
{
    public sealed record Sample(string Name, int Channels, int Rate, int Frames, int Codec, uint SetupCrc, byte[] Data);

    public IReadOnlyList<Sample> Samples { get; }

    private const int CodecVorbis = 15;
    private static readonly int[] Rates = [0, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000];

    private Fsb5(List<Sample> samples) => Samples = samples;

    public static bool Is(byte[] bytes) => bytes.Length >= 0x3C && bytes.AsSpan(0, 4).SequenceEqual("FSB5"u8);

    public static Fsb5 Read(byte[] bytes, Func<string, bool>? keep = null)
    {
        if (!Is(bytes))
            throw new InvalidDataException("not an FSB5 file");
        int version = BitConverter.ToInt32(bytes, 4), count = BitConverter.ToInt32(bytes, 8);
        int headersSize = BitConverter.ToInt32(bytes, 12), namesSize = BitConverter.ToInt32(bytes, 16), dataSize = BitConverter.ToInt32(bytes, 20);
        int codec = BitConverter.ToInt32(bytes, 24);
        int baseSize = version == 0 ? 0x40 : 0x3C;
        int namesAt = baseSize + headersSize, dataAt = namesAt + namesSize;

        var heads = new List<(int Channels, int Rate, int Frames, int Offset, uint Crc)>();
        int p = baseSize;
        for (int i = 0; i < count; i++)
        {
            ulong raw = BitConverter.ToUInt64(bytes, p);
            p += 8;
            bool more = (raw & 1) != 0;
            int rate = Rates[(int)(raw >> 1) & 0xF], channels = (int)((raw >> 5) & 1) + 1;
            int offset = (int)((raw >> 6) & 0xFFFFFFF) * 16, frames = (int)((raw >> 34) & 0x3FFFFFFF);
            uint crc = 0;
            while (more)
            {
                uint chunk = BitConverter.ToUInt32(bytes, p);
                more = (chunk & 1) != 0;
                int size = (int)((chunk >> 1) & 0xFFFFFF), type = (int)(chunk >> 25);
                int body = p + 4;
                switch (type)
                {
                    case 1: channels = bytes[body]; break;
                    case 2: rate = BitConverter.ToInt32(bytes, body); break;
                    case 11: crc = BitConverter.ToUInt32(bytes, body); break;
                }
                p = body + size;
            }
            heads.Add((channels, rate, frames, offset, crc));
        }

        var samples = new List<Sample>();
        for (int i = 0; i < count; i++)
        {
            string name = $"#{i}";
            if (namesSize > 0)
            {
                int start = namesAt + BitConverter.ToInt32(bytes, namesAt + 4 * i), end = start;
                while (end < bytes.Length && bytes[end] != 0) end++;
                name = Encoding.ASCII.GetString(bytes, start, end - start);
            }
            if (keep is not null && !keep(name))
                continue;
            int from = dataAt + heads[i].Offset;
            int to = i + 1 < count ? dataAt + heads[i + 1].Offset : Math.Min(bytes.Length, dataAt + dataSize);
            samples.Add(new Sample(name, heads[i].Channels, heads[i].Rate, heads[i].Frames, codec, heads[i].Crc, bytes[from..to]));
        }
        return new Fsb5(samples);
    }

    /// <summary>Interleaved 16-bit PCM of a Vorbis sample.</summary>
    public static short[] DecodePcm(Sample sample)
    {
        if (sample.Codec != CodecVorbis)
            throw new NotSupportedException($"FSB codec {sample.Codec} (only Vorbis is supported)");
        var setup = VorbisSetups.Find(sample.SetupCrc) ?? throw new InvalidDataException($"no Vorbis setup header for CRC {sample.SetupCrc}");
        var ogg = ToOgg(sample, setup);
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
        var pcm = new List<short>(sample.Frames * sample.Channels);
        var buffer = new float[4096 * sample.Channels];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < read; i++)
                pcm.Add((short)Math.Clamp(MathF.Round(buffer[i] * 32767f), short.MinValue, short.MaxValue));
        int wanted = sample.Frames * sample.Channels;
        if (sample.Frames > 0 && pcm.Count > wanted)
            pcm.RemoveRange(wanted, pcm.Count - wanted);
        return [.. pcm];
    }

    // Ogg stream: the three headers, then one page per audio packet with its granule position.
    internal static byte[] ToOgg(Sample sample, VorbisSetups.Setup setup)
    {
        var ogg = new OggWriter(serial: 1);
        ogg.Page([IdentificationHeader(sample.Channels, sample.Rate, setup.BlockShort, setup.BlockLong)], 0, bos: true);
        ogg.Page([CommentHeader(), setup.Packet], 0);

        var packets = new List<byte[]>();
        for (int p = 0; p + 2 <= sample.Data.Length;)
        {
            int size = BitConverter.ToUInt16(sample.Data, p);
            if (size == 0 || p + 2 + size > sample.Data.Length)
                break;
            packets.Add(sample.Data[(p + 2)..(p + 2 + size)]);
            p += 2 + size;
        }
        long granule = 0;
        int previous = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            int block = (packets[i][0] & 2) != 0 ? setup.ModeBlock1 : setup.ModeBlock0;
            if (previous != 0)
                granule += (block + previous) / 4;
            previous = block;
            bool last = i == packets.Count - 1;
            ogg.Page([packets[i]], last && sample.Frames > 0 ? sample.Frames : granule, eos: last);
        }
        return ogg.ToArray();
    }

    private static byte[] IdentificationHeader(int channels, int rate, int blockShort, int blockLong)
    {
        var b = new byte[30];
        b[0] = 1;
        "vorbis"u8.CopyTo(b.AsSpan(1));
        // version 0 (bytes 7-10)
        b[11] = (byte)channels;
        BitConverter.GetBytes(rate).CopyTo(b, 12);
        // bitrates 0 (bytes 16-27)
        b[28] = (byte)(Log2(blockShort) | (Log2(blockLong) << 4));
        b[29] = 1; // framing
        return b;
    }

    private static byte[] CommentHeader()
    {
        var vendor = "DarkSign"u8;
        var b = new byte[7 + 4 + vendor.Length + 4 + 1];
        b[0] = 3;
        "vorbis"u8.CopyTo(b.AsSpan(1));
        BitConverter.GetBytes(vendor.Length).CopyTo(b, 7);
        vendor.CopyTo(b.AsSpan(11));
        // 0 user comments, then framing bit
        b[^1] = 1;
        return b;
    }

    private static int Log2(int v) => 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

    private sealed class OggWriter(int serial)
    {
        private readonly MemoryStream _out = new();
        private int _sequence;

        public void Page(IReadOnlyList<byte[]> packets, long granule, bool bos = false, bool eos = false)
        {
            var lacing = new List<byte>();
            foreach (var packet in packets)
            {
                int left = packet.Length;
                while (left >= 255) { lacing.Add(255); left -= 255; }
                lacing.Add((byte)left);
            }
            if (lacing.Count > 255)
                throw new InvalidDataException("Ogg page too large");
            var header = new byte[27 + lacing.Count];
            "OggS"u8.CopyTo(header);
            header[5] = (byte)((bos ? 2 : 0) | (eos ? 4 : 0));
            BitConverter.GetBytes(granule).CopyTo(header, 6);
            BitConverter.GetBytes(serial).CopyTo(header, 14);
            BitConverter.GetBytes(_sequence++).CopyTo(header, 18);
            header[26] = (byte)lacing.Count;
            lacing.CopyTo(header, 27);
            var page = header.Concat(packets.SelectMany(p => p)).ToArray();
            BitConverter.GetBytes(Crc(page)).CopyTo(page, 22);
            _out.Write(page);
        }

        public byte[] ToArray() => _out.ToArray();

        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
        {
            uint r = (uint)i << 24;
            for (int k = 0; k < 8; k++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            return r;
        }).ToArray();

        private static uint Crc(byte[] data)
        {
            uint crc = 0;
            foreach (var b in data)
                crc = (crc << 8) ^ Table[((crc >> 24) ^ b) & 0xFF];
            return crc;
        }
    }
}

/// <summary>The Vorbis setup headers FMOD strips, by CRC32 (Sounds/fsb5-vorbis-setups.bin).</summary>
public static class VorbisSetups
{
    public sealed record Setup(int BlockShort, int BlockLong, int ModeBlock0, int ModeBlock1, byte[] Packet);

    private static Dictionary<uint, Setup>? _table;

    /// <summary>Extra setups by CRC, for tests.</summary>
    internal static void Add(uint crc, Setup setup) => Load()[crc] = setup;

    public static Setup? Find(uint crc) => Load().GetValueOrDefault(crc);

    private static Dictionary<uint, Setup> Load()
    {
        if (_table is not null)
            return _table;
        var table = new Dictionary<uint, Setup>();
        var path = Path.Combine(AppContext.BaseDirectory, "fsb5-vorbis-setups.bin");
        if (File.Exists(path))
        {
            var d = File.ReadAllBytes(path);
            int count = BitConverter.ToInt32(d, 4), p = 8;
            for (int i = 0; i < count; i++)
            {
                uint crc = BitConverter.ToUInt32(d, p);
                int s = BitConverter.ToUInt16(d, p + 4), l = BitConverter.ToUInt16(d, p + 6);
                int m0 = BitConverter.ToUInt16(d, p + 8), m1 = BitConverter.ToUInt16(d, p + 10), len = BitConverter.ToInt32(d, p + 12);
                table[crc] = new Setup(s, l, m0, m1, d[(p + 16)..(p + 16 + len)]);
                p += 16 + len;
            }
        }
        return _table = table;
    }
}
