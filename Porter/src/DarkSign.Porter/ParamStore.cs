using SoulsFormats;
using SoulsFormats.Cryptography;

namespace DarkSign.Porter;

/// <summary>A game's regulation params, with Paramdex defs applied and Paramdex row names filled in.</summary>
public sealed class ParamStore
{
    private readonly Dictionary<string, PARAM> _params = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BinderFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly PorterGame _game;
    private readonly ExternalData _data;

    public BND4 Regulation { get; }

    private ParamStore(PorterGame game, BND4 regulation, ExternalData data)
    {
        _game = game;
        _data = data;
        Regulation = regulation;
        foreach (var file in regulation.Files.Where(f => f.Name.EndsWith(".param", StringComparison.OrdinalIgnoreCase)))
            _files[ParamName(file.Name)] = file;
    }

    /// <summary>DS3 keeps its regulation as Data0.bdt; Elden Ring as regulation.bin.</summary>
    public static ParamStore Load(PorterGame game, string gameDir, ExternalData data)
    {
        var regulation = game == PorterGame.DarkSouls3
            ? RegulationDecryptor.DecryptDS3Regulation(Path.Combine(gameDir, "Data0.bdt"))
            : RegulationDecryptor.DecryptERRegulation(Path.Combine(gameDir, "regulation.bin"));
        return new ParamStore(game, regulation, data);
    }

    public IEnumerable<string> ParamNames => _files.Keys;

    /// <summary>The named param (e.g. "EquipParamWeapon") with its def applied, or null if the game has none.</summary>
    public PARAM? Get(string name)
    {
        if (_params.TryGetValue(name, out var cached))
            return cached;
        if (!_files.TryGetValue(name, out var file))
            return null;

        var param = PARAM.Read(file.Bytes);
        var defFile = _data.ParamDef(_game, name) ?? _data.ParamDef(_game, param.ParamType)
            ?? _data.ParamDef(_game, PascalCase(param.ParamType))
            ?? throw new FileNotFoundException($"Paramdex has no def for {_game.ShortName} {name} ({param.ParamType}).");
        var def = PARAMDEF.XmlDeserialize(defFile, versionAware: true, validateFields: false);
        if (!param.ApplyParamdefCarefully(def))
            throw new InvalidDataException(
                $"{_game.ShortName} {name}: Paramdex def {param.ParamType} does not fit this game version " +
                $"(row size {param.DetectedSize}).");

        var names = _data.ParamNames(_game, name);
        if (names is not null)
            ApplyNames(param, names);
        _params[name] = param;
        return param;
    }

    /// <summary>Writes every param that was loaded back into the regulation binder.</summary>
    public void SaveLoaded()
    {
        foreach (var (name, param) in _params)
            _files[name].Bytes = param.Write();
    }

    // Binder names are Windows paths ("N:\\GR\\data\\Param\\param\\GameParam\\EquipParamWeapon.param") on every OS.
    private static string ParamName(string binderName) =>
        Path.GetFileNameWithoutExtension(binderName.Replace('\\', '/').Split('/')[^1]);

    // MAGIC_PARAM_ST -> MagicParam, the other way Paramdex names Elden Ring defs.
    private static string PascalCase(string paramType) => string.Concat(
        paramType.Replace("_ST", "").Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    private static void ApplyNames(PARAM param, string namesFile)
    {
        var names = new Dictionary<int, string>();
        foreach (var line in File.ReadLines(namesFile))
        {
            var space = line.IndexOf(' ');
            if (space > 0 && int.TryParse(line[..space], out var id))
                names.TryAdd(id, line[(space + 1)..].Trim());
        }
        foreach (var row in param.Rows)
            if (string.IsNullOrEmpty(row.Name) && names.TryGetValue(row.ID, out var n))
                row.Name = n;
    }
}
