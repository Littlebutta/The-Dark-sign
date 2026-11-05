namespace DarkSign.Randomizer.Logic;

/// <summary>
/// Sorts enemies of both games into kinds by the words in their names ("Lothric Knight" is a knight, "Godrick
/// Soldier" a soldier). Shared by the map port's stand-in picker and the randomizer's enemy shuffle.
/// </summary>
public static class EnemyKinds
{
    // DS3/Elden Ring name words -> kind. Checked in order; the first kind whose word is in the name wins.
    private static readonly (string Kind, string[] Words)[] Kinds =
    [
        ("dragon", ["dragon", "wyvern", "drake", "wyrm"]),
        ("giant", ["giant", "troll", "ogre", "colossus"]),
        ("knight", ["knight", "paladin", "sentinel", "guard captain", "banner"]),
        ("archer", ["archer", "crossbow", "bowman", "sniper"]),
        ("mage", ["sorcerer", "mage", "scholar", "wizard", "pyromancer", "evangelist", "deacon", "cleric", "priest", "monk"]),
        ("skeleton", ["skeleton", "bonewheel", "skull"]),
        ("dog", ["dog", "hound", "wolf"]),
        ("rat", ["rat"]),
        ("bird", ["crow", "bird", "harpy"]),
        ("crab", ["crab"]),
        ("worm", ["slug", "leech", "maggot", "worm", "larva"]),
        ("serpent", ["serpent", "snake"]),
        ("lizard", ["lizard"]),
        ("tree", ["tree", "root", "ent "]),
        ("ghost", ["ghost", "spirit", "phantom", "wraith"]),
        ("beast", ["beast", "boar", "bear", "ox", "demon", "ape"]),
        ("soldier", ["soldier", "hollow", "thrall", "manservant", "peasant", "footman", "foot ", "zombie", "noble", "villager", "infantry", "warrior", "assassin", "thief"]),
    ];

    public static string KindOf(string name)
    {
        var n = " " + name.ToLowerInvariant() + " ";
        foreach (var (kind, words) in Kinds)
            if (words.Any(w => n.Contains(w, StringComparison.Ordinal)))
                return kind;
        return "";
    }
}
