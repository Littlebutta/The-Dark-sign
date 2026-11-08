using System.Collections;
using System.Reflection;
using HKLib.Reflection.hk2018;

namespace DarkSign.Porter.Havok;

/// <summary>
/// A window on HKLib's three lazily filled static caches (HKLib.Reflection's HavokData.ReflectorCache and the XML and
/// binary ArrayFormatHandler tables), which are plain Dictionaries with no locking. A cache miss writes to them, so
/// two threads serializing at once can corrupt them; that is why <see cref="AnimPort.HavokLock"/> exists. A cache hit
/// only reads, which is safe on any number of threads. So a stage whose object graphs use a fixed set of types can
/// fill the caches once, under the lock, and then serialize on every core (see <see cref="Maps.CollisionPort.WarmUp"/>).
///
/// <see cref="Count"/> says how many entries the caches hold; a stage compares it before and after its lock-free phase
/// to prove no miss happened. When HKLib changes and the fields can't be found, <see cref="Available"/> is false and
/// callers keep the lock.
/// </summary>
public static class HavokCaches
{
    private static readonly IDictionary?[] Caches = Find();

    public static bool Available => Caches.Length > 0 && Caches.All(c => c is not null);

    /// <summary>Entries in all caches together (reads only; safe beside other readers).</summary>
    public static int Count => Available ? Caches.Sum(c => c!.Count) : -1;

    private static IDictionary?[] Find()
    {
        try
        {
            var reflection = typeof(HavokData).Assembly;
            var serialization = typeof(HKLib.Serialization.hk2018.Xml.HavokXmlSerializer).Assembly;
            return
            [
                Field(reflection.GetType("HKLib.Reflection.hk2018.HavokData"), "ReflectorCache"),
                Field(serialization.GetType("HKLib.Serialization.hk2018.Xml.FormatHandlers.ArrayFormatHandler"), "ArrayFormatHandlers"),
                Field(serialization.GetType("HKLib.Serialization.hk2018.Binary.FormatHandlers.ArrayFormatHandler"), "ArrayFormatHandlers"),
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IDictionary? Field(Type? type, string name) =>
        type?.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IDictionary;
}
