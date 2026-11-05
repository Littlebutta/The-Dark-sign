namespace DarkSign.Core;

/// <summary>
/// Every feature of the Elden Ring mod with the other ways it can be done, so a feature that fails in game can be
/// switched to another method from the launcher (Fallbacks… window) without a new release. Option A is always the
/// current default; B and C are alternative implementations, and a last "off" option (when there is one) turns the
/// feature back to plain Elden Ring to tell whether that feature is what breaks the game.
///
/// A method acts in one or more of four places: environment variables for the porter (the content build), extra
/// port-maps arguments, environment variables for the game (our DLLs read them), and launch settings (loader, me3
/// profile options). Build methods change the content, so changing one rebuilds it on the next PLAY
/// (<see cref="BuildSignature"/> goes into the build stamp). docs/fallbacks.md explains each method.
/// </summary>
public static class Fallbacks
{
    public enum Stage { Build, Launch }

    /// <summary>Launch settings a method can ask for; several chosen methods combine (any true wins).</summary>
    public sealed record LaunchTweaks(bool ModEngine2 = false, bool DisableArxan = false, bool NoMemPatch = false,
        bool StartOnline = false)
    {
        public LaunchTweaks Or(LaunchTweaks? other) => other is null ? this : new LaunchTweaks(
            ModEngine2 || other.ModEngine2, DisableArxan || other.DisableArxan, NoMemPatch || other.NoMemPatch,
            StartOnline || other.StartOnline);
    }

    public sealed record Option(string Letter, string Title, string Help)
    {
        public IReadOnlyDictionary<string, string> PorterEnv { get; init; } = new Dictionary<string, string>();
        public IReadOnlyList<string> MapsArgs { get; init; } = [];
        public IReadOnlyDictionary<string, string> GameEnv { get; init; } = new Dictionary<string, string>();
        public LaunchTweaks? Launch { get; init; }
    }

    /// <param name="Key">Stable ID saved in launcher_settings.json (never rename one).</param>
    /// <param name="Group">Heading in the Fallbacks window.</param>
    /// <param name="Symptom">What going wrong looks like in game: when to try the next option.</param>
    public sealed record Feature(string Key, string Group, string Title, Stage Stage, string Symptom, IReadOnlyList<Option> Options);

    private static Dictionary<string, string> Env(params string[] pairs)
    {
        var env = new Dictionary<string, string>();
        for (int i = 0; i + 1 < pairs.Length; i += 2)
            env[pairs[i]] = pairs[i + 1];
        return env;
    }

