# Fallback methods

Every feature of the Elden Ring mod can be done more than one way. In the launcher, **Fallbacks…** (next to
"Rebuild content") has one drop-down per feature. **A** is the default, and **B**, **C** and **D** are alternative
implementations. **off** turns a feature back to plain Elden Ring, to find out whether that feature is what breaks the game.

- Change one feature at a time, and play again.
- A **build** method rebuilds the content on the next PLAY, which takes a few minutes. A **launch** method only changes how
  the game starts.
- The status line says which methods are in use, and when a build method isn't A every porter log starts with `fallback methods: …`.
- Co-op partners need the same build methods. The mod code on the main window covers regulation.bin and the build methods, so it shows whether they match.

The list below is generated from `Launcher/src/DarkSign.Core/Fallbacks.cs`, the catalog the window reads. The
variable names in brackets are what the launcher passes to the porter or the game, for running the porter by hand.

## Starting the game

### Mod loader (`loader`, at launch, no rebuild)

Try the next option if: The game doesn't start, closes at the logo, hangs before the title screen, or the F1 menu never appears.

- **A: me3 (default).** me3 v0.13.0 loads the mod folder, the save redirect and the F1 menu. The only loader that plays modded sounds.
- **B: me3 with Arxan disabled.** Same, plus me3's disable_arxan: neutralises the game's code protection, which can undo or crash on hooks (the F1 menu and motion hooks are ours). (me3 `disable_arxan = true`)
- **C: ModEngine2 2.1.0.** The loader used up to 1.0.12x; it booted to the title screen with this mod. Modded sounds don't play under it (proven 2026-11-01), so swings are silent or Elden Ring's. (ModEngine2)
- **D: me3 without its memory patch.** me3 without mem_patch (its bigger game heap). Try only if A and B crash with an out-of-memory or allocator error in me3's log. (me3 `mem_patch = false`)

### F1 test menu (`test-menu`, at launch, no rebuild)

Try the next option if: The game crashes or freezes when the F1 menu opens or as soon as the game window shows, or F1 does nothing.

