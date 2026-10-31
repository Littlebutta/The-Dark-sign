using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.OpenSsl;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>One file inside a game's dvdbnd archives.</summary>
public sealed record ArchiveEntry(string Archive, string? Path, ulong Hash, BHD5.FileHeader Header);

/// <summary>
/// Read-only access to a game's packed archives (DataN.bhd/.bdt). Headers are RSA-decrypted in memory and file
/// data is read straight from the .bdt, so nothing in the game folder is ever written.
/// </summary>
public sealed class GameArchives : IDisposable
{
    private readonly Dictionary<ulong, ArchiveEntry> _byHash = [];
    private readonly Dictionary<string, FileStream> _bdts = new(StringComparer.OrdinalIgnoreCase);

    public PorterGame Game { get; }
    public string GameDir { get; }
    public IReadOnlyCollection<ArchiveEntry> Entries => _byHash.Values;

    private GameArchives(PorterGame game, string gameDir)
    {
        Game = game;
        GameDir = gameDir;
    }

    public static GameArchives Open(PorterGame game, string gameDir, ExternalData data)
    {
        var keys = ArchiveKeyStore.Load(data.UxmKeysFile(), game);
        var names = LoadDictionary(data.UxmDictionary(game), game);
        var archives = new GameArchives(game, gameDir);

        foreach (var archive in game.Archives)
        {
            var bhdPath = System.IO.Path.Combine(gameDir, archive + ".bhd");
            if (!File.Exists(bhdPath))
                continue;
            if (!keys.TryGetValue(archive, out var pem))
                throw new InvalidDataException($"No archive key for {game.ShortName} {archive}.");

            var bhd = BHD5.Read(DecryptRsa(File.ReadAllBytes(bhdPath), pem), game.ArchiveFormat);
            foreach (var file in bhd.Buckets.SelectMany(b => b))
            {
                names.TryGetValue(file.FileNameHash, out var path);
                archives._byHash[file.FileNameHash] = new ArchiveEntry(archive, path, file.FileNameHash, file);
            }
        }
        return archives;
    }

    public static ulong Hash(string path, PorterGame game)
    {
        var s = path.Trim().Replace('\\', '/').ToLowerInvariant();
        if (!s.StartsWith('/'))
            s = "/" + s;
        if (game.ArchiveFormat >= BHD5.Game.EldenRing)
            return s.Aggregate(0ul, (h, c) => h * 0x85ul + c);
        return s.Aggregate(0u, (h, c) => h * 37u + c);
    }

    public bool Contains(string path) => _byHash.ContainsKey(Hash(path, Game));

    /// <summary>The file's bytes as stored (still DCX-compressed where the game compresses it).</summary>
    public byte[] ReadRaw(string path)
    {
        if (!_byHash.TryGetValue(Hash(path, Game), out var entry))
            throw new FileNotFoundException($"{path} is not in the {Game.ShortName} archives.");
        return ReadRaw(entry);
    }

    public byte[] ReadRaw(ArchiveEntry entry)
    {
        if (!_bdts.TryGetValue(entry.Archive, out var bdt))
        {
            bdt = File.OpenRead(System.IO.Path.Combine(GameDir, entry.Archive + ".bdt"));
            _bdts[entry.Archive] = bdt;
        }
        var bytes = entry.Header.ReadFile(bdt);
        var size = entry.Header.UnpaddedFileSize;
        return size > 0 && size < bytes.Length ? bytes[..(int)size] : bytes;
    }

    /// <summary>The file's bytes with any DCX compression removed.</summary>
    public byte[] Read(string path)
    {
        var raw = ReadRaw(path);
        return DCX.Is(raw) ? DCX.Decompress(raw) : raw;
    }

    /// <summary>The file's bytes without DCX compression, plus the compression to use when writing it back.</summary>
    public (byte[] Bytes, DCX.CompressionInfo Compression) ReadWithCompression(string path)
    {
        var bytes = DCX.Decompress(ReadRaw(path), out var compression);
        return (bytes, compression);
    }

    public void Dispose()
    {
        foreach (var s in _bdts.Values)
            s.Dispose();
        _bdts.Clear();
    }

    private static Dictionary<ulong, string> LoadDictionary(string file, PorterGame game)
    {
        var map = new Dictionary<ulong, string>();
        foreach (var line in File.ReadLines(file))
        {
            var path = line.Trim();
            if (path.Length == 0 || path.StartsWith('#'))
                continue;
            map[Hash(path, game)] = path;
        }
        return map;
    }

    /// <summary>Raw RSA public-key decryption, block by block, as the game does for its archive headers.</summary>
    private static byte[] DecryptRsa(byte[] encrypted, string pem)
    {
        var key = (AsymmetricKeyParameter)new PemReader(new StringReader(pem)).ReadObject();
        var engine = new RsaEngine();
        engine.Init(false, key);

        int inSize = engine.GetInputBlockSize(), outSize = engine.GetOutputBlockSize();
        using var output = new MemoryStream();
        for (int offset = 0; offset < encrypted.Length; offset += inSize)
        {
            var block = engine.ProcessBlock(encrypted, offset, Math.Min(inSize, encrypted.Length - offset));
            output.Write(new byte[outSize - block.Length]);
            output.Write(block);
        }
        return output.ToArray();
    }
}
