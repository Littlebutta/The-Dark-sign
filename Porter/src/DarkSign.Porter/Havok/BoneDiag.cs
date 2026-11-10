using System.Numerics;
using System.Text;
using SoulsFormats;

namespace DarkSign.Porter;

/// <summary>
/// weapon-diag bones: read-only report of both games' player skeletons (c0000) side by side: for every shared bone,
/// its parent in each game, how far its rest rotation differs, and what the moveset port does with its DS3 rotation
/// (<see cref="AnimPort.PlanRetarget"/>: copy, carry over relative to rest, or keep Elden Ring's pose). The hand
/// chains are printed in world space too, so a weapon bone that would hang backwards shows up here before a build
/// (DS3's L_Weapon rests 180° from Elden Ring's). Writes Work\reports\diag\bones.txt.
/// </summary>
public static class BoneDiag
{
    public static int Run(PorterContext ctx)
    {
        var report = new StringBuilder();
        void Log(string line) { Console.WriteLine(line); report.AppendLine(line); }
        using var ds3Archives = ctx.OpenArchives(PorterGame.DarkSouls3);
        using var erArchives = ctx.OpenArchives(PorterGame.EldenRing);
        var ds3Main = BND4.Read(ds3Archives.Read("/chr/c0000.anibnd.dcx"));
        var erMain = BND4.Read(erArchives.Read("/chr/c0000.anibnd.dcx"));
        var ds3 = AnimPort.ReadDs3Skeleton(Leaf(ds3Main, "skeleton.hkx")!.Bytes);
        var er = AnimPort.ReadErSkeleton(Leaf(erMain, "skeleton.hkx")!.Bytes, erMain.Files.FirstOrDefault(f => f.Name.EndsWith(".compendium", StringComparison.OrdinalIgnoreCase))?.Bytes);
        Log($"DS3 c0000: {ds3.Names.Count} bones; Elden Ring c0000: {er.Names.Count} bones; mode DARKSIGN_WEAPON_BONES={(AnimPort.BoneMode == "" ? "(unset: relative to rest)" : AnimPort.BoneMode)}");
        AnimPort.PlanRetarget(ds3, er);
        var ds3Index = ds3.Names.Select((n, i) => (n, i)).GroupBy(x => x.n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var wDs3 = AnimPort.WorldRest(ds3.Names.Count, ds3.Parents, i => ds3.ReferencePose[i].Rotation);
        var wEr = AnimPort.WorldRest(er.Names.Count, er.Parents, i => er.ReferencePose[i].m_rotation);

        Log("");
        Log("bone\tER parent\tDS3 parent\tlocal rest diff\tworld rest diff\taction (Pre / Post)");
        int shared = 0, corrected = 0, frozen = 0;
        for (int b = 0; b < er.Names.Count; b++)
        {
            if (!ds3Index.TryGetValue(er.Names[b], out var d))
                continue;
            shared++;
            string erParent = er.Parents.Count > b && er.Parents[b] >= 0 ? er.Names[er.Parents[b]] : "-";
            string ds3Parent = ds3.Parents.Count > d && ds3.Parents[d] >= 0 ? ds3.Names[ds3.Parents[d]] : "-";
            float local = AnimPort.Degrees(er.ReferencePose[b].m_rotation, ds3.ReferencePose[d].Rotation);
            float world = AnimPort.Degrees(wEr[b], wDs3[d]);
            string action;
            if (er.Frozen is not null && er.Frozen.Contains(b))
            {
                action = "keeps Elden Ring's pose";
                frozen++;
            }
            else if (er.Pre is not null && er.Post is not null && (er.Pre[b] != Quaternion.Identity || er.Post[b] != Quaternion.Identity))
            {
                action = $"carried over relative to rest ({AnimPort.Degrees(er.Pre[b]):0.#}° / {AnimPort.Degrees(er.Post![b]):0.#}°)";
                corrected++;
            }
            else
                action = "DS3 rotation copied";
            if (local >= 0.5f || world >= 0.5f || !erParent.Equals(ds3Parent, StringComparison.OrdinalIgnoreCase) || action != "DS3 rotation copied")
                Log($"{er.Names[b]}\t{erParent}\t{ds3Parent}\t{local:0.#}°\t{world:0.#}°\t{action}");
        }
        Log($"{shared} shared bones: {corrected} carried over relative to rest, {frozen} keep Elden Ring's pose, {shared - corrected - frozen} copied as DS3's (only bones that differ, or aren't copied, are listed)");

        Log("");
        Log("hand chains at rest, world space (axes of each bone: X, Y, Z as rows):");
        foreach (var name in new[] { "L_Hand", "L_Weapon", "L_Shield", "R_Hand", "R_Weapon", "R_Shield" })
        {
            int b = er.Names.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (b < 0 || !ds3Index.TryGetValue(name, out var d))
                continue;
            Log($"  {name}: DS3 {Axes(wDs3[d])}");
            Log($"  {new string(' ', name.Length)}  ER  {Axes(wEr[b])}");
        }
        Directory.CreateDirectory(ctx.WorkPath("reports", "diag"));
        File.WriteAllText(ctx.WorkPath("reports", "diag", "bones.txt"), report.ToString());
        Console.WriteLine($"wrote {ctx.WorkPath("reports", "diag", "bones.txt")}");
        return 0;
    }

    private static string Axes(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        return $"X ({m.M11:0.00},{m.M12:0.00},{m.M13:0.00}) Y ({m.M21:0.00},{m.M22:0.00},{m.M23:0.00}) Z ({m.M31:0.00},{m.M32:0.00},{m.M33:0.00})";
    }

    private static BinderFile? Leaf(BND4 bnd, string leaf) =>
        bnd.Files.FirstOrDefault(f => ItemText.Leaf(f.Name).Equals(leaf, StringComparison.OrdinalIgnoreCase));
}