- **A: Overlay and hotkeys (default).** F1 opens the menu (drawn into the game's DirectX 12 frames). The hotkeys work too: Ctrl+Shift+F1 starts the DS3 run, Ctrl+Shift+F2 leaves it, Ctrl+Shift+1-9 warps to a ported map, Ctrl+Shift+F3 gives every DS3 weapon, Ctrl+Shift+F4 clears the enemies spawned from the menu.
- **B: Hotkeys only.** No overlay (nothing hooks DirectX 12); the same hotkeys do the main jobs. A beep means the game took it, two beeps that it couldn't (load a character first). (game `DKSIGN_NO_OVERLAY=1`)

### Clearing enemies spawned from the F1 menu (`spawn-clear`, at launch, no rebuild)

Try the next option if: "Clear enemies spawned from this menu" (or Ctrl+Shift+F4) leaves them standing, or the game crashes when they're cleared.

- **A: Kill them (default).** Their HP is set to 0, so the game kills them the normal way: they fall and fade out (and may drop runes).
- **B: Remove them.** The game's own RemoveChrIns takes them out of the world at once, no bodies (DKSIGN_SPAWN_REMOVE=1). Called from the menu's thread, not the game's, so it can crash: use it only if A doesn't clear them. (`DKSIGN_SPAWN_REMOVE=1`)

### Content build speed (`build-threads`, rebuilds the content)

Try the next option if: The content build fails or gives different results from one run to the next.

- **A: Every core (default).** port-weapons and port-maps run side by side, each on every core.
- **B: One core.** Everything one step at a time (DARKSIGN_THREADS=1), as builds were before 1.0.12x. Slower, but rules out a threading bug. (`DARKSIGN_THREADS=1`)
- **C: Collision one file at a time.** Every core, but map collision files are serialized one at a time under a lock (DARKSIGN_COLLISION_LOCK=1), as builds before 1.0.23x did. Minutes slower per map; rules the lock-free Havok serialization out if a map's collision misbehaves. (`DARKSIGN_COLLISION_LOCK=1`)


## Weapon movesets

### Where a weapon's DS3 moveset goes (`moveset-slot`, rebuilds the content)

Try the next option if: A ported weapon swings with Elden Ring's moves (R1 or R2), or with a different Elden Ring weapon's moves.

- **A: spAtkcategory (default).** The DS3 moveset goes in the weapon's Special Motion Category, the way Elden Ring's unique weapons and community moveset mods do it; the row keeps its template's motion category.
- **B: wepmotionCategory.** The DS3 moveset replaces the weapon's motion category itself (builds up to 1.0.12x); the template's special section, if it has one, is left in place. (`DARKSIGN_MOVESET_FIELD=wmc`)
- **C: Both fields.** The DS3 moveset goes in spAtkcategory and in wepmotionCategory, so whichever one the game reads for a move finds it. Class rules (powerstance, guard) then follow the new section, a copy of the template's. (`DARKSIGN_MOVESET_FIELD=both`)

### How many DS3 movesets fit (`moveset-capacity`, rebuilds the content)

Try the next option if: The game closes at startup after an update, or a weapon that newly has its own DS3 moves does not attack or is missing some moves.

- **A: Every moveset (default).** Every DS3 moveset gets a motion category: numbers 90 to 255 first, then 256 to 399 (the community's range for weapon movesets), and when the behaviour file's animation IDs run short the later movesets are built lean, with DS3's moves only; their other moves play from the weapon's Elden Ring class as before.
- **B: Categories up to 255 only.** Motion categories stop at 255 (DARKSIGN_WIDE_MOTION=0), as in builds up to 1.0.284: about 62 movesets fit and the remaining weapons swing with their DS3 class's moves. Try it if the game closes at startup or a weapon that newly got its own moves does not attack at all. (`DARKSIGN_WIDE_MOTION=0`)
- **C: Full copies only.** As A, but every moveset is a full copy of its Elden Ring template's category (DARKSIGN_LEAN_MOVESETS=0), as in builds up to 1.0.284; movesets the animation IDs have no room for are left out and those weapons use their class's. Try it if a weapon is missing moves it has nothing of its own for in DS3 (a roll attack, a jump attack, a guard counter). (`DARKSIGN_LEAN_MOVESETS=0`)

### Which DS3 animation replaces each move (R2, charged R2) (`anim-map`, rebuilds the content)

Try the next option if: Some moves of a ported weapon are DS3 but others (often R2, charged R2 or two-handed attacks) still look like Elden Ring.

- **A: Behaviour selector names (default).** An Elden Ring move gets the DS3 animation whose behaviour selector has the same name, when each game's selector holds exactly one animation; a two-handed move the names miss follows its one-handed twin.
- **B: Names, then neighbours' offsets.** Moves the names don't pair take the ID offset their paired neighbours share (e.g. Elden Ring 0305xx -> DS3 0303xx), when that DS3 animation exists for the weapon. (`DARKSIGN_ANIM_MAP=offsets`)
- **C: B plus the hand-checked heavy table.** B, plus the heavy-attack pairs found while porting Friede's scythe (Elden Ring 030500/030501/030505 = DS3 030320/030330/030321, two-handed 032xxx = DS3 034xxx), which win. (`DARKSIGN_ANIM_MAP=table`)
- **D: Names only, no two-handed twins.** A, without pairing two-handed moves through their one-handed twins (Elden Ring 032xxx = DS3 034xxx) when the names don't pair them (DARKSIGN_TWOHAND_MAP=0). (`DARKSIGN_TWOHAND_MAP=0`)
- **E: A without the paired dual-wield set.** A, but a paired weapon's two-handed moves (Elden Ring 032xxx) take DS3's 034xxx two-handed entries, which import the class's one-handed swings, instead of DS3's own dual-wield attacks (033xxx) (DARKSIGN_PAIRED_2H=0). Builds up to 1.0.217 did this: the Ringed Knight pair two-handed like one Ultra Greatsword. (`DARKSIGN_PAIRED_2H=0`)

### When the next attack can start (R1 chains) (`combo-windows`, rebuilds the content)

Try the next option if: R1 doesn't chain to the second swing, swings are cut short, or you can act too early or too late.

- **A: Elden Ring windows, stretched (default).** Elden Ring's combo and cancel windows from the template move, stretched onto the DS3 swing between matching hits and recovery points. No window after the last hit opens before DS3's own first one, multi-hit DS3 swings anchor on their last hit, the template clip's length limits are cleared, Elden Ring's "until the end" (100 s) event ends don't count as the template's length, and a one-shot art's follow-up window (R1/R2) runs from DS3's first cancel after the last hit to the end of the DS3 move, so no move is cut short.
- **B: DS3's own windows.** DS3's JumpTable events copied as they are (DARKSIGN_DS3_JUMPTABLES=1). Some DS3 IDs mean different things in Elden Ring. (`DARKSIGN_DS3_JUMPTABLES=1`)
- **C: Elden Ring windows, shifted.** Elden Ring's windows moved to the first DS3 hit and not stretched (DARKSIGN_SWING_ALIGN=0), as the first moveset builds did. (`DARKSIGN_SWING_ALIGN=0`)
- **D: As Friede's first clean R1 (1.0.89).** A without the 2026-11-05 guard (DARKSIGN_TIMING_GUARD=0): windows may open before DS3's own recovery, the template clip's length limits stay, the 100 s event ends count as the template's length (stance attack windows shrink to a fraction of a second) and art follow-up windows sit at 45-90% of the move. Use it if A makes chains feel late. (`DARKSIGN_TIMING_GUARD=0`)

### Moves DS3 doesn't have (Elden Ring-only actions) (`er-moves`, rebuilds the content)

Try the next option if: The game hangs while loading, or Elden Ring-only moves of a ported weapon (e.g. guard counter) T-pose or do nothing.

- **A: Import the template's animation (default).** The new category points at the template's own animation, as Elden Ring's categories do. Keeps the animation binder small.
- **B: Copy the template's animation.** A full copy under the new category (DARKSIGN_COPY_ER_ANIMS=1). Bigger binder (a 4x binder once hung loading). (`DARKSIGN_COPY_ER_ANIMS=1`)

### DS3 animation format (`anim-format`, rebuilds the content)

Try the next option if: DS3 moves look broken (twisting limbs, jitter, sliding) or the game hangs while loading.

- **A: Spline-compressed (default).** DS3 animations re-encoded as Havok spline animations, like Elden Ring's own. Each bone's DS3 rotation is carried over relative to its own rest pose, so a bone that rests differently in the two skeletons (L_Weapon: 180° about X) moves the Elden Ring model as DS3's bone moved DS3's.
- **B: Interleaved (uncompressed).** Every frame stored as is (DARKSIGN_ANIM_INTERLEAVED=1). Exact, but about 5x bigger: the first build like this hung loading. (`DARKSIGN_ANIM_INTERLEAVED=1`)
- **C: A, weapon bones from DS3.** A, but every shared bone copies DS3's bone rotation as it is (DARKSIGN_WEAPON_BONES=ds3), including L_Weapon, which rests turned 180° in DS3 and not in Elden Ring. Builds up to 1.0.221 did this: the Ringed Knight pair's left blade and Friede's second scythe pointed backwards in every DS3 move. Try it only to compare. (`DARKSIGN_WEAPON_BONES=ds3`)
- **D: A, rest-different end bones frozen.** The way of 1.0.225 up to the previous build (DARKSIGN_WEAPON_BONES=freeze): shared end bones that rest differently keep Elden Ring's pose, every other bone copies DS3's rotation. It missed L_Weapon (not an end bone in Elden Ring), so the off-hand model stayed backwards; A instead carries every bone's rotation over relative to its rest pose. (`DARKSIGN_WEAPON_BONES=freeze`)

### Jump attacks (`jump-attacks`, rebuilds the content)

Try the next option if: A ported weapon's jump attack plays again when you land, or the landing freezes.

- **A: Template clip settings (default).** Each new behaviour clip shares its Elden Ring template clip's variable bindings, as copied.
- **B: Start Time bound to TimeActEditor_10.** Every new clip of a jump attack (animation ID ending in 70) gets its own binding of Start Time to TimeActEditor_10, as Themyys' moveset guide says jump attacks need. (`DARKSIGN_JUMP_BINDING=1`)

### Hitboxes of DS3 swings (`weapon-hits`, rebuilds the content)

Try the next option if: A ported weapon goes through enemies and walls, does no or odd damage, or staggers wrongly.

- **A: DS3 attack over the template's (default).** Each swing's DS3 attack row (hit spheres on the weapon's dummy polys, motion value, damage type, wall hits) laid over the Elden Ring template weapon's attack for the same move, which fills the fields DS3 doesn't have.
- **B: DS3 attack only.** The DS3 attack row alone; fields only Elden Ring has stay at their defaults (DARKSIGN_ATTACKS=ds3). (`DARKSIGN_ATTACKS=ds3`)
- **C: Elden Ring template's attacks.** Every swing DS3 has uses the template weapon's own attack row for the same move (DARKSIGN_ATTACKS=template): Elden Ring hitboxes and damage. Tells whether DS3's attack rows or the dummy polys on the DS3 model are the problem. (`DARKSIGN_ATTACKS=template`)

### TAE event groups for new DS3 events (`event-groups`, rebuilds the content)

Try the next option if: DS3 effects or sounds of some moves never happen while others do, or a move freezes the character.

- **A: Add missing groups (default).** A DS3 event whose type the Elden Ring move doesn't use gets an event group copied from another move of the template.
- **B: Only the move's own groups.** Such events are left out (DARKSIGN_ADD_EVENT_GROUPS=0). (`DARKSIGN_ADD_EVENT_GROUPS=0`)


## Two-handed split

### Split weapons (Friede's scythe, twinblades) (`two-hand`, rebuilds the content)

Try the next option if: Two-handing a split weapon doesn't split it, or the second blade floats, sticks to the body or sits in the wrong hand.

- **A: DS3 positions, matched by bone (default).** Each model's DS3 dummy poly translated to the Elden Ring player dummy with the same ID at the same spot (else on the same bone), with DS3's show/hide bytes; paired weapons (left blade in the left hand) also get isDualBlade and DS3's two-handed grip. Friede's second scythe stays on the back one- and two-handed, as in DS3, and comes into the left hand only during the art: its hang slot is the left hand and the art moves it there with the 712 move Elden Ring's own arts make (ChangeType 3, as a612). DS3 did this through a hardcode Elden Ring lacks.
- **B: An Elden Ring split layout.** The weapon takes a whole WepAbsorpPosParam row from an Elden Ring weapon that already holds a second model in the left hand when two-handed (the game's own working layout). (`DARKSIGN_ABSORP=er`)
- **C: DS3 dummy IDs as they are.** DS3's dummy poly IDs copied without matching them through the bones (both games number most hand and back dummies alike). (`DARKSIGN_ABSORP=raw`)
- **D: A without the paired grip.** A's positions, but paired weapons keep the template's isDualBlade and hold motions (DARKSIGN_PAIRED_GRIP=0). Try it if two-handing a paired weapon looks wrong or crashes. (`DARKSIGN_PAIRED_GRIP=0`)
- **E: A without the Friede layout.** A, but Friede's art keeps its DS3-only "Friede override" event as DS3 wrote it and the scythe's row is DS3's own (no left-hand slot for the art's second blade) (DARKSIGN_FRIEDE_OVERRIDE=keep). (`DARKSIGN_FRIEDE_OVERRIDE=keep`)
- **F: Friede's art moves the scythe with ChangeType 1.** The 1.0.221-1.0.238 layout (DARKSIGN_FRIEDE_OVERRIDE=back): the second scythe stays on the back when two-handing and the art moves it to a left-hand slot with a move Elden Ring's own animations never make (712 ChangeType 1 on a right-hand model). It never showed the scythe in the art in those builds (1.0.217 kept DS3's display byte, 1.0.221 marked the slot shown, 1.0.225 wrote a condition byte that matches no model). (`DARKSIGN_FRIEDE_OVERRIDE=back`)
- **G: Friede's second scythe in the left hand two-handed.** The previous build's layout (DARKSIGN_FRIEDE_OVERRIDE=both): Elden Ring's paired layout, the second scythe in the left hand when two-handing and in the art (712 ChangeType 2, as Rellana's twin blades). Try it if A's art shows no second scythe. (`DARKSIGN_FRIEDE_OVERRIDE=both`)
- **off: Off (template positions).** Ported weapons keep their template's WepAbsorpPosParam row (DARKSIGN_ABSORP=0): no split. Tells whether the split is what breaks something. (`DARKSIGN_ABSORP=0`)


## Weapon models

### DS3 weapon colours (metal) (`weapon-textures`, rebuilds the content)

Try the next option if: Ported weapons look dark grey and untextured, or metal parts look painted, too shiny or too bright.

- **A: Reflectance to metallic and base colour (default).** Each DS3 reflectance map (_r) becomes Elden Ring's metallic map (_m), and the diffuse map (_a) takes the reflectance colour where the surface is metal, the usual specular-to-metallic conversion.
- **B: Metallic only.** The metallic map is made from the reflectance the same way, but DS3's diffuse map is kept as it is (DARKSIGN_WEAPON_PBR=metal). (`DARKSIGN_WEAPON_PBR=metal`)
- **off: Off (flat, not metal).** The old way: a flat black metallic map and DS3's diffuse map (DARKSIGN_WEAPON_PBR=0). Metal blades render dark grey. (`DARKSIGN_WEAPON_PBR=0`)


### DS3 weapon model node roles (`weapon-nodes`, rebuilds the content)

Try the next option if: A ported weapon's swings do no damage, or its hit effect and sound play away from the blade (in the sky on big swings).

- **A: Elden Ring node flags (default).** Each converted model's nodes get the role flags Elden Ring's own weapon models carry: the nodes dummy polys hang on are marked dummy owners, the mesh's node mesh and bone. DS3 models carry none, and 1.0.225 kept none, so the game had no owner for the hit spheres.
- **B: Keep DS3's (none).** The 1.0.225 behaviour: node flags left 0 as DS3 wrote them (DARKSIGN_NODE_FLAGS=0). (`DARKSIGN_NODE_FLAGS=0`)


### DS3 weapon glow and detail layers (`weapon-materials`, rebuilds the content)

Try the next option if: A ported weapon looks rusty, patchy or grey over its own texture, or a glowing DS3 part (Friede's frozen blade) doesn't glow.

- **A: Elden Ring vertex colours and emissive materials (default).** Ported meshes take the template weapon's vertex colour, so Elden Ring's detail layers (rust, bone, leather) don't mix over the DS3 texture, and DS3 materials with their own glow map get a copy of an Elden Ring glowing weapon material.
- **B: Keep DS3 vertex colours.** Glowing materials as in A, but DS3's own vertex colours are kept (DARKSIGN_VCOLOR=keep). (`DARKSIGN_VCOLOR=keep`)
- **C: No glowing materials.** Vertex colours as in A, but every material uses the plain template material, no glow (DARKSIGN_EMISSIVE=0). (`DARKSIGN_EMISSIVE=0`)


## Weapon sounds and effects

### DS3 weapon sounds (`sounds`, rebuilds the content)

Try the next option if: Swings are silent, play the wrong sound, or sound pitched down or muffled.

- **A: Embedded, own container, registered (default).** DS3 samples decoded to PCM and embedded in cs_main.bnk, each Sound in a random container of its own under the template's actor-mixer (the layout Yonder and Spellforge use). The new sound numbers are listed in the game's own sound index (sound\soundbanksinfo.mobnkinfo), the swing events are one-shot 129s that play to the end, and each sample also gets a loose copy in sd\wem (what made Nightreign Ultimate Voices' new sounds play).
- **B: Streamed loose files.** Same layout, but each sample is a loose .wem in sd\wem\NN\ that me3 streams (DARKSIGN_STREAM_SOUNDS=1), the way RogueRing ships its sounds. (`DARKSIGN_STREAM_SOUNDS=1`)
- **C: Inside Elden Ring's whoosh container.** The Sound goes under the template's own container (DARKSIGN_SOUND_CONTAINERS=0), as builds up to 1.0.12x did. That container lowers the pitch and filters it. (`DARKSIGN_SOUND_CONTAINERS=0`)
- **D: Played by our DLL (no Wwise).** Elden Ring's sound bank is left alone. The DS3 samples are written as WAV files and the test menu DLL plays them on its own audio stream whenever a ported swing's sound event runs (DOOM RING's approach). Always audible, but not 3D-positioned. Elden Ring 2.7.2 only. (`DARKSIGN_SOUND_METHOD=dll`, game `DKSIGN_DLL_SOUNDS=1`)
- **E: A bank of our own, loaded by Torrent.** cs_main.bnk is left exactly as the game has it. The DS3 sounds go into a bank of their own (sd\cs_c9990.bnk, built like the community's custom banks) that Torrent's NpcParam rows load, the way Yonder's guide keeps a mod bank always loaded; the game's index lists it. Use it if A or B silence the whole game. (`DARKSIGN_SOUND_BANK=own`)
- **F: As builds up to 1.0.161.** A without this round's changes: the game's sound index isn't edited, the swing events keep the template's playback settings, and there are no loose copies (DARKSIGN_SOUND_INDEX=0, DARKSIGN_SOUND_ONESHOT=0, DARKSIGN_SOUND_MIRROR=0). Use it if A breaks something B, C and E don't. (`DARKSIGN_SOUND_INDEX=0`, `DARKSIGN_SOUND_ONESHOT=0`, `DARKSIGN_SOUND_MIRROR=0`)
- **G: Samples by the sound's name only.** A without DS3's event files (DARKSIGN_SOUND_FEV=0), as builds before them: a sound is the DS3 sample named like it. Sounds whose samples are named differently stay Elden Ring's or silent, and a sound made of several layers plays only one. Use it if ported sounds play the wrong sample or too many at once. (`DARKSIGN_SOUND_FEV=0`)
- **off: Off (Elden Ring sounds).** Ported weapons keep the Elden Ring template's swing sounds (DARKSIGN_DS3_SOUND_EVENTS=0). Not DS3, but never silent. (`DARKSIGN_DS3_SOUND_EVENTS=0`)

### DS3 weapon effects (trails, sparks) (`effects`, rebuilds the content)

Try the next option if: The game crashes when a ported weapon swings, or its effects are missing or wrong.

- **A: DS3 effects (default).** DS3's FXR effects converted to Elden Ring's format (@cccode/fxr), with the sounds they play.
- **B: DS3 effects without their sounds.** Same effects; the sounds inside them are left out (DARKSIGN_FXR_SOUNDS=0). (`DARKSIGN_FXR_SOUNDS=0`)
- **C: DS3 and Elden Ring effects together.** Moves spawn DS3's effects and keep the template's Elden Ring trails and sparks too (DARKSIGN_KEEP_ER_EFFECTS=1), so a DS3 effect that doesn't show still leaves Elden Ring's instead of nothing. (`DARKSIGN_KEEP_ER_EFFECTS=1`)
- **D: DS3 effects with held sounds.** An effect's sound that DS3 holds while the effect lives stays held, not played once (DARKSIGN_FXR_HELD_SOUNDS=1). Such a sound can keep playing after its effect ends. (`DARKSIGN_FXR_HELD_SOUNDS=1`)
- **off: Off (Elden Ring effects).** Ported weapons keep the template's effects (DARKSIGN_DS3_EFFECT_EVENTS=0). (`DARKSIGN_DS3_EFFECT_EVENTS=0`)

### DS3 weapon arts (skills on L2) (`weapon-arts`, rebuilds the content)

Try the next option if: L2 on a ported weapon does an Elden Ring Ash of War, nothing, the wrong move, the character freezes or T-poses, or the player can't move at all after loading (that last one means the hks: go to B).

- **A: DS3 arts with stances (default).** Each DS3 weapon art (a weapon's own section, or for most weapons its class's skill: Warcry, Stomp, Spin Slash, ...) becomes its own Ash of War, locked to the weapon (no Ashes of War or affinities, like Elden Ring's unique weapons), with R1/R2 follow-ups where DS3 has them (art IDs 300-398, then 170-254, then any other ID c0000.hks gives no behaviour of its own; TAE sections 600 + ID; SwordArtsParam rows 90000+) with DS3's animations, events, FP costs (DS3 SwordArtsParam) and skill name. Stance skills (Friede's, the Great Scythe's) hold the stance with light and heavy follow-ups, which needs `Mods\ER\action\script\c0000.hks` (ividyon's community hks with the ported stances added to IsStanceArts). The port-weapons log lists every art (`arts: ...`).
- **B: DS3 arts without the hks.** Same arts, but no c0000.hks (DARKSIGN_WEAPON_ARTS=nohks): one-shot skills are unchanged, stance skills become a single attack (their stance light attack). (`DARKSIGN_WEAPON_ARTS=nohks`)
- **C: DS3 arts, Ashes of War allowed.** Same as A, but ported weapons aren't locked to their art (DARKSIGN_WEAPON_ARTS=unlocked): Ashes of War and affinities can be put on them as on the Elden Ring weapon they're built from. (`DARKSIGN_WEAPON_ARTS=unlocked`)
- **D: DS3 arts, stance attacks don't chain.** Same as A, but a stance's light attack doesn't chain into DS3's second and third (DARKSIGN_STANCE_CHAIN=0): R1 after the first stance attack is a plain R1, as in builds up to 1.0.238. With A, a DS3 stance with more light attacks (Friede's 36230 -> 36250 -> 36290) chains through Elden Ring's own chain for Rellana's Moon-and-Fire Stance (hks art 318: stance light -> ComboEnd 40010 -> ComboEnd_2 40020 behind combo windows 100054 / 100055, the window being DS3's own transition marker SpEffect 100500); the hks tests of art 318 that name that chain are widened to the ported stances. Try D if the stance's second hit plays the wrong move or freezes the character. (`DARKSIGN_STANCE_CHAIN=0`)
- **off: Off (Elden Ring Ashes of War).** Ported weapons keep their template's Elden Ring Ash of War (DARKSIGN_WEAPON_ARTS=0), as in builds up to 1.0.169. (`DARKSIGN_WEAPON_ARTS=0`)

### DS3 weapon status and buffs (frostbite, bleed, poison) (`weapon-effects`, rebuilds the content)

Try the next option if: A ported weapon builds up no status (Friede's scythe no frostbite), the wrong status, or holding or swinging it does something odd (health drains, stats change, the game crashes on hit).

- **A: Status, equipped effects and art buffs (default).** DS3's status on hit, the effects a DS3 weapon gives while held and the buffs its weapon art casts, ported as new Elden Ring effects (status rows copied from Elden Ring's own bleed, frost and poison rows with DS3's buildup). Report: `TheDarkSign\Work\reports\weapon-effects.tsv`.
- **B: Status on hit only.** Only the status a weapon builds up on hit (DARKSIGN_WEAPON_EFFECTS=status); equipped effects and art buffs are left out. (`DARKSIGN_WEAPON_EFFECTS=status`)
- **off: Off.** No DS3 effects (DARKSIGN_WEAPON_EFFECTS=0): ported weapons build up nothing, as in builds before 1.0.17x. (`DARKSIGN_WEAPON_EFFECTS=0`)

### DS3 weapon glows, buff visuals and projectiles (`weapon-visuals`, rebuilds the content)

Try the next option if: A ported weapon shows no glow, buff effect or projectile where DS3 has one (the Moonlight Greatsword's wave), shows a wrong one, a projectile does odd damage, or the game crashes when one appears.

- **A: Effect visuals and projectiles (default).** What DS3's effects look like (SpEffectVfxParam rows: blade glows, weapon-art buffs, worn effects) and the projectiles DS3 moves fire (Bullet rows with their effects, attacks and follow-up bullets), ported as new Elden Ring rows that play the converted DS3 effects. Report per weapon: `TheDarkSign\Work\reports\weapon-fx.tsv` (which effects each weapon has, how many were converted, why the rest were left out).
- **B: Effect visuals, no projectiles.** Glows and buff visuals as in A; moves that fire a projectile use the template weapon's bullet or none (DARKSIGN_BULLETS=0), as in builds up to 1.0.242. (`DARKSIGN_BULLETS=0`)
- **C: Projectiles, no effect visuals.** Projectiles as in A; ported effects have no visuals of their own (DARKSIGN_EFFECT_VISUALS=0), as in builds up to 1.0.242. (`DARKSIGN_EFFECT_VISUALS=0`)
- **D: No effects with 3D models.** As A, but DS3 effects that draw a 3D model (the Moonlight Greatsword's wave, Friede's ice) are left out (DARKSIGN_EFFECT_MODELS=0), as in builds up to 1.0.249. Try this first if the game crashes or shows a broken shape when such an effect plays. (`DARKSIGN_EFFECT_MODELS=0`)
- **E: Every DS3 on-model effect.** As A, and Friede's Great Scythe keeps the frost mist DS3 plays on its main scythe at all times (DARKSIGN_RESIDENT_ALL=1), as in 1.0.249-1.0.255. (`DARKSIGN_RESIDENT_ALL=1`)
- **F: Projectile events as DS3 writes them.** As A, but the event that fires a projectile is copied from DS3 byte for byte (DARKSIGN_BULLET_EVENTS=ds3) instead of being rewritten in Elden Ring's layout, as in builds up to 1.0.271. (`DARKSIGN_BULLET_EVENTS=ds3`)
- **G: Projectiles without the weapon's attack power.** As A, but a projectile's hit doesn't count the weapon's own attack power or status build-up (DARKSIGN_BULLET_BASE_ATK=0), as in builds up to 1.0.279, where DS3 projectiles hit for nothing. Try this if a projectile hits far too hard. (`DARKSIGN_BULLET_BASE_ATK=0`)
- **H: No projectiles for weapons built as their class.** As A, but a weapon whose own DS3 moves found no free motion category, and so is built as its class, loses the projectiles its own moves fire (DARKSIGN_BORROW_BULLETS=0), as in builds up to 1.0.282: the Moonlight Greatsword's charged heavy fired no wave. (`DARKSIGN_BORROW_BULLETS=0`)
- **off: Off.** Neither (DARKSIGN_BULLETS=0 and DARKSIGN_EFFECT_VISUALS=0): only the effects in a move's own effect events show, as in builds up to 1.0.242. (`DARKSIGN_BULLETS=0`, `DARKSIGN_EFFECT_VISUALS=0`)


## DS3 maps

### Which Elden Ring map IDs hold the DS3 maps (`map-slots`, rebuilds the content)

Try the next option if: Warping into a DS3 map fails (nothing happens, endless loading, crash, or you land in the colosseum).

- **A: Colosseum slots (default).** Firelink/Cemetery of Ash, High Wall and Undead Settlement replace the three colosseum arenas (m45_00-02), which single-player never visits.
- **B: Spare map IDs.** Each DS3 map gets a map ID of its own that Elden Ring lists in its map registry but ships no map for (m11_70, m11_72, m19_70), built from the colosseums' file formats. Untested in game. A map ID the game doesn't list at all is not an option: registering one crashed the game at boot (1.0.238); see `docs/maps/own-map-ids.md`. (`DARKSIGN_MAP_SLOTS=new`)

### Getting into the DS3 run (`run-start`, rebuilds the content)

Try the next option if: Neither the F1 menu's Start button nor Ctrl+Shift+F1 takes you to the Cemetery of Ash.

- **A: On request (default).** The F1 menu or Ctrl+Shift+F1 sets a request flag, and an event in common.emevd warps you.
- **B: Automatically on load.** Also, every time a character loads outside a DS3 run, the event warps you there after 3 seconds, without any menu. Leave with the F1 menu or Ctrl+Shift+F2 (you'll be sent back on the next load). (`DARKSIGN_AUTO_DS3_RUN=1`)

### Map collision format (`map-collision`, rebuilds the content)

Try the next option if: You fall through the floor of a DS3 map, or the game hangs or crashes while a DS3 map loads.

- **A: XML tagfiles (default).** DS3 collision converted to Havok 2018 and stored as XML tagfiles, which JortPob says Elden Ring reads.
- **B: Binary tagfiles.** The same collision written as binary Havok tagfiles, like Elden Ring's own (port-maps --binary-collision). (`port-maps --binary-collision`)
- **C: Binary, keeping DS3's SIMD tree.** Binary tagfiles that keep DS3's SIMD collision tree instead of building a new one (--binary-collision --simd). (`port-maps --binary-collision`, `port-maps --simd`)

### Which map parts are drawn (`map-groups`, rebuilds the content)

Try the next option if: Parts of a DS3 map are invisible or flicker in and out as you move.

- **A: Everything visible (default).** Every map part draws everywhere in the map: map pieces carry no draw or display groups (as the colosseums' own parts have them), collisions and enemies have every group on.
- **B: DS3's draw groups.** Parts keep DS3's draw and display groups (port-maps --ds3-groups), as DS3 culls them. (`port-maps --ds3-groups`)
- **C: No draw groups.** Parts carry no draw or display groups at all (port-maps --no-groups), as the colosseums' own parts and JortPob's maps have them. (`port-maps --no-groups`)

### Draw distance settings of the DS3 map pieces (`map-draw`, rebuilds the content)

Try the next option if: DS3 map geometry is invisible or pops in only when close.

- **A: The port's own row (default).** Every map piece names a PartsDrawParam row the port adds (unlimited draw distance, no LOD switching, as JortPob's maps have).
- **B: A vanilla row.** Map pieces name Stormveil Castle's most common PartsDrawParam row, 2046 (port-maps --vanilla-draw-param), and nothing is added to the regulation. (`port-maps --vanilla-draw-param`)

### Materials of the DS3 map pieces (`map-materials`, rebuilds the content)

Try the next option if: DS3 map geometry is invisible, black, far too dark or shiny, or foliage and fences are solid cards.

- **A: Elden Ring's map shaders by kind (default).** Each DS3 material is drawn with a copy of an Elden Ring map material of its kind: normal-mapped opaque, alpha-tested (foliage, grates), blended, and unlit (sky domes), each mesh rebuilt in that material's vertex layout.
- **B: Albedo only.** Every DS3 material uses the albedo-only material JortPob builds its maps on (port-maps --plain-materials): no normal maps, cut-outs or unlit sky, but the simplest shader there is. (`port-maps --plain-materials`)

### Lighting of the DS3 maps (`map-lighting`, rebuilds the content)

Try the next option if: A DS3 map looks wrong since it got its own lighting: washed out, lit from inside walls, lamps in the wrong places, or the game hangs or crashes while it loads.

- **A: Sunlight, ambient light and DS3's lamps (default).** DS3's sky dome casts no shadow, so the host map's sun reaches the map. The host's environment map (its ambient light) applies in a box around the whole DS3 map. DS3's own lamps (candles, bonfires, torches) replace the host's.
- **B: The host map's lighting, untouched.** Nothing is changed (port-maps --host-lighting): the sky dome shadows the map, there is no ambient light and the lamps are the colosseum's. Dark, as every build up to 1.0.284 was. (`port-maps --host-lighting`)

### Enemies and bosses in the DS3 maps (`map-enemies`, rebuilds the content)

Try the next option if: A DS3 map crashes or hangs while loading, enemies stand in walls or fall through the floor, or a boss fight never starts.

- **A: DS3 characters where ported, stand-ins elsewhere (default).** Every DS3 enemy and boss stands where it did in DS3, with its DS3 entity ID so the converted boss and trap events find it. Characters the enemy porter has ported (darksign_chr_map.tsv) are the real DS3 ones; the rest are Elden Ring enemies of the same kind (knight, soldier, dog, dragon...), bosses get Elden Ring bosses.
- **B: Elden Ring stand-ins only.** The same placements, but always the Elden Ring stand-ins, even for ported DS3 characters (port-maps --enemies standin). Tells whether a ported character is what breaks. (`port-maps --enemies standin`)
- **off: Off (no enemies).** The DS3 maps are empty of enemies (port-maps --enemies none), to explore them or to find out whether enemies are what breaks a map. (`port-maps --enemies none`)

### How DS3's enemies and bosses are ported (`enemy-port`, rebuilds the content)

Try the next option if: A ported DS3 enemy crashes the game when it loads, is invisible or a mess of stretched polygons, T-poses or moves wrongly, or the enemy-port report (Work\reports\enemy-port.tsv) lists many failures.

- **A: DS3 model and animations (default).** Every DS3 character placed in DS3's maps gets its own Elden Ring character ID (c7000 and up), built on the closest Elden Ring enemy: DS3's model, textures and animations on that enemy's skeleton, behaviour and AI, with DS3's HP, souls and attacks.
- **B: DS3 model, Elden Ring animations.** DS3's model and textures on the Elden Ring enemy, which keeps its own animations and timing (DARKSIGN_ENEMY_PORT=model). Use when ported characters look right standing still but move wrongly. (`DARKSIGN_ENEMY_PORT=model`)
- **C: Elden Ring bodies with DS3 stats.** No new models: each DS3 character is the closest Elden Ring enemy with DS3's name, HP and souls (DARKSIGN_ENEMY_PORT=stats). Use when ported models crash or don't show. (`DARKSIGN_ENEMY_PORT=stats`)
- **off: Off (plain stand-ins).** No enemy port: the DS3 maps use Elden Ring enemies of the same kind as stand-ins (DARKSIGN_ENEMY_PORT=0). (`DARKSIGN_ENEMY_PORT=0`)

### Materials and model data of ported DS3 enemies (`enemy-model`, rebuilds the content)

Try the next option if: A ported DS3 enemy is invisible, black, shiny or wears the wrong textures, shows all its weapons at once, has parts that stand still or stretch while it moves, vanishes at some camera angles, or cannot be hit or locked on to.

- **A: Plain Elden Ring character materials (default).** Every DS3 material is drawn with Elden Ring's plain character shader (albedo, metallic, normal) and DS3's own textures; the model carries Elden Ring's node flags and culling boxes, DS3's display masks pick its weapon and armour variants, and the skeleton carries every DS3 bone (weapons, skirts, hats) next to the template's.
- **B: The template enemy's materials.** As A, but each DS3 material is a copy of the Elden Ring template enemy's material with DS3's textures in it (DARKSIGN_ENEMY_MATERIALS=template), as builds up to 1.0.255 meant to do. Use when ported enemies are invisible, black or flicker with A. (`DARKSIGN_ENEMY_MATERIALS=template`)
- **C: DS3's node flags and mesh boxes.** As A, but the model keeps DS3's node flags and mesh boxes (DARKSIGN_ENEMY_NODES=0), as in builds up to 1.0.255. Use when ported enemies crash on spawn, vanish at some camera angles or are stretched with A. (`DARKSIGN_ENEMY_NODES=0`)
- **D: Template skeleton only.** As A, but the ported skeleton keeps only the Elden Ring template's bones (DARKSIGN_ENEMY_BONES=template), as in builds up to 1.0.263: DS3 weapons, skirts, hats and capes don't follow the animation. Use when ported enemies crash on spawn or when hit, or fold up, with A. (`DARKSIGN_ENEMY_BONES=template`)
- **E: Template animations where DS3 has none.** As A, but a move of the Elden Ring template that DS3 has no move of the same ID for keeps the template's animation (DARKSIGN_ENEMY_ANIMS=template), as in builds up to 1.0.271, instead of playing the nearest DS3 move. Use when ported enemies freeze, slide or stand idle where they should step, fall or die with A. (`DARKSIGN_ENEMY_ANIMS=template`)
- **F: DS3 textures next to the template's.** As A, but DS3's textures are a second file in the template's high-quality texture binder with flat 'not metal' maps (DARKSIGN_ENEMY_TEXTURES=added), as in builds up to 1.0.271, where ported enemies drew blank silver. Use when ported enemies crash on spawn or the game hangs loading them with A. (`DARKSIGN_ENEMY_TEXTURES=added`)

### What ported DS3 enemies drop (`enemy-drops`, rebuilds the content)

Try the next option if: A ported DS3 enemy drops nothing where DS3 drops something, drops the wrong item or far too often, the game crashes or hangs when a ported enemy dies, or the build's enemy params step says the drops were not ported.

- **A: DS3's drops and chances (default).** Every ported DS3 enemy drops what it drops in DS3, with DS3's chances: ported weapons, and Elden Ring items of the same name. A DS3 item that isn't ported yet (armour, rings, most goods) keeps its chance and gives nothing; Work\reports\enemy-drops.tsv lists them all.
- **B: The Elden Ring template's drops.** Ported enemies keep the drops of the Elden Ring enemy they are built on (DARKSIGN_ENEMY_DROPS=template), as in every build before DS3 drops were ported. (`DARKSIGN_ENEMY_DROPS=template`)

### Where the DS3 maps' own flags are kept (`map-flags`, rebuilds the content)

Try the next option if: In a DS3 map, bosses come back, opened doors and shortcuts close again or picked-up items return after a reload, or the pre-flight check says a map flag block is MISSING.

- **A: The slot map's own flags (default).** A DS3 map's flags move with it into its Elden Ring slot's map flags (e.g. 4500xxxx for m45_00), the way Elden Ring's legacy dungeons number theirs.
- **B: JortPob's saved blocks.** The maps' own flags get new numbers in the saved ranges of the bottom-edge sea tiles (1030294000 and up), the blocks JortPob's Morrowind port keeps all its flags in (DARKSIGN_MAP_FLAGS=common). Saves made with A lose their DS3 map progress. (`DARKSIGN_MAP_FLAGS=common`)


## Seamless Co-op

### How Seamless Co-op is loaded (`seamless`, at launch, no rebuild)

Try the next option if: With Seamless Co-op ticked: the game won't start, Seamless' menu doesn't appear, or you can't connect.

- **A: me3 native, offline start (default).** me3 loads your ersc.dll before our DLLs; me3 starts the game offline and Seamless uses Steam's networking.
- **B: me3 native, online start.** Same, but me3 starts the game online (start_online = true), in case Seamless needs the game's network layer up. Our DLL still blocks FromSoftware's servers outside co-op. (me3 `start_online = true`)
- **C: ModEngine2.** Load Seamless through ModEngine2's external_dlls, the way Seamless' own docs show (no modded sounds under ModEngine2). (ModEngine2)

