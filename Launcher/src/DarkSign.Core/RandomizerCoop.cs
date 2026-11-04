using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DarkSign.Core;

/// <summary>
/// Playing a randomized world with friends. Every player keeps their own seed; a co-op code carries one profile's
/// randomizer choices (seed included) so a friend's launcher can make exactly the same world next to their own and
/// load it while they play together. Both games read the world from files when they start, and Seamless Co-op needs
/// everyone in a session to have the same files, so "joining" means loading the host's world for that launch only:
/// the friend's own character, saves and seed stay as they are.
///
/// The same code always makes the same world on any PC (the randomizer is deterministic and culture-independent;
/// plan.json carries a WorldId hash of the result), so <see cref="CheckCode"/> folds the world into the mod code
/// players compare before a session.
/// </summary>
public static class RandomizerCoop
{
    private const byte Version = 1;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32: no I, L, O, U
    private static readonly string[] Goals = ["Both", "EldenBeast", "SoulOfCinder"];

    /// <summary>A short code like 0K3F-9QXA-…: version, toggles, goal, cross-game ratios, seed and a checksum.</summary>
    public static string Encode(RandomizerSettings s)
    {
        var bytes = new byte[14];
        bytes[0] = Version;
        bytes[1] = (byte)((s.Entrances ? 1 : 0) | (s.Items ? 2 : 0) | (s.CrossGameItems ? 4 : 0) | (s.Bosses ? 8 : 0) |
                          (s.Enemies ? 16 : 0) | (s.SharedShards ? 32 : 0) | (s.Dlc ? 64 : 0));
        bytes[2] = (byte)Math.Max(0, Array.IndexOf(Goals, s.Goal));
        bytes[3] = Percent(s.CrossGameFogs);
        bytes[4] = Percent(s.CrossGameBosses);
        BitConverter.TryWriteBytes(bytes.AsSpan(5, 8), s.Seed);
        if (!BitConverter.IsLittleEndian) bytes.AsSpan(5, 8).Reverse();
        bytes[13] = Checksum(bytes.AsSpan(0, 13));
        var text = ToBase32(bytes);
        return string.Join('-', Enumerable.Range(0, (text.Length + 3) / 4).Select(i => text.Substring(i * 4, Math.Min(4, text.Length - i * 4))));
    }

    /// <summary>The settings a co-op code stands for, or null if it isn't a valid code (typo, wrong version).</summary>
    public static RandomizerSettings? Decode(string? code)
    {
        var bytes = FromBase32(Normalize(code));
        if (bytes is null || bytes.Length != 14 || bytes[0] != Version || bytes[13] != Checksum(bytes.AsSpan(0, 13)) || bytes[2] >= Goals.Length)
            return null;
        var seedBytes = bytes.AsSpan(5, 8).ToArray();
        if (!BitConverter.IsLittleEndian) Array.Reverse(seedBytes);
        var seed = BitConverter.ToInt64(seedBytes);
        if (seed <= 0) return null;
        return new RandomizerSettings
        {
            Enabled = true,
            Seed = seed,
            Entrances = (bytes[1] & 1) != 0,
            Items = (bytes[1] & 2) != 0,
            CrossGameItems = (bytes[1] & 4) != 0,
            Bosses = (bytes[1] & 8) != 0,
            Enemies = (bytes[1] & 16) != 0,
            SharedShards = (bytes[1] & 32) != 0,
            Dlc = (bytes[1] & 64) != 0,
            Goal = Goals[bytes[2]],
            CrossGameFogs = bytes[3] / 100.0,
            CrossGameBosses = bytes[4] / 100.0,
        };
    }

    /// <summary>Upper case, no separators, and the letters people mix up read as the digits they look like.</summary>
    public static string Normalize(string? code) =>
        string.Concat((code ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit)
            .Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }));

    /// <summary>The folder name a joined world is kept under (TheDarkSign\Randomizer\&lt;name&gt;), one per code, so
    /// joining the same friend again reuses it.</summary>
    public static string GuestWorldName(string code) => "Co-op " + Normalize(code);

    /// <summary>The world this profile loads: the friend's world while joining one, else its own randomized world,
    /// else null (the normal game).</summary>
    public static (string WorldName, RandomizerSettings Settings)? ActiveWorld(LaunchProfile profile)
    {
        if (Decode(profile.JoinCode) is { } joined)
            return (GuestWorldName(profile.JoinCode!), joined);
        if (profile.Randomizer is { Enabled: true } own)
            return (profile.Name, own);
        return null;
    }

    /// <summary>The WorldId the porter wrote into a world's plan.json, or null if it hasn't been made.</summary>
    public static string? ReadWorldId(string root, string worldName)
    {
        var path = Path.Combine(RandomizerSettings.ProfileDir(root, worldName), "plan.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("WorldId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The code co-op partners compare: the content build's regulation fingerprint alone for the normal
    /// game, or that folded together with the randomized world's ID, so one code covers both.</summary>
    public static string? CheckCode(string? modFingerprint, string? worldId)
    {
        if (worldId is null) return modFingerprint;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes((modFingerprint ?? "-") + "/" + worldId));
        return Convert.ToHexString(hash)[..8];
    }

    private static byte Percent(double v) => (byte)Math.Clamp((int)Math.Round(v * 100, MidpointRounding.AwayFromZero), 0, 100);

    private static byte Checksum(ReadOnlySpan<byte> data) => SHA256.HashData(data)[0];

    private static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[]? FromBase32(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) return null;
            buffer = ((buffer << 5) | v) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }
}
