namespace DarkSign.Porter;

/// <summary>
/// The error that stopped a SoulsFormats write. SoulsFormats writes inside using blocks, and a writer disposed with
/// offsets still unfilled throws "Not all reservations filled", which replaces the error that stopped the write
/// (1.0.340: the c6330 / c6331 enemy binders failed with only the unfilled reservations named). This keeps, per
/// thread, the last exception thrown before such a dispose error (first-chance), so a report can name the cause.
/// </summary>
public static class MaskedErrors
{
    [ThreadStatic] private static Exception? _last;
    private static int _installed;

    /// <summary>Starts watching; once per run, before the writes.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 0)
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (!IsMask(e.Exception))
                    _last = e.Exception;
            };
    }

    public static bool IsMask(Exception e) =>
        e is InvalidOperationException && e.Message.StartsWith("Not all reservations filled", StringComparison.Ordinal);

    /// <summary>The exception the dispose error replaced, on this thread; null for any other exception or when unknown.</summary>
    public static Exception? Cause(Exception e) => IsMask(e) && _last is { } last && !ReferenceEquals(last, e) ? last : null;
}
