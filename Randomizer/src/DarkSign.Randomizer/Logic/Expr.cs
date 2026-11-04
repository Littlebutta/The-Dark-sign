namespace DarkSign.Randomizer.Logic;

/// <summary>
/// A logic condition: a name (a key item, or an area that must be reached; reaching a boss area means beating its
/// boss), or AND / OR / "N OF" over sub-conditions. Text form, compatible with FogMod's flat "AND a b" / "OR a b":
/// <c>coiledsword</c>, <c>AND smallenvoybanner dregheap_demonprince</c>, <c>2 OF er:godrick er:radahn er:rykard</c>,
/// and nested with parentheses: <c>AND er:altus (OR er:rold er:snowfield)</c>.
/// </summary>
public sealed class Expr
{
    public static readonly Expr True = new(ExprKind.All, 0, null, []);

    public ExprKind Kind { get; }
    /// <summary>For <see cref="ExprKind.Count"/>: how many of the parts must hold.</summary>
    public int Needed { get; }
    public string? Name { get; }
    public IReadOnlyList<Expr> Parts { get; }

    private Expr(ExprKind kind, int needed, string? name, IReadOnlyList<Expr> parts)
    {
        Kind = kind;
        Needed = needed;
        Name = name;
        Parts = parts;
    }

    public static Expr Named(string name) => new(ExprKind.Name, 0, name, []);
    public static Expr All(params Expr[] parts) => Simplify(new(ExprKind.All, 0, null, parts));
    public static Expr Any(params Expr[] parts) => Simplify(new(ExprKind.Any, 0, null, parts));
    public static Expr Count(int needed, IEnumerable<Expr> parts) => new(ExprKind.Count, needed, null, parts.ToList());

    public bool IsTrue => Kind == ExprKind.All && Parts.Count == 0;

    public bool Eval(Func<string, bool> has) => Kind switch
    {
        ExprKind.Name => has(Name!),
        ExprKind.All => Parts.All(p => p.Eval(has)),
        ExprKind.Any => Parts.Any(p => p.Eval(has)),
        ExprKind.Count => Parts.Count(p => p.Eval(has)) >= Needed,
        _ => false,
    };

    public IEnumerable<string> Names() =>
        Kind == ExprKind.Name ? [Name!] : Parts.SelectMany(p => p.Names());

    /// <summary>Renames every name (e.g. adds a game prefix).</summary>
    public Expr Map(Func<string, string> rename) => Kind == ExprKind.Name
        ? Named(rename(Name!))
        : new Expr(Kind, Needed, null, Parts.Select(p => p.Map(rename)).ToList());

    public static Expr? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var tokens = Tokenize(text);
        int at = 0;
        var expr = ParseList(tokens, ref at, topLevel: true);
        if (at != tokens.Count)
            throw new FormatException($"Unexpected '{tokens[at]}' in condition '{text}'");
        return expr;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (var word in text.Replace("(", " ( ").Replace(")", " ) ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            tokens.Add(word);
        return tokens;
    }

    // A list is "AND t...", "OR t...", "N OF t..." or a single term.
    private static Expr ParseList(List<string> tokens, ref int at, bool topLevel)
    {
        if (at >= tokens.Count)
            throw new FormatException("Empty condition");
        var head = tokens[at];
        ExprKind kind;
        int needed = 0;
        if (head == "AND") { kind = ExprKind.All; at++; }
        else if (head == "OR") { kind = ExprKind.Any; at++; }
        else if (at + 1 < tokens.Count && tokens[at + 1] == "OF" && int.TryParse(head, out needed)) { kind = ExprKind.Count; at += 2; }
        else
        {
            var single = ParseTerm(tokens, ref at);
            if (topLevel && at < tokens.Count)
                throw new FormatException($"Use AND/OR between names: '{string.Join(' ', tokens)}'");
            return single;
        }
        var parts = new List<Expr>();
        while (at < tokens.Count && tokens[at] != ")")
            parts.Add(ParseTerm(tokens, ref at));
        if (parts.Count == 0)
            throw new FormatException($"{head} needs at least one name");
        return kind == ExprKind.Count ? Count(needed, parts) : Simplify(new Expr(kind, 0, null, parts));
    }

    private static Expr ParseTerm(List<string> tokens, ref int at)
    {
        var token = tokens[at];
        if (token == "(")
        {
            at++;
            var inner = ParseList(tokens, ref at, topLevel: false);
            if (at >= tokens.Count || tokens[at] != ")")
                throw new FormatException("Missing ')'");
            at++;
            return inner;
        }
        if (token is ")" or "AND" or "OR" or "OF")
            throw new FormatException($"Unexpected '{token}'");
        at++;
        return Named(token);
    }

    private static Expr Simplify(Expr e)
    {
        if (e.Kind is not (ExprKind.All or ExprKind.Any))
            return e;
        var flat = new List<Expr>();
        foreach (var p in e.Parts)
        {
            if (p.Kind == e.Kind)
                flat.AddRange(p.Parts);
            else if (!(e.Kind == ExprKind.All && p.IsTrue))
                flat.Add(p);
        }
        if (flat.Count == 1)
            return flat[0];
        return new Expr(e.Kind, 0, null, flat);
    }

    public override string ToString() => Kind switch
    {
        ExprKind.Name => Name!,
        ExprKind.All when Parts.Count == 0 => "TRUE",
        ExprKind.All => "(AND " + string.Join(' ', Parts) + ")",
        ExprKind.Any => "(OR " + string.Join(' ', Parts) + ")",
        _ => $"({Needed} OF " + string.Join(' ', Parts) + ")",
    };
}

public enum ExprKind { Name, All, Any, Count }
