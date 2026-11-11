namespace DarkSign.Porter;

/// <summary>
/// Shares out the player behaviour's animation IDs among the ported movesets. Every animation a behaviour clip
/// plays has an internal ID, a signed 16-bit number, so the file holds 32,767 at most; Elden Ring's own clips use a
/// large part of that, and a moveset built as a full copy of its template's motion category (one clip per move of
/// the template, DS3's or not) costs several hundred more. ERClipGeneratorTool (The12thAvenger) numbers new clips
/// the same way, max + 1, and no public mod adds enough movesets to meet the limit, so there is no community
/// answer to it. Ours: a moveset can also be built "lean", with only the moves DS3 replaces. The weapon's Special
/// Motion Category (spAtkcategory) overrides its motion category move by move, as for Elden Ring's own unique
/// weapons (Themyys, Introduction to Moveset Modding), so every move the lean section lacks plays from the
/// template's motion category, which the row keeps: the same animation the full copy imports.
/// </summary>
internal static class ClipBudget
{
    /// <summary>Animation internal IDs are signed 16-bit.</summary>
    public const int Limit = short.MaxValue;

    /// <summary>IDs kept back for the weapon arts, which are built after the movesets (a few clips per art).</summary>
    public const int ArtReserve = 2500;

    public enum Mode { None, Lean, Full }

    /// <summary>A moveset wanting IDs, in priority order: what a full copy and a lean build cost.</summary>
    public readonly record struct Want(int Full, int Lean);

    /// <summary>
    /// The mode of each moveset, given <paramref name="available"/> free IDs. Every moveset is built, lean if need be,
    /// before any gets a full copy: an own moveset left out means a weapon swings with its class's moves. Then full
    /// copies go out in priority order while IDs last (the builds up to 1.0.284 made full copies only, so the movesets
    /// playtested so far stay as they were whenever there is room). When even lean builds don't all fit, the
    /// lowest priorities are left out.
    /// </summary>
    public static Mode[] Plan(IReadOnlyList<Want> wants, int available)
    {
        var modes = new Mode[wants.Count];
        int left = available;
        for (int i = 0; i < wants.Count; i++)
        {
            if (wants[i].Lean > left)
                continue;
            modes[i] = Mode.Lean;
            left -= wants[i].Lean;
        }
        for (int i = 0; i < wants.Count; i++)
        {
            int extra = wants[i].Full - wants[i].Lean;
            if (modes[i] != Mode.Lean || extra > left)
                continue;
            modes[i] = Mode.Full;
            left -= extra;
        }
        return modes;
    }

    public static int SelfTest()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
        static string Show(Mode[] m) => string.Concat(m.Select(x => x switch { Mode.Full => "F", Mode.Lean => "L", _ => "-" }));

        Check("room for all: every moveset is a full copy", Show(Plan([new(400, 100), new(400, 100), new(300, 50)], 1100)) == "FFF");
        Check("short: all built, full copies by priority", Show(Plan([new(400, 100), new(400, 100), new(300, 50)], 600)) == "FLL");
        Check("short: a later, cheaper full copy still fits", Show(Plan([new(400, 100), new(900, 100), new(300, 50)], 800)) == "FLF");
        Check("very short: lowest priority left out", Show(Plan([new(400, 100), new(400, 100), new(300, 50)], 220)) == "LL-");
        Check("very short: a cheaper later one still fits", Show(Plan([new(400, 100), new(400, 150), new(300, 50)], 160)) == "L-L");
        Check("no lean builds (lean = full): as many full copies as fit", Show(Plan([new(400, 400), new(400, 400), new(300, 300)], 750)) == "F-F");
        Check("nothing free: nothing built", Show(Plan([new(400, 100)], 0)) == "-");
        // 111 movesets of 330 clips, 120 of them DS3's, on what Elden Ring leaves free: all are built.
        var many = Enumerable.Repeat(new Want(330, 120), 111).ToList();
        var plan = Plan(many, 20000);
        Check("111 movesets on 20,000 IDs: all built, " + plan.Count(m => m == Mode.Full) + " full",
            plan.All(m => m != Mode.None) && plan.Count(m => m == Mode.Full) == (20000 - 111 * 120) / 210
            && plan.Sum(m => m == Mode.Full ? 330 : 120) <= 20000);
        Console.WriteLine(failures == 0 ? "clip budget self-test passed" : $"clip budget self-test: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
