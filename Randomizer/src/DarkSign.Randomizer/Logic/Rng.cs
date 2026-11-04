namespace DarkSign.Randomizer.Logic;

/// <summary>
/// A small seeded random generator (xoshiro256**, seeded by SplitMix64). Our own so a seed gives the same world on
/// every .NET version and every PC: a seed shared between friends must build the same randomization.
/// </summary>
public sealed class Rng
{
    private ulong _s0, _s1, _s2, _s3;

    public Rng(long seed, int stream = 0)
    {
        ulong x = unchecked((ulong)seed ^ ((ulong)(uint)stream * 0x9E3779B97F4A7C15UL));
        _s0 = SplitMix(ref x); _s1 = SplitMix(ref x); _s2 = SplitMix(ref x); _s3 = SplitMix(ref x);
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextULong()
    {
        ulong result = RotL(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3; _s2 ^= t; _s3 = RotL(_s3, 45);
        return result;
    }

    private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));

    /// <summary>0 &lt;= result &lt; max.</summary>
    public int Next(int max) => max <= 0 ? throw new ArgumentOutOfRangeException(nameof(max)) : (int)(NextULong() % (ulong)max);

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    /// <summary>Picks by weight; weights must be &gt;= 0 with a positive sum.</summary>
    public T PickWeighted<T>(IReadOnlyList<T> items, Func<T, double> weight)
    {
        double total = 0;
        foreach (var i in items) total += Math.Max(0, weight(i));
        if (total <= 0) return Pick(items);
        double r = NextDouble() * total;
        foreach (var i in items)
        {
            r -= Math.Max(0, weight(i));
            if (r < 0) return i;
        }
        return items[^1];
    }

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>Derives a seed for a profile from text (so "Devin's run" always means the same seed).</summary>
    public static long SeedFromText(string text)
    {
        ulong h = 1469598103934665603UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text.Trim()))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return (long)(h & 0x7FFF_FFFF_FFFF_FFFFUL) % 1_000_000_000_000L;
    }
}