    public static readonly IReadOnlyList<Feature> All =
    [
        // ---- Starting the game ----
        new("loader", "Starting the game", "Mod loader", Stage.Launch,
            "The game doesn't start, closes at the logo, hangs before the title screen, or the F1 menu never appears.",
        [
            new("A", "me3 (default)", "me3 v0.13.0 loads the mod folder, the save redirect and the F1 menu. The only loader that plays modded sounds."),
            new("B", "me3 with Arxan disabled", "Same, plus me3's disable_arxan: neutralises the game's code protection, which can undo or crash on hooks (the F1 menu and motion hooks are ours).")
                { Launch = new LaunchTweaks(DisableArxan: true) },
            new("C", "ModEngine2 2.1.0", "The loader used up to 1.0.12x; it booted to the title screen with this mod. Modded sounds don't play under it (proven 2026-11-01), so swings are silent or Elden Ring's.")
                { Launch = new LaunchTweaks(ModEngine2: true) },
            new("D", "me3 without its memory patch", "me3 without mem_patch (its bigger game heap). Try only if A and B crash with an out-of-memory or allocator error in me3's log.")
                { Launch = new LaunchTweaks(NoMemPatch: true) },
        ]),
        new("test-menu", "Starting the game", "F1 test menu", Stage.Launch,
            "The game crashes or freezes when the F1 menu opens or as soon as the game window shows, or F1 does nothing.",
        [
            new("A", "Overlay and hotkeys (default)", "F1 opens the menu (drawn into the game's DirectX 12 frames). The hotkeys work too: Ctrl+Shift+F1 starts the DS3 run, Ctrl+Shift+F2 leaves it, Ctrl+Shift+1-9 warps to a ported map, Ctrl+Shift+F3 gives every DS3 weapon."),
            new("B", "Hotkeys only", "No overlay (nothing hooks DirectX 12); the same hotkeys do the main jobs. A beep means the game took it, two beeps that it couldn't (load a character first).")
                { GameEnv = Env(NoOverlayVariable, "1") },
        ]),
        new("build-threads", "Starting the game", "Content build speed", Stage.Build,
            "The content build fails or gives different results from one run to the next.",
        [
            new("A", "Every core (default)", "port-weapons and port-maps run side by side, each on every core."),
            new("B", "One core", "Everything one step at a time (DARKSIGN_THREADS=1), as builds were before 1.0.12x. Slower, but rules out a threading bug.")
                { PorterEnv = Env("DARKSIGN_THREADS", "1") },
        ]),

        // ---- Weapon movesets ----
        new("moveset-slot", "Weapon movesets", "Where a weapon's DS3 moveset goes", Stage.Build,
            "A ported weapon swings with Elden Ring's moves (R1 or R2), or with a different Elden Ring weapon's moves.",
        [
            new("A", "spAtkcategory (default)", "The DS3 moveset goes in the weapon's Special Motion Category, the way Elden Ring's unique weapons and community moveset mods do it; the row keeps its template's motion category."),
            new("B", "wepmotionCategory", "The DS3 moveset replaces the weapon's motion category itself (builds up to 1.0.12x); the template's special section, if it has one, is left in place.")
                { PorterEnv = Env("DARKSIGN_MOVESET_FIELD", "wmc") },
        ]),
        new("combo-windows", "Weapon movesets", "When the next attack can start (R1 chains)", Stage.Build,
            "R1 doesn't chain to the second swing, swings are cut short, or you can act too early or too late.",
        [
            new("A", "Elden Ring windows, stretched (default)", "Elden Ring's combo and cancel windows from the template move, stretched onto the DS3 swing between matching hits and recovery points."),
            new("B", "DS3's own windows", "DS3's JumpTable events copied as they are (DARKSIGN_DS3_JUMPTABLES=1). Some DS3 IDs mean different things in Elden Ring.")
                { PorterEnv = Env("DARKSIGN_DS3_JUMPTABLES", "1") },
            new("C", "Elden Ring windows, shifted", "Elden Ring's windows moved to the first DS3 hit and not stretched (DARKSIGN_SWING_ALIGN=0), as the first moveset builds did.")
                { PorterEnv = Env("DARKSIGN_SWING_ALIGN", "0") },
        ]),
        new("er-moves", "Weapon movesets", "Moves DS3 doesn't have (Elden Ring-only actions)", Stage.Build,
            "The game hangs while loading, or Elden Ring-only moves of a ported weapon (e.g. guard counter) T-pose or do nothing.",
        [
            new("A", "Import the template's animation (default)", "The new category points at the template's own animation, as Elden Ring's categories do. Keeps the animation binder small."),
            new("B", "Copy the template's animation", "A full copy under the new category (DARKSIGN_COPY_ER_ANIMS=1). Bigger binder (a 4x binder once hung loading).")
                { PorterEnv = Env("DARKSIGN_COPY_ER_ANIMS", "1") },
        ]),
        new("anim-format", "Weapon movesets", "DS3 animation format", Stage.Build,
            "DS3 moves look broken (twisting limbs, jitter, sliding) or the game hangs while loading.",
        [
            new("A", "Spline-compressed (default)", "DS3 animations re-encoded as Havok spline animations, like Elden Ring's own."),
            new("B", "Interleaved (uncompressed)", "Every frame stored as is (DARKSIGN_ANIM_INTERLEAVED=1). Exact, but about 5x bigger: the first build like this hung loading.")
                { PorterEnv = Env("DARKSIGN_ANIM_INTERLEAVED", "1") },
        ]),
        new("event-groups", "Weapon movesets", "TAE event groups for new DS3 events", Stage.Build,
            "DS3 effects or sounds of some moves never happen while others do, or a move freezes the character.",
        [
            new("A", "Add missing groups (default)", "A DS3 event whose type the Elden Ring move doesn't use gets an event group copied from another move of the template."),
            new("B", "Only the move's own groups", "Such events are left out (DARKSIGN_ADD_EVENT_GROUPS=0).")
                { PorterEnv = Env("DARKSIGN_ADD_EVENT_GROUPS", "0") },
        ]),

        // ---- Two-handed split ----
        new("two-hand", "Two-handed split", "Split weapons (Friede's scythe, twinblades)", Stage.Build,
            "Two-handing a split weapon doesn't split it, or the second blade floats, sticks to the body or sits in the wrong hand.",
        [
            new("A", "DS3 positions, matched by bone (default)", "Each model's DS3 dummy poly translated to the Elden Ring player dummy on the same bone."),
            new("off", "Off (template positions)", "Ported weapons keep their template's WepAbsorpPosParam row (DARKSIGN_ABSORP=0): no split. Tells whether the split is what breaks something.")
                { PorterEnv = Env("DARKSIGN_ABSORP", "0") },
        ]),

        // ---- Weapon sounds and effects ----
        new("sounds", "Weapon sounds and effects", "DS3 weapon sounds", Stage.Build,
            "Swings are silent, play the wrong sound, or sound pitched down or muffled.",
        [
            new("A", "Embedded, own container (default)", "DS3 samples decoded to PCM and embedded in cs_main.bnk, each Sound in a random container of its own under the template's actor-mixer (the layout Yonder and Spellforge use)."),
            new("B", "Streamed loose files", "Same layout, but each sample is a loose .wem in sd\\wem\\NN\\ that me3 streams (DARKSIGN_STREAM_SOUNDS=1), the way RogueRing ships its sounds.")
                { PorterEnv = Env("DARKSIGN_STREAM_SOUNDS", "1") },
            new("C", "Inside Elden Ring's whoosh container", "The Sound goes under the template's own container (DARKSIGN_SOUND_CONTAINERS=0), as builds up to 1.0.12x did. That container lowers the pitch and filters it.")
                { PorterEnv = Env("DARKSIGN_SOUND_CONTAINERS", "0") },
            new("D", "Played by our DLL (no Wwise)", "Elden Ring's sound bank is left alone. The DS3 samples are written as WAV files and the test menu DLL plays them on its own audio stream whenever a ported swing's sound event runs (DOOM RING's approach). Always audible, but not 3D-positioned. Elden Ring 2.7.2 only.")
                { PorterEnv = Env("DARKSIGN_SOUND_METHOD", "dll"), GameEnv = Env(DllSoundsVariable, "1") },
            new("off", "Off (Elden Ring sounds)", "Ported weapons keep the Elden Ring template's swing sounds (DARKSIGN_DS3_SOUND_EVENTS=0). Not DS3, but never silent.")
                { PorterEnv = Env("DARKSIGN_DS3_SOUND_EVENTS", "0") },
        ]),
        new("effects", "Weapon sounds and effects", "DS3 weapon effects (trails, sparks)", Stage.Build,
            "The game crashes when a ported weapon swings, or its effects are missing or wrong.",
        [
            new("A", "DS3 effects (default)", "DS3's FXR effects converted to Elden Ring's format (@cccode/fxr), with the sounds they play."),
            new("B", "DS3 effects without their sounds", "Same effects; the sounds inside them are left out (DARKSIGN_FXR_SOUNDS=0).")
                { PorterEnv = Env("DARKSIGN_FXR_SOUNDS", "0") },
            new("off", "Off (Elden Ring effects)", "Ported weapons keep the template's effects (DARKSIGN_DS3_EFFECT_EVENTS=0).")
                { PorterEnv = Env("DARKSIGN_DS3_EFFECT_EVENTS", "0") },
        ]),

        // ---- DS3 maps ----
        new("map-collision", "DS3 maps", "Map collision format", Stage.Build,
            "You fall through the floor of a DS3 map, or the game hangs or crashes while a DS3 map loads.",
        [
            new("A", "XML tagfiles (default)", "DS3 collision converted to Havok 2018 and stored as XML tagfiles, which JortPob says Elden Ring reads."),
            new("B", "Binary tagfiles", "The same collision written as binary Havok tagfiles, like Elden Ring's own (port-maps --binary-collision).")
                { MapsArgs = ["--binary-collision"] },
            new("C", "Binary, keeping DS3's SIMD tree", "Binary tagfiles that keep DS3's SIMD collision tree instead of building a new one (--binary-collision --simd).")
                { MapsArgs = ["--binary-collision", "--simd"] },
        ]),
        new("map-groups", "DS3 maps", "Which map parts are drawn", Stage.Build,
            "Parts of a DS3 map are invisible or flicker in and out as you move.",
        [
            new("A", "Everything visible (default)", "Every map part draws everywhere in the map (Elden Ring's draw groups ignored)."),
            new("B", "DS3's draw groups", "Parts keep DS3's draw and display groups (port-maps --ds3-groups), as DS3 culls them.")
                { MapsArgs = ["--ds3-groups"] },
        ]),

        // ---- Co-op ----
        new("seamless", "Seamless Co-op", "How Seamless Co-op is loaded", Stage.Launch,
            "With Seamless Co-op ticked: the game won't start, Seamless' menu doesn't appear, or you can't connect.",
        [
            new("A", "me3 native, offline start (default)", "me3 loads your ersc.dll before our DLLs; me3 starts the game offline and Seamless uses Steam's networking."),
            new("B", "me3 native, online start", "Same, but me3 starts the game online (start_online = true), in case Seamless needs the game's network layer up. Our DLL still blocks FromSoftware's servers outside co-op.")
                { Launch = new LaunchTweaks(StartOnline: true) },
            new("C", "ModEngine2", "Load Seamless through ModEngine2's external_dlls, the way Seamless' own docs show (no modded sounds under ModEngine2).")
                { Launch = new LaunchTweaks(ModEngine2: true) },
        ]),
    ];

