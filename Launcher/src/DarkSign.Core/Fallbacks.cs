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
            new("A", "Overlay and hotkeys (default)", "F1 opens the menu (drawn into the game's DirectX 12 frames). The hotkeys work too: Ctrl+Shift+F1 starts the DS3 run, Ctrl+Shift+F2 leaves it, Ctrl+Shift+1-9 warps to a ported map, Ctrl+Shift+F3 gives every DS3 weapon, Ctrl+Shift+F4 clears the enemies spawned from the menu."),
            new("B", "Hotkeys only", "No overlay (nothing hooks DirectX 12); the same hotkeys do the main jobs. A beep means the game took it, two beeps that it couldn't (load a character first).")
                { GameEnv = Env(NoOverlayVariable, "1") },
        ]),
        new("spawn-clear", "Starting the game", "Clearing enemies spawned from the F1 menu", Stage.Launch,
            "\"Clear enemies spawned from this menu\" (or Ctrl+Shift+F4) leaves them standing, or the game crashes when they're cleared.",
        [
            new("A", "Kill them (default)", "Their HP is set to 0, so the game kills them the normal way: they fall and fade out (and may drop runes)."),
            new("B", "Remove them", "The game's own RemoveChrIns takes them out of the world at once, no bodies (DKSIGN_SPAWN_REMOVE=1). Called from the menu's thread, not the game's, so it can crash: use it only if A doesn't clear them.")
                { GameEnv = Env(SpawnRemoveVariable, "1") },
        ]),
        new("build-threads", "Starting the game", "Content build speed", Stage.Build,
            "The content build fails or gives different results from one run to the next.",
        [
            new("A", "Every core (default)", "port-weapons and port-maps run side by side, each on every core."),
            new("B", "One core", "Everything one step at a time (DARKSIGN_THREADS=1), as builds were before 1.0.12x. Slower, but rules out a threading bug.")
                { PorterEnv = Env("DARKSIGN_THREADS", "1") },
            new("C", "Collision one file at a time", "Every core, but map collision files are serialized one at a time under a lock (DARKSIGN_COLLISION_LOCK=1), as builds before 1.0.23x did. Minutes slower per map; rules the lock-free Havok serialization out if a map's collision misbehaves.")
                { PorterEnv = Env("DARKSIGN_COLLISION_LOCK", "1") },
        ]),

        // ---- Weapon movesets ----
        new("moveset-slot", "Weapon movesets", "Where a weapon's DS3 moveset goes", Stage.Build,
            "A ported weapon swings with Elden Ring's moves (R1 or R2), or with a different Elden Ring weapon's moves.",
        [
            new("A", "spAtkcategory (default)", "The DS3 moveset goes in the weapon's Special Motion Category, the way Elden Ring's unique weapons and community moveset mods do it; the row keeps its template's motion category."),
            new("B", "wepmotionCategory", "The DS3 moveset replaces the weapon's motion category itself (builds up to 1.0.12x); the template's special section, if it has one, is left in place.")
                { PorterEnv = Env("DARKSIGN_MOVESET_FIELD", "wmc") },
            new("C", "Both fields", "The DS3 moveset goes in spAtkcategory and in wepmotionCategory, so whichever one the game reads for a move finds it. Class rules (powerstance, guard) then follow the new section, a copy of the template's.")
                { PorterEnv = Env("DARKSIGN_MOVESET_FIELD", "both") },
        ]),
        new("moveset-capacity", "Weapon movesets", "How many DS3 movesets fit", Stage.Build,
            "The game closes at startup after an update, or a weapon that newly has its own DS3 moves does not attack or is missing some moves.",
        [
            new("A", "Every moveset (default)", "Every DS3 moveset gets a motion category: numbers 90 to 255 first, then 256 to 399 (the community's range for weapon movesets), and when the behaviour file's animation IDs run short the later movesets are built lean, with DS3's moves only; their other moves play from the weapon's Elden Ring class as before."),
            new("B", "Categories up to 255 only", "Motion categories stop at 255 (DARKSIGN_WIDE_MOTION=0), as in builds up to 1.0.284: about 62 movesets fit and the remaining weapons swing with their DS3 class's moves. Try it if the game closes at startup or a weapon that newly got its own moves does not attack at all.")
                { PorterEnv = Env("DARKSIGN_WIDE_MOTION", "0") },
            new("C", "Full copies only", "As A, but every moveset is a full copy of its Elden Ring template's category (DARKSIGN_LEAN_MOVESETS=0), as in builds up to 1.0.284; movesets the animation IDs have no room for are left out and those weapons use their class's. Try it if a weapon is missing moves it has nothing of its own for in DS3 (a roll attack, a jump attack, a guard counter).")
                { PorterEnv = Env("DARKSIGN_LEAN_MOVESETS", "0") },
        ]),
        new("anim-map", "Weapon movesets", "Which DS3 animation replaces each move (R2, charged R2)", Stage.Build,
            "Some moves of a ported weapon are DS3 but others (often R2, charged R2 or two-handed attacks) still look like Elden Ring.",
        [
            new("A", "Behaviour selector names (default)", "An Elden Ring move gets the DS3 animation whose behaviour selector has the same name, when each game's selector holds exactly one animation; a two-handed move the names miss follows its one-handed twin."),
            new("B", "Names, then neighbours' offsets", "Moves the names don't pair take the ID offset their paired neighbours share (e.g. Elden Ring 0305xx -> DS3 0303xx), when that DS3 animation exists for the weapon.")
                { PorterEnv = Env("DARKSIGN_ANIM_MAP", "offsets") },
            new("C", "B plus the hand-checked heavy table", "B, plus the heavy-attack pairs found while porting Friede's scythe (Elden Ring 030500/030501/030505 = DS3 030320/030330/030321, two-handed 032xxx = DS3 034xxx), which win.")
                { PorterEnv = Env("DARKSIGN_ANIM_MAP", "table") },
            new("D", "Names only, no two-handed twins", "A, without pairing two-handed moves through their one-handed twins (Elden Ring 032xxx = DS3 034xxx) when the names don't pair them (DARKSIGN_TWOHAND_MAP=0).")
                { PorterEnv = Env("DARKSIGN_TWOHAND_MAP", "0") },
            new("E", "A without the paired dual-wield set", "A, but a paired weapon's two-handed moves (Elden Ring 032xxx) take DS3's 034xxx two-handed entries, which import the class's one-handed swings, instead of DS3's own dual-wield attacks (033xxx) (DARKSIGN_PAIRED_2H=0). Builds up to 1.0.217 did this: the Ringed Knight pair two-handed like one Ultra Greatsword.")
                { PorterEnv = Env("DARKSIGN_PAIRED_2H", "0") },
        ]),
        new("combo-windows", "Weapon movesets", "When the next attack can start (R1 chains)", Stage.Build,
            "R1 doesn't chain to the second swing, swings are cut short, or you can act too early or too late.",
        [
            new("A", "Elden Ring windows, stretched (default)", "Elden Ring's combo and cancel windows from the template move, stretched onto the DS3 swing between matching hits and recovery points. No window after the last hit opens before DS3's own first one, multi-hit DS3 swings anchor on their last hit, the template clip's length limits are cleared, Elden Ring's \"until the end\" (100 s) event ends don't count as the template's length, and a one-shot art's follow-up window (R1/R2) runs from DS3's first cancel after the last hit to the end of the DS3 move, so no move is cut short."),
            new("B", "DS3's own windows", "DS3's JumpTable events copied as they are (DARKSIGN_DS3_JUMPTABLES=1). Some DS3 IDs mean different things in Elden Ring.")
                { PorterEnv = Env("DARKSIGN_DS3_JUMPTABLES", "1") },
            new("C", "Elden Ring windows, shifted", "Elden Ring's windows moved to the first DS3 hit and not stretched (DARKSIGN_SWING_ALIGN=0), as the first moveset builds did.")
                { PorterEnv = Env("DARKSIGN_SWING_ALIGN", "0") },
            new("D", "As Friede's first clean R1 (1.0.89)", "A without the 2026-11-05 guard (DARKSIGN_TIMING_GUARD=0): windows may open before DS3's own recovery, the template clip's length limits stay, the 100 s event ends count as the template's length (stance attack windows shrink to a fraction of a second) and art follow-up windows sit at 45-90% of the move. Use it if A makes chains feel late.")
                { PorterEnv = Env("DARKSIGN_TIMING_GUARD", "0") },
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
            new("A", "Spline-compressed (default)", "DS3 animations re-encoded as Havok spline animations, like Elden Ring's own. Each bone's DS3 rotation is carried over relative to its own rest pose, so a bone that rests differently in the two skeletons (L_Weapon: 180° about X) moves the Elden Ring model as DS3's bone moved DS3's."),
            new("B", "Interleaved (uncompressed)", "Every frame stored as is (DARKSIGN_ANIM_INTERLEAVED=1). Exact, but about 5x bigger: the first build like this hung loading.")
                { PorterEnv = Env("DARKSIGN_ANIM_INTERLEAVED", "1") },
            new("C", "A, weapon bones from DS3", "A, but every shared bone copies DS3's bone rotation as it is (DARKSIGN_WEAPON_BONES=ds3), including L_Weapon, which rests turned 180° in DS3 and not in Elden Ring. Builds up to 1.0.221 did this: the Ringed Knight pair's left blade and Friede's second scythe pointed backwards in every DS3 move. Try it only to compare.")
                { PorterEnv = Env("DARKSIGN_WEAPON_BONES", "ds3") },
            new("D", "A, rest-different end bones frozen", "The way of 1.0.225 up to the previous build (DARKSIGN_WEAPON_BONES=freeze): shared end bones that rest differently keep Elden Ring's pose, every other bone copies DS3's rotation. It missed L_Weapon (not an end bone in Elden Ring), so the off-hand model stayed backwards; A instead carries every bone's rotation over relative to its rest pose.")
                { PorterEnv = Env("DARKSIGN_WEAPON_BONES", "freeze") },
        ]),
        new("weapon-hits", "Weapon movesets", "Hitboxes of DS3 swings", Stage.Build,
            "A ported weapon goes through enemies and walls, does no or odd damage, or staggers wrongly.",
        [
            new("A", "DS3 attack over the template's (default)", "Each swing's DS3 attack row (hit spheres on the weapon's dummy polys, motion value, damage type, wall hits) laid over the Elden Ring template weapon's attack for the same move, which fills the fields DS3 doesn't have."),
            new("B", "DS3 attack only", "The DS3 attack row alone; fields only Elden Ring has stay at their defaults (DARKSIGN_ATTACKS=ds3).")
                { PorterEnv = Env("DARKSIGN_ATTACKS", "ds3") },
            new("C", "Elden Ring template's attacks", "Every swing DS3 has uses the template weapon's own attack row for the same move (DARKSIGN_ATTACKS=template): Elden Ring hitboxes and damage. Tells whether DS3's attack rows or the dummy polys on the DS3 model are the problem.")
                { PorterEnv = Env("DARKSIGN_ATTACKS", "template") },
        ]),
        new("jump-attacks", "Weapon movesets", "Jump attacks", Stage.Build,
            "A ported weapon's jump attack plays again when you land, or the landing freezes.",
        [
            new("A", "Template clip settings (default)", "Each new behaviour clip shares its Elden Ring template clip's variable bindings, as copied."),
            new("B", "Start Time bound to TimeActEditor_10", "Every new clip of a jump attack (animation ID ending in 70) gets its own binding of Start Time to TimeActEditor_10, as Themyys' moveset guide says jump attacks need.")
                { PorterEnv = Env("DARKSIGN_JUMP_BINDING", "1") },
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
            new("A", "DS3 positions, matched by bone (default)", "Each model's DS3 dummy poly translated to the Elden Ring player dummy with the same ID at the same spot (else on the same bone), with DS3's show/hide bytes; paired weapons (left blade in the left hand) also get isDualBlade and DS3's two-handed grip. Friede's second scythe stays on the back one- and two-handed, as in DS3, and comes into the left hand only during the art: its hang slot is the left hand and the art moves it there with the 712 move Elden Ring's own arts make (ChangeType 3, as a612)."),
            new("B", "An Elden Ring split layout", "The weapon takes a whole WepAbsorpPosParam row from an Elden Ring weapon that already holds a second model in the left hand when two-handed (the game's own working layout).")
                { PorterEnv = Env("DARKSIGN_ABSORP", "er") },
            new("C", "DS3 dummy IDs as they are", "DS3's dummy poly IDs copied without matching them through the bones (both games number most hand and back dummies alike).")
                { PorterEnv = Env("DARKSIGN_ABSORP", "raw") },
            new("D", "A without the paired grip", "A's positions, but paired weapons keep the template's isDualBlade and hold motions (DARKSIGN_PAIRED_GRIP=0). Try it if two-handing a paired weapon looks wrong or crashes.")
                { PorterEnv = Env("DARKSIGN_PAIRED_GRIP", "0") },
            new("E", "A without the Friede layout", "A, but Friede's art keeps its DS3-only \"Friede override\" event as DS3 wrote it and the scythe's row is DS3's own (no left-hand slot for the art's second blade) (DARKSIGN_FRIEDE_OVERRIDE=keep).")
                { PorterEnv = Env("DARKSIGN_FRIEDE_OVERRIDE", "keep") },
            new("F", "Friede's art moves the scythe with ChangeType 1", "The 1.0.221-1.0.238 layout (DARKSIGN_FRIEDE_OVERRIDE=back): the second scythe stays on the back when two-handing and the art moves it to a left-hand slot with a move Elden Ring's own animations never make (ChangeType 1 on a right-hand model). It never showed the scythe in the art in those builds.")
                { PorterEnv = Env("DARKSIGN_FRIEDE_OVERRIDE", "back") },
            new("G", "Friede's second scythe in the left hand two-handed", "The previous build's layout (DARKSIGN_FRIEDE_OVERRIDE=both): Elden Ring's paired layout, the second scythe in the left hand when two-handing and in the art (ChangeType 2, as Rellana's twin blades). Try it if A's art shows no second scythe.")
                { PorterEnv = Env("DARKSIGN_FRIEDE_OVERRIDE", "both") },
            new("off", "Off (template positions)", "Ported weapons keep their template's WepAbsorpPosParam row (DARKSIGN_ABSORP=0): no split. Tells whether the split is what breaks something.")
                { PorterEnv = Env("DARKSIGN_ABSORP", "0") },
        ]),

        // ---- Weapon models ----
        new("weapon-textures", "Weapon models", "DS3 weapon colours (metal)", Stage.Build,
            "Ported weapons look dark grey and untextured, or metal parts look painted, too shiny or too bright.",
        [
            new("A", "Reflectance to metallic and base colour (default)", "Each DS3 reflectance map (_r) becomes Elden Ring's metallic map (_m), and the diffuse map (_a) takes the reflectance colour where the surface is metal, the usual specular-to-metallic conversion."),
            new("B", "Metallic only", "The metallic map is made from the reflectance the same way, but DS3's diffuse map is kept as it is (DARKSIGN_WEAPON_PBR=metal).")
                { PorterEnv = Env("DARKSIGN_WEAPON_PBR", "metal") },
            new("off", "Off (flat, not metal)", "The old way: a flat black metallic map and DS3's diffuse map (DARKSIGN_WEAPON_PBR=0). Metal blades render dark grey.")
                { PorterEnv = Env("DARKSIGN_WEAPON_PBR", "0") },
        ]),

        new("weapon-nodes", "Weapon models", "DS3 weapon model node roles", Stage.Build,
            "A ported weapon's swings do no damage, or its hit effect and sound play away from the blade (in the sky on big swings).",
        [
            new("A", "Elden Ring node flags (default)", "Each converted model's nodes get the role flags Elden Ring's own weapon models carry: the nodes dummy polys hang on are marked dummy owners, the mesh's node mesh and bone. DS3 models carry none, and 1.0.225 kept none, so the game had no owner for the hit spheres."),
            new("B", "Keep DS3's (none)", "The 1.0.225 behaviour: node flags left 0 as DS3 wrote them (DARKSIGN_NODE_FLAGS=0).")
                { PorterEnv = Env("DARKSIGN_NODE_FLAGS", "0") },
        ]),

        new("weapon-materials", "Weapon models", "DS3 weapon glow and detail layers", Stage.Build,
            "A ported weapon looks rusty, patchy or grey over its own texture, or a glowing DS3 part (Friede's frozen blade) doesn't glow.",
        [
            new("A", "Elden Ring vertex colours and emissive materials (default)", "Ported meshes take the template weapon's vertex colour, so Elden Ring's detail layers (rust, bone, leather) don't mix over the DS3 texture, and DS3 materials with their own glow map get a copy of an Elden Ring glowing weapon material."),
            new("B", "Keep DS3 vertex colours", "Glowing materials as in A, but DS3's own vertex colours are kept (DARKSIGN_VCOLOR=keep).")
                { PorterEnv = Env("DARKSIGN_VCOLOR", "keep") },
            new("C", "No glowing materials", "Vertex colours as in A, but every material uses the plain template material, no glow (DARKSIGN_EMISSIVE=0).")
                { PorterEnv = Env("DARKSIGN_EMISSIVE", "0") },
        ]),

        // ---- Weapon sounds and effects ----
        new("sounds", "Weapon sounds and effects", "DS3 weapon sounds", Stage.Build,
            "Swings are silent, play the wrong sound, or sound pitched down or muffled.",
        [
            new("A", "Embedded, own container, registered (default)", "DS3 samples decoded to PCM and embedded in cs_main.bnk, each Sound in a random container of its own under the template's actor-mixer (the layout Yonder and Spellforge use). The new sound numbers are listed in the game's own sound index (sound\\soundbanksinfo.mobnkinfo), the swing events are one-shot 129s that play to the end, and each sample also gets a loose copy in sd\\wem (what made Nightreign Ultimate Voices' new sounds play)."),
            new("B", "Streamed loose files", "Same layout, but each sample is a loose .wem in sd\\wem\\NN\\ that me3 streams (DARKSIGN_STREAM_SOUNDS=1), the way RogueRing ships its sounds.")
                { PorterEnv = Env("DARKSIGN_STREAM_SOUNDS", "1") },
            new("C", "Inside Elden Ring's whoosh container", "The Sound goes under the template's own container (DARKSIGN_SOUND_CONTAINERS=0), as builds up to 1.0.12x did. That container lowers the pitch and filters it.")
                { PorterEnv = Env("DARKSIGN_SOUND_CONTAINERS", "0") },
            new("D", "Played by our DLL (no Wwise)", "Elden Ring's sound bank is left alone. The DS3 samples are written as WAV files and the test menu DLL plays them on its own audio stream whenever a ported swing's sound event runs (DOOM RING's approach). Always audible, but not 3D-positioned. Elden Ring 2.7.2 only.")
                { PorterEnv = Env("DARKSIGN_SOUND_METHOD", "dll"), GameEnv = Env(DllSoundsVariable, "1") },
            new("E", "A bank of our own, loaded by Torrent", "cs_main.bnk is left exactly as the game has it. The DS3 sounds go into a bank of their own (sd\\cs_c9990.bnk, built like the community's custom banks) that Torrent's NpcParam rows load, the way Yonder's guide keeps a mod bank always loaded; the game's index lists it. Use it if A or B silence the whole game.")
                { PorterEnv = Env("DARKSIGN_SOUND_BANK", "own") },
            new("F", "As builds up to 1.0.161", "A without this round's changes: the game's sound index isn't edited, the swing events keep the template's playback settings, and there are no loose copies (DARKSIGN_SOUND_INDEX=0, DARKSIGN_SOUND_ONESHOT=0, DARKSIGN_SOUND_MIRROR=0). Use it if A breaks something B, C and E don't.")
                { PorterEnv = Env("DARKSIGN_SOUND_INDEX", "0", "DARKSIGN_SOUND_ONESHOT", "0", "DARKSIGN_SOUND_MIRROR", "0") },
            new("G", "Samples by the sound's name only", "A without DS3's event files (DARKSIGN_SOUND_FEV=0), as builds before them: a sound is the DS3 sample named like it. Sounds whose samples are named differently stay Elden Ring's or silent, and a sound made of several layers plays only one. Use it if ported sounds play the wrong sample or too many at once.")
                { PorterEnv = Env("DARKSIGN_SOUND_FEV", "0") },
            new("off", "Off (Elden Ring sounds)", "Ported weapons keep the Elden Ring template's swing sounds (DARKSIGN_DS3_SOUND_EVENTS=0). Not DS3, but never silent.")
                { PorterEnv = Env("DARKSIGN_DS3_SOUND_EVENTS", "0") },
        ]),
        new("effects", "Weapon sounds and effects", "DS3 weapon effects (trails, sparks)", Stage.Build,
            "The game crashes when a ported weapon swings, or its effects are missing or wrong.",
        [
            new("A", "DS3 effects (default)", "DS3's FXR effects converted to Elden Ring's format (@cccode/fxr), with the sounds they play."),
            new("B", "DS3 effects without their sounds", "Same effects; the sounds inside them are left out (DARKSIGN_FXR_SOUNDS=0).")
                { PorterEnv = Env("DARKSIGN_FXR_SOUNDS", "0") },
            new("C", "DS3 and Elden Ring effects together", "Moves spawn DS3's effects and keep the template's Elden Ring trails and sparks too (DARKSIGN_KEEP_ER_EFFECTS=1), so a DS3 effect that doesn't show still leaves Elden Ring's instead of nothing.")
                { PorterEnv = Env("DARKSIGN_KEEP_ER_EFFECTS", "1") },
            new("D", "DS3 effects with held sounds", "An effect's sound that DS3 holds while the effect lives stays held, not played once (DARKSIGN_FXR_HELD_SOUNDS=1). Such a sound can keep playing after its effect ends.")
                { PorterEnv = Env("DARKSIGN_FXR_HELD_SOUNDS", "1") },
            new("off", "Off (Elden Ring effects)", "Ported weapons keep the template's effects (DARKSIGN_DS3_EFFECT_EVENTS=0).")
                { PorterEnv = Env("DARKSIGN_DS3_EFFECT_EVENTS", "0") },
        ]),
        new("weapon-arts", "Weapon sounds and effects", "DS3 weapon arts (skills on L2)", Stage.Build,
            "L2 on a ported weapon does an Elden Ring Ash of War, nothing, the wrong move, the character freezes or T-poses, or the player can't move at all after loading (that last one means the hks: go to B).",
        [
            new("A", "DS3 arts with stances (default)", "Each DS3 weapon art (a weapon's own, or its class's skill such as Warcry or Stomp) becomes its own Ash of War with DS3's animations, events, FP cost and R1/R2 follow-ups, locked to the weapon. Stance skills (Friede's, the Great Scythe's) hold the stance with light and heavy follow-ups, which needs Mods\\ER\\action\\script\\c0000.hks (ividyon's community hks with the ported stances added)."),
            new("B", "DS3 arts without the hks", "Same arts, but no c0000.hks (DARKSIGN_WEAPON_ARTS=nohks): one-shot skills are unchanged, stance skills become a single attack (their stance light attack).")
                { PorterEnv = Env("DARKSIGN_WEAPON_ARTS", "nohks") },
            new("C", "DS3 arts, Ashes of War allowed", "Same as A, but ported weapons aren't locked to their art (DARKSIGN_WEAPON_ARTS=unlocked): Ashes of War and affinities can be put on them as on the Elden Ring weapon they're built from.")
                { PorterEnv = Env("DARKSIGN_WEAPON_ARTS", "unlocked") },
            new("D", "DS3 arts, stance attacks don't chain", "Same as A, but a stance's light attack doesn't chain into DS3's second and third (DARKSIGN_STANCE_CHAIN=0): R1 after the first stance attack is a plain R1, as in builds up to 1.0.238. Try it if the stance's second hit plays the wrong move or freezes the character.")
                { PorterEnv = Env("DARKSIGN_STANCE_CHAIN", "0") },
            new("off", "Off (Elden Ring Ashes of War)", "Ported weapons keep their template's Elden Ring Ash of War (DARKSIGN_WEAPON_ARTS=0), as in builds up to 1.0.169.")
                { PorterEnv = Env("DARKSIGN_WEAPON_ARTS", "0") },
        ]),
        new("weapon-effects", "Weapon sounds and effects", "DS3 weapon status and buffs (frostbite, bleed, poison)", Stage.Build,
            "A ported weapon builds up no status (Friede's scythe no frostbite), the wrong status, or holding or swinging it does something odd (health drains, stats change, the game crashes on hit).",
        [
            new("A", "Status, equipped effects and art buffs (default)", "DS3's status on hit, the effects a DS3 weapon gives while held and the buffs its weapon art casts, ported as new Elden Ring effects (status rows copied from Elden Ring's own bleed, frost and poison rows with DS3's buildup)."),
            new("B", "Status on hit only", "Only the status a weapon builds up on hit (DARKSIGN_WEAPON_EFFECTS=status); equipped effects and art buffs are left out.")
                { PorterEnv = Env("DARKSIGN_WEAPON_EFFECTS", "status") },
            new("off", "Off", "No DS3 effects (DARKSIGN_WEAPON_EFFECTS=0): ported weapons build up nothing, as in builds before 1.0.17x.")
                { PorterEnv = Env("DARKSIGN_WEAPON_EFFECTS", "0") },
        ]),
        new("weapon-visuals", "Weapon sounds and effects", "DS3 weapon glows, buff visuals and projectiles", Stage.Build,
            "A ported weapon shows no glow, buff effect or projectile where DS3 has one (the Moonlight Greatsword's wave), shows a wrong one, a projectile does odd damage, or the game crashes when one appears.",
        [
            new("A", "Effect visuals and projectiles (default)", "What DS3's effects look like (SpEffectVfxParam rows: blade glows, weapon-art buffs, worn effects) and the projectiles DS3 moves fire (Bullet rows with their effects, attacks and follow-up bullets), ported as new Elden Ring rows that play the converted DS3 effects."),
            new("B", "Effect visuals, no projectiles", "Glows and buff visuals as in A; moves that fire a projectile use the template weapon's bullet or none (DARKSIGN_BULLETS=0), as in builds up to 1.0.242.")
                { PorterEnv = Env("DARKSIGN_BULLETS", "0") },
            new("C", "Projectiles, no effect visuals", "Projectiles as in A; ported effects have no visuals of their own (DARKSIGN_EFFECT_VISUALS=0), as in builds up to 1.0.242.")
                { PorterEnv = Env("DARKSIGN_EFFECT_VISUALS", "0") },
            new("D", "No effects with 3D models", "As A, but DS3 effects that draw a 3D model (the Moonlight Greatsword's wave, Friede's ice) are left out (DARKSIGN_EFFECT_MODELS=0), as in builds up to 1.0.249. Try this first if the game crashes or shows a broken shape when such an effect plays.")
                { PorterEnv = Env("DARKSIGN_EFFECT_MODELS", "0") },
            new("E", "Every DS3 on-model effect", "As A, and Friede's Great Scythe keeps the frost mist DS3 plays on its main scythe at all times (DARKSIGN_RESIDENT_ALL=1), as in 1.0.249-1.0.255.")
                { PorterEnv = Env("DARKSIGN_RESIDENT_ALL", "1") },
            new("F", "Projectile events as DS3 writes them", "As A, but the event that fires a projectile is copied from DS3 byte for byte (DARKSIGN_BULLET_EVENTS=ds3) instead of being rewritten in Elden Ring's layout, as in builds up to 1.0.271.")
                { PorterEnv = Env("DARKSIGN_BULLET_EVENTS", "ds3") },
            new("G", "Projectiles without the weapon's attack power", "As A, but a projectile's hit doesn't count the weapon's own attack power or status build-up (DARKSIGN_BULLET_BASE_ATK=0), as in builds up to 1.0.279, where DS3 projectiles hit for nothing. Try this if a projectile hits far too hard.")
                { PorterEnv = Env("DARKSIGN_BULLET_BASE_ATK", "0") },
            new("H", "No projectiles for weapons built as their class", "As A, but a weapon whose own DS3 moves found no free motion category, and so is built as its class, loses the projectiles its own moves fire (DARKSIGN_BORROW_BULLETS=0), as in builds up to 1.0.282: the Moonlight Greatsword's charged heavy fired no wave.")
                { PorterEnv = Env("DARKSIGN_BORROW_BULLETS", "0") },
            new("off", "Off", "Neither (DARKSIGN_BULLETS=0 and DARKSIGN_EFFECT_VISUALS=0): only the effects in a move's own effect events show, as in builds up to 1.0.242.")
                { PorterEnv = Env("DARKSIGN_BULLETS", "0", "DARKSIGN_EFFECT_VISUALS", "0") },
        ]),

        // ---- DS3 maps ----
        new("map-slots", "DS3 maps", "Which Elden Ring map IDs hold the DS3 maps", Stage.Build,
            "Warping into a DS3 map fails (nothing happens, endless loading, crash, or you land in the colosseum).",
        [
            new("A", "Colosseum slots (default)", "Firelink/Cemetery of Ash, High Wall and Undead Settlement replace the three colosseum arenas (m45_00-02), which single-player never visits."),
            new("B", "Spare map IDs", "Each DS3 map gets a map ID of its own that Elden Ring lists but ships no map for (m11_70, m11_72, m19_70), built from the colosseums' file formats. Untested; IDs the game doesn't list at all crash it at boot.")
                { PorterEnv = Env("DARKSIGN_MAP_SLOTS", "new") },
        ]),
        new("run-start", "DS3 maps", "Getting into the DS3 run", Stage.Build,
            "Neither the F1 menu's Start button nor Ctrl+Shift+F1 takes you to the Cemetery of Ash.",
        [
            new("A", "On request (default)", "The F1 menu or Ctrl+Shift+F1 sets a request flag, and an event in common.emevd warps you."),
            new("B", "Automatically on load", "Also, every time a character loads outside a DS3 run, the event warps you there after 3 seconds, without any menu. Leave with the F1 menu or Ctrl+Shift+F2 (you'll be sent back on the next load).")
                { PorterEnv = Env("DARKSIGN_AUTO_DS3_RUN", "1") },
        ]),
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
            new("A", "Everything visible (default)", "Every map part draws everywhere in the map: map pieces carry no draw or display groups (as the colosseums' own parts have them), collisions and enemies have every group on."),
            new("B", "DS3's draw groups", "Parts keep DS3's draw and display groups (port-maps --ds3-groups), as DS3 culls them.")
                { MapsArgs = ["--ds3-groups"] },
            new("C", "No draw groups", "Parts carry no draw or display groups at all (port-maps --no-groups), as the colosseums' own parts and JortPob's maps have them.")
                { MapsArgs = ["--no-groups"] },
        ]),
        new("map-draw", "DS3 maps", "Draw distance settings of the DS3 map pieces", Stage.Build,
            "DS3 map geometry is invisible or pops in only when close.",
        [
            new("A", "The port's own row (default)", "Every map piece names a PartsDrawParam row the port adds (unlimited draw distance, no LOD switching, as JortPob's maps have)."),
            new("B", "A vanilla row", "Map pieces name Stormveil Castle's most common PartsDrawParam row, 2046 (port-maps --vanilla-draw-param), and nothing is added to the regulation.")
                { MapsArgs = ["--vanilla-draw-param"] },
        ]),
        new("map-materials", "DS3 maps", "Materials of the DS3 map pieces", Stage.Build,
            "DS3 map geometry is invisible, black, far too dark or shiny, or foliage and fences are solid cards.",
        [
            new("A", "Elden Ring's map shaders by kind (default)", "Each DS3 material is drawn with a copy of an Elden Ring map material of its kind: normal-mapped opaque, alpha-tested (foliage, grates), blended, and unlit (sky domes), each mesh rebuilt in that material's vertex layout."),
            new("B", "Albedo only", "Every DS3 material uses the albedo-only material JortPob builds its maps on (port-maps --plain-materials): no normal maps, cut-outs or unlit sky, but the simplest shader there is.")
                { MapsArgs = ["--plain-materials"] },
        ]),
        new("map-texture-detail", "DS3 maps", "Texture detail of the DS3 maps", Stage.Build,
            "A DS3 map hitches or stutters as you move through it, or its textures look blurrier up close than they did in 1.0.299.",
        [
            new("A", "Full textures near you, half size beyond (default)", "DS3's textures in full for map pieces within 128 m, and a half size low detail copy (a quarter of the memory) for everything further away, the way JortPob and Elden Ring's own maps keep a low detail copy."),
            new("B", "Half size textures", "Every DS3 map texture at half its width and height, and quarter size beyond 128 m (port-maps --half-textures): for a graphics card whose memory DS3's textures still don't fit in.")
                { MapsArgs = ["--half-textures"] },
            new("C", "Full size twice, as up to 1.0.299", "The low detail copy is the full texture again and full textures load within 256 m (port-maps --full-low-textures): every texture of a map sits in graphics memory twice at full size.")
                { MapsArgs = ["--full-low-textures"] },
        ]),
        new("map-lighting", "DS3 maps", "Lighting of the DS3 maps", Stage.Build,
            "A DS3 map looks wrong since it got its own lighting: washed out, lit from inside walls, lamps in the wrong places, or the game hangs or crashes while it loads.",
        [
            new("A", "Sunlight, ambient light and DS3's map lights (default)", "DS3's sky dome casts no shadow, so the host map's sun reaches the map. The host's environment map (its ambient light) applies in a box around the whole DS3 map. The lights of DS3's light file (soft fill lights, mostly indoors) replace the host's. Candles, torches and bonfires are DS3 objects with their own flame effects and are not part of this."),
            new("B", "The host map's lighting, untouched", "Nothing is changed (port-maps --host-lighting): the sky dome shadows the map, there is no ambient light and the lamps are the colosseum's. Dark, as every build up to 1.0.284 was.")
                { MapsArgs = ["--host-lighting"] },
        ]),
        new("map-sky", "DS3 maps", "Sky over the DS3 maps", Stage.Build,
            "A DS3 map has no sky at all (black or a flat colour overhead), or the game hangs or crashes while it loads since Elden Ring's sky was removed.",
        [
            new("A", "DS3's own sky (default)", "Elden Ring's distant view of the host map (its sky, the Erdtree and far scenery) is replaced with an empty one, so the only sky is the dome DS3 built into the map."),
            new("B", "The host map's sky", "The host's distant view is left alone (port-maps --host-sky): Elden Ring's sky and the Erdtree show over the DS3 map, as in every build up to 1.0.299.")
                { MapsArgs = ["--host-sky"] },
        ]),
        new("map-helper-pieces", "DS3 maps", "DS3's helper map pieces", Stage.Build,
            "Part of a DS3 map is missing since 1.0.300 (a wall, a floor, a whole building), or its shadows look wrong.",
        [
            new("A", "Left out (default)", "DS3 keeps pieces in its maps that are never drawn as scenery: low detail doubles of whole areas that only water reflections show, flat cards and boxes that only cast shadows, and pieces of another state of the map. They are left out. Work\\reports\\map-pieces-<map>.tsv lists every piece and why it was left out."),
            new("B", "Kept as ordinary pieces", "Every DS3 map piece is drawn (port-maps --helper-pieces), as in every build up to 1.0.299: blank slabs and blurry doubles of the ground and rocks stand in the maps, but nothing is missing.")
                { MapsArgs = ["--helper-pieces"] },
        ]),
        new("map-navmesh", "DS3 maps", "How enemies find their way in the DS3 maps", Stage.Build,
            "A DS3 map crashes or hangs while loading since launcher 1.0.300, enemies stand still or refuse to cross ground they should walk on, or they walk through closed doors.",
        [
            new("A", "DS3's navmeshes, every face plain ground (default)", "The map's navmesh list is built from DS3's, so the game loads DS3's navmeshes: enemies walk around walls and gaps instead of running straight at you. DS3's per-face marks (closed doors, ladders, drops) are cleared, because Elden Ring reads those bits its own way."),
            new("B", "DS3's navmeshes with DS3's face marks", "The same, but each face keeps DS3's mark (port-maps --navmesh-flags ds3). Try it if enemies walk where DS3 kept them out; if they then refuse ground they should cross, Elden Ring reads the marks differently.")
                { MapsArgs = ["--navmesh-flags", "ds3"] },
            new("off", "The host map's navmesh list, untouched", "The list stays the colosseum's, which names none of DS3's navmeshes (port-maps --host-navmesh-list): the game loads no navmesh and enemies run straight at you, as in every build up to 1.0.299.")
                { MapsArgs = ["--host-navmesh-list"] },
        ]),
        new("map-enemies", "DS3 maps", "Enemies and bosses in the DS3 maps", Stage.Build,
            "A DS3 map crashes or hangs while loading, enemies stand in walls or fall through the floor, or a boss fight never starts.",
        [
            new("A", "DS3 characters where ported, stand-ins elsewhere (default)", "Every DS3 enemy and boss stands where it did in DS3, with its DS3 entity ID so the converted boss and trap events find it. Characters the enemy porter has ported (darksign_chr_map.tsv) are the real DS3 ones; the rest are Elden Ring enemies of the same kind (knight, soldier, dog, dragon...), bosses get Elden Ring bosses."),
            new("B", "Elden Ring stand-ins only", "The same placements, but always the Elden Ring stand-ins, even for ported DS3 characters (port-maps --enemies standin). Tells whether a ported character is what breaks.")
                { MapsArgs = ["--enemies", "standin"] },
            new("off", "Off (no enemies)", "The DS3 maps are empty of enemies (port-maps --enemies none), to explore them or to find out whether enemies are what breaks a map.")
                { MapsArgs = ["--enemies", "none"] },
        ]),

        new("map-objects", "DS3 maps", "DS3 objects in the DS3 maps", Stage.Build,
            "A DS3 map crashes, hangs while loading or hitches since objects were added, objects float or are a mess of polygons, or something invisible blocks the way.",
        [
            new("A", "DS3's objects as Elden Ring assets (default)", "Every DS3 object (statues, candles, crates, doors, ladders, the bonfire's sword) is converted to an Elden Ring asset and stands where DS3 has it, with its DS3 entity ID so the converted events find it. Objects that never move or break also get DS3's collision; doors, lifts and crates can be walked through until their animations and breaking are ported. Report: Work\\reports\\object-port.tsv."),
            new("B", "Objects without collision", "The same objects, but none of them is solid (port-maps --objects nocollision). Tells whether an object's collision is what blocks the way or breaks the map.")
                { MapsArgs = ["--objects", "nocollision"] },
            new("off", "Off (no objects)", "The DS3 maps have no objects (port-maps --objects none), as every build up to 1.0.299 was.")
                { MapsArgs = ["--objects", "none"] },
        ]),
        new("map-regions", "DS3 maps", "DS3 regions, patrol routes and generators", Stage.Build,
            "A DS3 map crashes or hangs while loading since regions were added, enemies walk into walls or stand in a pile, or an event fires in the wrong place.",
        [
            new("A", "DS3's regions and map-file events (default)", "The areas DS3's events watch (boss rooms, ambush triggers, warp points), enemy patrol routes, platoons and enemy generators are carried into the map with their DS3 entity IDs."),
            new("B", "None", "The map file has no DS3 regions, patrol routes, platoons or generators (port-maps --no-regions), as every build up to 1.0.299 was: events that wait for the player to enter an area never fire and enemies don't patrol.")
                { MapsArgs = ["--no-regions"] },
        ]),

        new("enemy-port", "DS3 maps", "How DS3's enemies and bosses are ported", Stage.Build,
            "A ported DS3 enemy crashes the game when it loads, is invisible or a mess of stretched polygons, T-poses or moves wrongly, or the enemy-port report (Work\\reports\\enemy-port.tsv) lists many failures.",
        [
            new("A", "DS3 model and animations (default)", "Every DS3 character placed in DS3's maps gets its own Elden Ring character ID (c7000 and up), built on the closest Elden Ring enemy: DS3's model, textures and animations on that enemy's skeleton, behaviour and AI, with DS3's HP, souls and attacks."),
            new("B", "DS3 model, Elden Ring animations", "DS3's model and textures on the Elden Ring enemy, which keeps its own animations and timing (DARKSIGN_ENEMY_PORT=model). Use when ported characters look right standing still but move wrongly.")
                { PorterEnv = Env("DARKSIGN_ENEMY_PORT", "model") },
            new("C", "Elden Ring bodies with DS3 stats", "No new models: each DS3 character is the closest Elden Ring enemy with DS3's name, HP and souls (DARKSIGN_ENEMY_PORT=stats). Use when ported models crash or don't show.")
                { PorterEnv = Env("DARKSIGN_ENEMY_PORT", "stats") },
            new("off", "Off (plain stand-ins)", "No enemy port: the DS3 maps use Elden Ring enemies of the same kind as stand-ins (DARKSIGN_ENEMY_PORT=0).")
                { PorterEnv = Env("DARKSIGN_ENEMY_PORT", "0") },
        ]),
        new("enemy-model", "DS3 maps", "Materials and model data of ported DS3 enemies", Stage.Build,
            "A ported DS3 enemy is invisible, black, shiny or wears the wrong textures, shows all its weapons at once, has parts that stand still or stretch while it moves, vanishes at some camera angles, or cannot be hit or locked on to.",
        [
            new("A", "Plain Elden Ring character materials (default)", "Every DS3 material is drawn with Elden Ring's plain character shader (albedo, metallic, normal) and DS3's own textures; the model carries Elden Ring's node flags and culling boxes, DS3's display masks pick its weapon and armour variants, and the skeleton carries every DS3 bone (weapons, skirts, hats) next to the template's."),
            new("B", "The template enemy's materials", "As A, but each DS3 material is a copy of the Elden Ring template enemy's material with DS3's textures in it (DARKSIGN_ENEMY_MATERIALS=template), as builds up to 1.0.255 meant to do. Use when ported enemies are invisible, black or flicker with A.")
                { PorterEnv = Env("DARKSIGN_ENEMY_MATERIALS", "template") },
            new("C", "DS3's node flags and mesh boxes", "As A, but the model keeps DS3's node flags and mesh boxes (DARKSIGN_ENEMY_NODES=0), as in builds up to 1.0.255. Use when ported enemies crash on spawn, vanish at some camera angles or are stretched with A.")
                { PorterEnv = Env("DARKSIGN_ENEMY_NODES", "0") },
            new("D", "Template skeleton only", "As A, but the ported skeleton keeps only the Elden Ring template's bones (DARKSIGN_ENEMY_BONES=template), as in builds up to 1.0.263: DS3 weapons, skirts, hats and capes don't follow the animation. Use when ported enemies crash on spawn or when hit, or fold up, with A.")
                { PorterEnv = Env("DARKSIGN_ENEMY_BONES", "template") },
            new("E", "Template animations where DS3 has none", "As A, but a move of the Elden Ring template that DS3 has no move of the same ID for keeps the template's animation (DARKSIGN_ENEMY_ANIMS=template), as in builds up to 1.0.271, instead of playing the nearest DS3 move. Use when ported enemies freeze, slide or stand idle where they should step, fall or die with A.")
                { PorterEnv = Env("DARKSIGN_ENEMY_ANIMS", "template") },
            new("F", "DS3 textures next to the template's", "As A, but DS3's textures are a second file in the template's high-quality texture binder with flat 'not metal' maps (DARKSIGN_ENEMY_TEXTURES=added), as in builds up to 1.0.271, where ported enemies drew blank silver. Use when ported enemies crash on spawn or the game hangs loading them with A.")
                { PorterEnv = Env("DARKSIGN_ENEMY_TEXTURES", "added") },
        ]),
        new("enemy-drops", "DS3 maps", "What ported DS3 enemies drop", Stage.Build,
            "A ported DS3 enemy drops nothing where DS3 drops something, drops the wrong item or far too often, the game crashes or hangs when a ported enemy dies, or the build's enemy params step says the drops were not ported.",
        [
            new("A", "DS3's drops and chances (default)", "Every ported DS3 enemy drops what it drops in DS3, with DS3's chances: ported weapons, and Elden Ring items of the same name. A DS3 item that isn't ported yet (armour, rings, most goods) keeps its chance and gives nothing; Work\\reports\\enemy-drops.tsv lists them all."),
            new("B", "The Elden Ring template's drops", "Ported enemies keep the drops of the Elden Ring enemy they are built on (DARKSIGN_ENEMY_DROPS=template), as in every build before DS3 drops were ported.")
                { PorterEnv = Env("DARKSIGN_ENEMY_DROPS", "template") },
        ]),
        new("map-flags", "DS3 maps", "Where the DS3 maps' own flags are kept", Stage.Build,
            "In a DS3 map, bosses come back, opened doors and shortcuts close again or picked-up items return after a reload, or the pre-flight check says a map flag block is MISSING.",
        [
            new("A", "The slot map's own flags (default)", "A DS3 map's flags move with it into its Elden Ring slot's map flags (e.g. 4500xxxx for m45_00), the way Elden Ring's legacy dungeons number theirs."),
            new("B", "JortPob's saved blocks", "The maps' own flags get new numbers in the saved ranges of the bottom-edge sea tiles (1030294000 and up), the blocks JortPob's Morrowind port keeps all its flags in (DARKSIGN_MAP_FLAGS=common). Saves made with A lose their DS3 map progress.")
                { PorterEnv = Env("DARKSIGN_MAP_FLAGS", "common") },
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

    /// <summary>Game variable: the F1 menu removes spawned enemies instead of killing them (spawn-clear B).</summary>
    public const string SpawnRemoveVariable = "DKSIGN_SPAWN_REMOVE";

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

    /// <summary>The build methods a stamp from <see cref="BuildStamp"/> was built with ("" for all-A or no stamp).</summary>
    public static string SignatureOfStamp(string? stamp)
    {
        stamp = stamp?.Trim() ?? "";
        int open = stamp.IndexOf(" [", StringComparison.Ordinal);
        return open >= 0 && stamp.EndsWith(']') ? stamp[(open + 2)..^1] : "";
    }

    /// <summary>
    /// True when the build runs on one core (build-threads B): the launcher then also runs port-maps after
    /// port-weapons instead of side by side, so the build really is one step at a time.
    /// </summary>
    public static bool SerialBuild(IReadOnlyDictionary<string, string>? methods) =>
        PorterEnvironment(methods).TryGetValue("DARKSIGN_THREADS", out var threads) && threads == "1";

    /// <summary>The build stamp for a porter version and methods.</summary>
    public static string BuildStamp(string porterVersion, IReadOnlyDictionary<string, string>? methods) =>
        BuildSignature(methods) is { Length: > 0 } sig ? $"{porterVersion} [{sig}]" : porterVersion;
}