    /// <summary>Game variable: the test menu DLL plays the ported sounds itself (sounds D).</summary>
    public const string DllSoundsVariable = "DKSIGN_DLL_SOUNDS";

    /// <summary>Game variable: the test menu DLL doesn't hook DirectX 12 (no overlay), hotkeys only.</summary>
    public const string NoOverlayVariable = "DKSIGN_NO_OVERLAY";

    /// <summary>Game variable: no hotkeys (the DLL is loaded only for another job and the test menu is off).</summary>
    public const string NoHotkeysVariable = "DKSIGN_NO_HOTKEYS";

    /// <summary>Game variable: the mod folder, for the DLL's files (DLL sounds).</summary>
    public const string ModDirVariable = "DKSIGN_MOD_DIR";

    /// <summary>True when a chosen method needs the test menu DLL even with the F1 test menu unticked.</summary>
    public static bool NeedsTestMenuDll(IReadOnlyDictionary<string, string>? methods) =>
        GameEnvironment(methods).ContainsKey(DllSoundsVariable);

    public static Feature? Get(string key) => All.FirstOrDefault(f => f.Key == key);

    /// <summary>The chosen option of a feature: the saved letter, or A when none is saved or it no longer exists.</summary>
    public static Option Chosen(IReadOnlyDictionary<string, string>? methods, Feature feature) =>
        methods is not null && methods.TryGetValue(feature.Key, out var letter)
            && feature.Options.FirstOrDefault(o => o.Letter == letter) is { } chosen
            ? chosen
            : feature.Options[0];

    private static IEnumerable<(Feature Feature, Option Option)> Choices(IReadOnlyDictionary<string, string>? methods) =>
        All.Select(f => (f, Chosen(methods, f)));

    /// <summary>Environment variables for the porter (content build).</summary>
    public static Dictionary<string, string> PorterEnvironment(IReadOnlyDictionary<string, string>? methods)
    {
        var env = new Dictionary<string, string> { ["DARKSIGN_METHODS"] = BuildSignature(methods) };
        foreach (var (_, option) in Choices(methods))
            foreach (var (k, v) in option.PorterEnv)
                env[k] = v;
        return env;
    }

    /// <summary>Extra arguments for port-maps.</summary>
    public static List<string> MapsArguments(IReadOnlyDictionary<string, string>? methods) =>
        Choices(methods).SelectMany(c => c.Option.MapsArgs).Distinct().ToList();

    /// <summary>Environment variables for the game (read by our DLLs).</summary>
    public static Dictionary<string, string> GameEnvironment(IReadOnlyDictionary<string, string>? methods)
    {
        var env = new Dictionary<string, string>();
        foreach (var (_, option) in Choices(methods))
            foreach (var (k, v) in option.GameEnv)
                env[k] = v;
        return env;
    }

    /// <summary>Launch settings. <paramref name="coop"/>: Seamless Co-op is on (its methods only count then).</summary>
    public static LaunchTweaks Launch(IReadOnlyDictionary<string, string>? methods, bool coop) =>
        Choices(methods).Where(c => coop || c.Feature.Key != "seamless")
            .Aggregate(new LaunchTweaks(), (all, c) => all.Or(c.Option.Launch));

    /// <summary>
    /// The build methods that differ from the default, e.g. "sounds=B two-hand=off" ("" when all are A). It goes into
    /// the content build stamp, so changing a build method rebuilds the content and going back to all-A doesn't
    /// rebuild content that was built with all-A.
    /// </summary>
    public static string BuildSignature(IReadOnlyDictionary<string, string>? methods) =>
        string.Join(" ", Choices(methods).Where(c => c.Feature.Stage == Stage.Build && c.Option != c.Feature.Options[0])
            .Select(c => $"{c.Feature.Key}={c.Option.Letter}"));

    /// <summary>Every method that isn't the default, for the status line ("" when all are A).</summary>
    public static string Summary(IReadOnlyDictionary<string, string>? methods) =>
        string.Join(", ", Choices(methods).Where(c => c.Option != c.Feature.Options[0])
            .Select(c => $"{c.Feature.Title}: {c.Option.Letter}"));

    /// <summary>The build stamp for a porter version and methods.</summary>
    public static string BuildStamp(string porterVersion, IReadOnlyDictionary<string, string>? methods) =>
        BuildSignature(methods) is { Length: > 0 } sig ? $"{porterVersion} [{sig}]" : porterVersion;
}
