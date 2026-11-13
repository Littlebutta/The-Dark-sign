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

### Bosses spawned from the F1 menu (`spawn-boss-kits`, at launch, no rebuild)

Try the next option if: You want to try a DS3 boss's partners (Pontiff's clone, Friede's later stages) from the F1 spawn list instead of in the boss's own arena.

- **A: The boss alone (default).** A DS3 boss spawned from the F1 menu comes alone. Its phases and partners run from DS3's map scripts, which never see characters the menu spawns, so fight a boss through all its phases in its own arena (the DS3 run tab's boss warps).
- **B: The boss with its partners.** As up to 1.0.410: the boss comes with the partners its DS3 fight uses (DKSIGN_BOSS_KITS=1), all in plain sight, because the scripts that hide them and bring them in don't run for spawned characters. (`DKSIGN_BOSS_KITS=1`)

### Enemies that grow out of another in the F1 menu (`spawn-transform`, at launch, no rebuild)

Try the next option if: The Pus of Man spawned from the F1 menu never turns up, turns up in the wrong place, or the game crashes when the hollow transforms.

- **A: A hollow first, then the Pus of Man (default).** The Pus of Man spawns as the High Wall hollow it bursts out of; 8 seconds later, or 1.5 seconds after the hollow is first hit, the Pus of Man takes its place and the hollow is taken away. Killed before that, the hollow just dies.
- **B: The Pus of Man at once.** As up to 1.0.422: the Pus of Man spawns as itself (DKSIGN_NO_TRANSFORM=1). (`DKSIGN_NO_TRANSFORM=1`)

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
- **F: No DS3 moves through a named file.** As A, but a DS3 move whose TAE entry plays another entry's file (its header names it: the Moonlight Greatsword's a158_030320 plays a025_030320 with the wave's bullet added, Smough's a070_030020 plays 030010's motion) keeps Elden Ring's animation and loses its DS3 events (DARKSIGN_NAMED_FILES=0), as in builds up to 1.0.385. With A the named file is converted (the weapon arts did this already). Use F if such moves play wrong. (`DARKSIGN_NAMED_FILES=0`)

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
- **D: Show parts DS3 hides by animation.** As A, but parts DS3 hides with the weapon's idle animation are drawn (DARKSIGN_HIDDEN_PARTS=0), as in builds up to 1.0.378. With A, the bones the idle animation (a999_000000 in the weapon's parts .anibnd, which Elden Ring can't load) scales to almost nothing have their vertices pulled to the bone, as the scale does in DS3: the Black Blade, Chaos Blade, Darkdrift, Frayed Blade and Washing Pole keep a full scabbard on bone "Case" in model 0. Use D if part of a ported weapon has gone missing. (`DARKSIGN_HIDDEN_PARTS=0`)
- **E: Keep DS3's node rotations.** As A, but ported weapon models keep DS3's node transforms (DARKSIGN_BAKE_NODES=0), as in builds up to 1.0.385. With A, each rigid mesh's vertices (with normals and tangents) and the dummy polys are moved into model space through their node, and every node is left at identity like Elden Ring's own weapons: DS3 turns Havel's Greatshield 90° and the Knight Shield about 25° with the root node, and the ported shields were held turned by exactly that. A also renames nodes whose names repeat ignoring case (Dummy_SFX / Dummy_Sfx on the Immolation Tinder and four others) and writes each mesh's culling box the way Elden Ring reads it (centre, half size, rotation; it held DS3's corners with a zero centre, so weapons could vanish when the game culled them). Use E if a weapon is held at an odd angle or its hit or effect points moved. (`DARKSIGN_BAKE_NODES=0`)
- **F: Shields keep DS3's hold turn.** As A, but ported shields are not turned onto Elden Ring's hold point (DARKSIGN_SHIELD_HOLD=0), as up to 1.0.410. A weapon sits on a dummy poly of the player model, in its frame: DS3 holds a shield on dummy 23 on L_Weapon (up +Z), Elden Ring's shield rows on dummy 98 or 23 on L_Shield (up in the XY plane), about 90° apart about forward, so every ported shield was held turned about 90° about its face (most visible when guarding, which plays Elden Ring's own animation). With A each ported shield is turned by DS3's dummy frame times the inverse of Elden Ring's, at the player model's bind pose. Use F if a shield sits worse than before. F also skips the shape check in G's description. (`DARKSIGN_SHIELD_HOLD=0`)
- **G: Shields skip the template shape check.** As A, but ported shields are only turned by the hold frames (DARKSIGN_SHIELD_SHAPE=0), as in 1.0.420. With A each single-model ported shield then has its face laid on the face of the vanilla Elden Ring shield it is built on: the thinnest principal axis of each model's vertices is its face (signed by where the plate lies from the grip), and the shortest turn that puts the ported face on the template's is applied when it is 15° or more (a shield facing the wrong way is turned about its own length, so its top stays up). The spin about the face stays the hold frames': matching the long axes too (#186) turned the small shields on the near-round Buckler template onto their side. Shields with several models (the Giant Door Shield) keep DS3's model positions on their own absorb row, so each model is turned from its DS3 dummy's frame onto the frame of the Elden Ring dummy it really sits on, and no shape check applies; up to #186 the door was turned for the template's hand dummy instead and faced backwards on the other side of the body. The porter log has one "shield shape" line per shield and one "shield hold" line listing the multi-model shields. Use G if a shield that looked right in 1.0.420 sits worse now. (`DARKSIGN_SHIELD_SHAPE=0`)


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
- **H: Without DS3's levels and pitch.** A, but every ported sound plays at the Elden Ring template's level with no pitch change or randomisation (DARKSIGN_SOUND_LEVELS=0), as earlier builds did. Use it if ported sounds are too quiet, too loud or at the wrong pitch. (`DARKSIGN_SOUND_LEVELS=0`)
- **I: Without loops and Stop events.** A, but DS3 sounds that loop play through once, no ported sound has a Stop event, and effects play the sounds they hold once (DARKSIGN_SOUND_LOOPS=0), as earlier builds did. Use it if a ported sound is cut off early, never ends, or the game misbehaves when an effect or move ends. (`DARKSIGN_SOUND_LOOPS=0`)
- **J: Elden Ring's other swing sounds on every DS3 hitbox.** A, but a DS3 swing with no Elden Ring swing of its own repeats the template's remaining swing sounds even when the move plays DS3's sounds (DARKSIGN_SWING_SOUND_REPEAT=1), as up to 1.0.347, where a swing with several hitboxes (the Dragonslayer Greataxe) played them several times. Use it if some moves lose a sound they had. (`DARKSIGN_SWING_SOUND_REPEAT=1`)
- **K: Sound events name their sound twice.** A, but each ported sound event (129) names the ported sound as its override sound too (DARKSIGN_SOUND_OVERRIDE=same), as up to 1.0.410. With A the override is cleared (type 0, ID 0), as in about 99.9% of Elden Ring's own 129 events: naming it could play each DS3 swing sound twice at once, a hair apart, which comb-filters into a flanger whoosh (the Dragonslayer Greataxe's "jet engine"). Use K if ported swings go silent. (`DARKSIGN_SOUND_OVERRIDE=same`)
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
- **E: DS3 arts, old art ID order.** Same as A, but the art IDs go out as in builds up to 1.0.378 (DARKSIGN_ART_POOL=0). With A, there are 115 free art IDs plus the hks's stance-only IDs 342, 343, 346 and 347 (stance arts take those first), weapons whose DS3 moves are the same files with the same events share one ID (Golden Ritual Spear and Heysel Pick, the a48 shield arts), and every art with its own DS3 section gets an ID before any class skill (Demon's Scar, the Frayed Blade and the Farron Greatsword keep their own; fx18: 176 weapons do a DS3 art, class skills such as the Black Blade's Hold take Elden Ring's Ash of War of the same kind). With E, none of that. (`DARKSIGN_ART_POOL=0`)
- **F: DS3 arts, most weapons first.** Same as A, but the arts serving the most weapons go first, arts with their own DS3 section winning ties (DARKSIGN_ART_POOL=weapons): 201 weapons do a DS3 art (the Black Blade keeps its Hold), but seven one-weapon arts (Demon's Scar, the Frayed Blade, Irithyll Straight Sword, Executioner's Greatsword, Dragonslayer Greatshield, Millwood Battle Axe, Sage's Crystal Staff) fall back to an Elden Ring ash. (`DARKSIGN_ART_POOL=weapons`)
- **G: Drop DS3's [HKS] marker effects.** As A, but a DS3 move's "[HKS]" marker SpEffect that Elden Ring's hks doesn't name the same is left out (DARKSIGN_HKS_MARKERS=drop), as in builds up to 1.0.385. With A it is ported under a new ID (never kept: DS3's 100500 is Elden Ring's Raptor Dodge marker), so its fields still work: DS3's 100420 "[HKS] Dragonhead Greatshield" adds 100 to the behaviour judge, which picks Dragon Roar's breath. Use G if a move does something odd since then. (`DARKSIGN_HKS_MARKERS=drop`)
- **H: Own-section arts from the first block.** As A, but a weapon's own DS3 section always gives its art from its first skill move (DARKSIGN_ART_BLOCK_BY_NAME=0), as up to 1.0.410. With A, when the DS3 art's name names a kind (Stomp, Stance, Spin Slash, Charge, Warcry, Perseverance) and the section has that block, the art comes from that block: the Old King's Great Hammer's a174 keeps Molten Perseverance at 37700 (Perseverance buff, then the lava throw 37710 as the R2 follow-up), and its 36000 is a Stomp left over from development, which is what 1.0.410 played. Use H if an art changed for the worse. (`DARKSIGN_ART_BLOCK_BY_NAME=0`)
- **off: Off (Elden Ring Ashes of War).** Ported weapons keep their template's Elden Ring Ash of War (DARKSIGN_WEAPON_ARTS=0), as in builds up to 1.0.169. (`DARKSIGN_WEAPON_ARTS=0`)

### DS3 weapon status and buffs (frostbite, bleed, poison) (`weapon-effects`, rebuilds the content)

Try the next option if: A ported weapon builds up no status (Friede's scythe no frostbite), the wrong status, or holding or swinging it does something odd (health drains, stats change, the game crashes on hit).

- **A: Status, equipped effects and art buffs (default).** DS3's status on hit, the effects a DS3 weapon gives while held and the buffs its weapon art casts, ported as new Elden Ring effects (status rows copied from Elden Ring's own bleed, frost and poison rows with DS3's buildup). Report: `TheDarkSign\Work\reports\weapon-effects.tsv`.
- **B: Status on hit only.** Only the status a weapon builds up on hit (DARKSIGN_WEAPON_EFFECTS=status); equipped effects and art buffs are left out. (`DARKSIGN_WEAPON_EFFECTS=status`)
- **C: Blank effect rows.** As A, but an effect with no Elden Ring row to start from begins blank, every field 0 (DARKSIGN_SPEFFECT_BLANK=1), as in builds up to 1.0.353, where Friede's weapon art made the enemies it hit take and deal no damage. Only for comparing with A. (`DARKSIGN_SPEFFECT_BLANK=1`)
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
- **I: No animated effect models.** As A, but DS3 effects whose 3D model is animated (Demon's Scar's art) are left out (DARKSIGN_FX_ANIMATED_MODELS=0), as in builds up to 1.0.378. With A, the model's anibnd (same number as the model in both games: sXXXXX_skeleton plus sXXXXX_a000_0000NN animations) is converted onto an Elden Ring effect anibnd from the common effect binders: its skeleton takes DS3's bones and each animation goes through the moveset's animation converter. Try I if the game crashes or shows a broken shape when such an effect plays. (`DARKSIGN_FX_ANIMATED_MODELS=0`)
- **J: DS3 follow-up projectile conditions.** As A, but a projectile that makes another where it lands keeps DS3's condition for it (255 hit the ground or an enemy, 254 life ran out), which Elden Ring's own projectiles never use (DARKSIGN_BULLET_LAUNCH=ds3), as in builds up to 1.0.420. With A they become Elden Ring's 0 (whenever the projectile ends), so trails of projectiles that each land where the last fell (the Immolation Tinder's fire trail) can go on. Try J if a ported trail or burst now spawns too much. (`DARKSIGN_BULLET_LAUNCH=ds3`)
- **off: Off.** Neither (DARKSIGN_BULLETS=0 and DARKSIGN_EFFECT_VISUALS=0): only the effects in a move's own effect events show, as in builds up to 1.0.242. (`DARKSIGN_BULLETS=0`, `DARKSIGN_EFFECT_VISUALS=0`)


## DS3 maps

### Which Elden Ring map IDs hold the DS3 maps (`map-slots`, rebuilds the content)

Try the next option if: Warping into a DS3 map fails (nothing happens, endless loading, crash, or you land in the colosseum).

- **A: Colosseum slots (default).** Firelink/Cemetery of Ash, High Wall and Undead Settlement replace the three colosseum arenas (m45_00-02), which single-player never visits.
- **B: Spare map IDs.** Each DS3 map gets a map ID of its own that Elden Ring lists in its map registry and single-player never visits (m11_70, m11_72, m19_70; each ships a small map of its own), built from the colosseums' file formats. The Painted World then takes the first colosseum. Untested in game. A map ID the game doesn't list at all is not an option: registering one crashed the game at boot (1.0.238); see `docs/maps/own-map-ids.md`. (`DARKSIGN_MAP_SLOTS=new`)

### Where the Painted World of Ariandel goes (`map-painted-world`, rebuilds the content)

Try the next option if: Warping to the Painted World (or the F1 menu's Friede fight) fails: nothing happens, endless loading, or a crash.

- **A: Own map ID (default).** The Painted World gets m19_70, a map Elden Ring ships and single-player never visits, built from the first colosseum's formats and side files. The three colosseum maps stay as they are.
- **B: In a colosseum.** The Painted World replaces the Undead Settlement in the third colosseum (m45_02), the slot kind every DS3 map is proven to load from. The Undead Settlement is left out. (`DARKSIGN_PAINTED_WORLD=colosseum`)
- **C: Left out.** No Painted World: the three colosseum maps only, as builds before 1.0.37x had it. (`DARKSIGN_PAINTED_WORLD=0`)

### Getting into the DS3 run (`run-start`, rebuilds the content)

Try the next option if: Neither the F1 menu's Start button nor Ctrl+Shift+F1 takes you to the Cemetery of Ash.

- **A: On request (default).** The F1 menu or Ctrl+Shift+F1 sets a request flag, and an event in common.emevd warps you.
- **B: Automatically on load.** Also, every time a character loads outside a DS3 run, the event warps you there after 3 seconds, without any menu. Leave with the F1 menu or Ctrl+Shift+F2 (you'll be sent back on the next load). (`DARKSIGN_AUTO_DS3_RUN=1`)

### Map collision format (`map-collision`, rebuilds the content)

Try the next option if: You fall through the floor of a DS3 map, or the game hangs or crashes while a DS3 map loads.

- **A: Binary tagfiles (default).** DS3 collision and navmeshes converted to Havok 2018 and stored as binary tagfiles, the form Elden Ring ships its own in: the same size as DS3's files.
- **B: XML tagfiles, as up to 1.0.30x.** The same collision stored as XML tagfiles, which JortPob ships and Elden Ring reads, but some 55 times larger: 3.4 to 7.5 GB per DS3 map, which the game reads as you walk (port-maps --xml-collision). Try it if you fall through the floor on A. (`port-maps --xml-collision`)
- **C: Binary, keeping DS3's SIMD tree.** Binary tagfiles that keep DS3's SIMD collision tree instead of building a new one (port-maps --simd). (`port-maps --simd`)

### Which map parts are drawn (culling) (`map-groups`, rebuilds the content)

Try the next option if: Parts of a DS3 map vanish or pop in as you move, a room is empty until you step inside, or the map lags far more than an Elden Ring map.

- **A: Everything visible (default).** Every map part draws from everywhere in the map, nothing culled: map pieces and objects carry no draw or display groups, collisions and enemies have every group on, as in every build up to 1.0.410 and again after 1.0.417. DS3's own lower detail models keep far scenery cheap instead ("Lower detail models far away").
- **B: DS3's draw groups, as 1.0.414 to 1.0.417.** Each DS3 map area shows only the map pieces, collisions and objects DS3 showed from it (port-maps --ds3-culling), written into Elden Ring's own draw and display groups (Smithbox's reading of the part struct); collisions themselves are never culled. In 1.0.417 this hid Firelink Shrine's chairs until you stepped outside, faded the world outside the shrine, made the player vanish near the Crystal Lizard and made Friede's arena flicker, so it is only for comparison. (`port-maps --ds3-culling`)
- **C: No draw groups.** Parts carry no draw or display groups at all (port-maps --no-groups), as the colosseums' own parts and JortPob's maps have them; nothing is culled. (`port-maps --no-groups`)

How it works: a DS3 collision lists the groups shown while the player stands on it, and a part is drawn only while one of its own groups is shown. Elden Ring does the same, but the porter's SoulsFormats reads Elden Ring's group struct as three unnamed arrays; Smithbox's MSBE (vawser/Smithbox, `DisplayDataStruct`) reads the same bytes as display groups, alternate display groups and mask, draw groups, alternate draw groups and mask. Up to 1.0.410, the old B (`--ds3-groups`) wrote DS3's masks into the first two arrays, so the real draw groups stayed empty and nothing was ever culled. 1.0.414 to 1.0.417 culled by default (now B) and it went wrong in game, so A is the default again. `--ds3-groups` and `--all-visible` are still accepted and mean A. See `Porter/src/DarkSign.Porter/Maps/MapDrawGroups.cs`.

### Draw distance settings of the DS3 map pieces (`map-draw`, rebuilds the content)

Try the next option if: DS3 map geometry is invisible or pops in only when close.

- **A: The port's own rows (default).** Every map piece names a PartsDrawParam row the port adds (unlimited draw distance, as JortPob's maps have); pieces whose models carry DS3's lower detail models name a second row that switches to them by distance ("Lower detail models far away").
- **B: A vanilla row.** Map pieces name Stormveil Castle's most common PartsDrawParam row, 2046 (port-maps --vanilla-draw-param), and nothing is added to the regulation. (`port-maps --vanilla-draw-param`)

### Lower detail models far away (`map-lod`, rebuilds the content)

Try the next option if: A DS3 map lags or stutters when you look across it, or distant buildings and rocks look blocky, flicker or vanish when you move away from them.

- **A: DS3's lower detail models by distance (default).** DS3 ships a simpler copy of most map models inside the model itself (5,078 lower detail lists in Undead Settlement's). The game switches a piece to the first beyond 30 m and the second beyond 60 m, as vanilla Elden Ring does for its own map pieces that have them; pieces whose models have none are always drawn in full. The port log's "lower detail" lines say how many triangles each level keeps.
- **B: Full detail everywhere, as up to 1.0.417.** Every piece is drawn at full detail at any distance (port-maps --no-lod): every triangle of the whole map in view at once. (`port-maps --no-lod`)
- **C: Twice as far.** The switch happens beyond 60 m and 120 m (port-maps --far-lod): sharper distant scenery for more to draw. (`port-maps --far-lod`)

How it works: every mesh of a DS3 (and Elden Ring) map model can hold three triangle lists, full detail and two lower detail levels, and the game draws the one its PartsDrawParam row picks by distance. Up to 1.0.417 the port's row never switched, so map 3 drew every triangle of the town at full detail whenever the player faced it (a steady 3 fps on the frame-time recorder). A model only switches when every one of its meshes has every level; a mesh that lacks one gets a copy of its next more detailed list, since a mesh with no list for a level is believed to draw nothing there. See `Porter/src/DarkSign.Porter/Maps/MapLod.cs`.

### Materials of the DS3 map pieces (`map-materials`, rebuilds the content)

Try the next option if: DS3 map geometry is invisible, black, far too dark or shiny, or foliage and fences are solid cards.

- **A: Elden Ring's map shaders by kind (default).** Each DS3 material is drawn with a copy of an Elden Ring map material of its kind: normal-mapped opaque, alpha-tested (foliage, grates), blended, and unlit (sky domes), each mesh rebuilt in that material's vertex layout.
- **B: Albedo only.** Every DS3 material uses the albedo-only material JortPob builds its maps on (port-maps --plain-materials): no normal maps, cut-outs or unlit sky, but the simplest shader there is. (`port-maps --plain-materials`)

### Texture detail of the DS3 maps (`map-texture-detail`, rebuilds the content)

Try the next option if: A DS3 map hitches or stutters as you move through it, or its textures look blurrier up close than they did in 1.0.299.

- **A: Full textures near you, half size beyond (default).** DS3's textures in full for map pieces within 128 m (further for large pieces: a floor or building 100 m across loads them from 128 m beyond its middle, the largest from 512 m beyond), and a half size low detail copy (a quarter of the memory) for everything further away, the way JortPob and Elden Ring's own maps keep a low detail copy.
- **B: Half size textures.** Every DS3 map texture at half its width and height, and quarter size beyond 128 m (port-maps --half-textures): for a graphics card whose memory DS3's textures still don't fit in. (`port-maps --half-textures`)
- **C: Full size twice, as up to 1.0.299.** The low detail copy is the full texture again and full textures load within 256 m (port-maps --full-low-textures): every texture of a map sits in graphics memory twice at full size. (`port-maps --full-low-textures`)

### Lighting of the DS3 maps (`map-lighting`, rebuilds the content)

Try the next option if: A DS3 map looks wrong since it got its own lighting: washed out, lit from inside walls, lamps in the wrong places, or the game hangs or crashes while it loads.

- **A: Sunlight, ambient light and DS3's map lights (default).** DS3's sky dome casts no shadow, so the host map's sun reaches the map. The host's environment map (its ambient light) applies in a box around the whole DS3 map. The lights of DS3's light file (soft fill lights, mostly indoors) replace the host's. Candles, torches and bonfires are DS3 objects with their own flame effects and are not part of this.
- **B: The host map's lighting, untouched.** Nothing is changed (port-maps --host-lighting): the sky dome shadows the map, there is no ambient light and the lamps are the colosseum's. Dark, as every build up to 1.0.284 was. (`port-maps --host-lighting`)

### Sky over the DS3 maps (`map-sky`, rebuilds the content)

Try the next option if: A DS3 map has no sky at all (black or a flat colour overhead), or the game hangs or crashes while it loads since Elden Ring's sky was removed.

- **A: DS3's own sky (default).** Elden Ring's distant view of the host map (its sky, the Erdtree and far scenery) is replaced with an empty one, so the only sky is the dome DS3 built into the map.
- **B: The host map's sky.** The host's distant view is left alone (port-maps --host-sky): Elden Ring's sky and the Erdtree show over the DS3 map, as in every build up to 1.0.299. (`port-maps --host-sky`)

### DS3's helper map pieces (`map-helper-pieces`, rebuilds the content)

Try the next option if: Part of a DS3 map is missing since 1.0.300 (a wall, a floor, a whole building), or its shadows look wrong.

- **A: Left out (default).** DS3 keeps pieces in its maps that are never drawn as scenery: low detail doubles of whole areas that only water reflections show, flat cards and boxes that only cast shadows, and pieces of another state of the map. They are left out. Work\reports\map-pieces-<map>.tsv lists every piece and why it was left out.
- **B: Kept as ordinary pieces.** Every DS3 map piece is drawn (port-maps --helper-pieces), as in every build up to 1.0.299: blank slabs and blurry doubles of the ground and rocks stand in the maps, but nothing is missing. (`port-maps --helper-pieces`)

### Floor copies in DS3 collision (`map-hit-filters`, rebuilds the content)

Try the next option if: Enemies stand in mid-air or on surfaces they shouldn't since 1.0.354, or anything about the ground changed since then.

- **A: Written as normal floor (default).** DS3 lays copies of its floors in collision with hit filter 24, which Elden Ring reads differently: enemies that landed on one fell through the map (1.0.347, map 3). They are written as normal floor (hit filter 8). The port log's layout notes count them.
- **B: DS3's hit filters.** Every collision keeps DS3's hit filter number (port-maps --ds3-hit-filters), as in every build up to 1.0.353: enemies fall through the floor where a filter-24 copy is the top surface. (`port-maps --ds3-hit-filters`)

### DS3's low-detail stand-ins and far views (`map-stand-ins`, rebuilds the content)

Try the next option if: A far part of a DS3 map is missing (a castle wall, a hall, a tower or mountains you could see from a distance), or blocky slabs, steps or low-resolution polygons floating over the map are back.

- **A: Left out where detailed pieces stand (default).** DS3 shows a far area as one blocky piece with a stretched "low" texture, and only from where its detailed pieces are not drawn. With DS3's culling on ("Which map parts are drawn" B) the game does the same and every stand-in is kept. Without it (the default) the port draws everything at once, so such a stand-in is left out when detailed pieces DS3 never shows together with it stand inside it. One with nothing detailed under it (Lothric Castle seen from the High Wall) stays. Far views go the same way: low-detail copies of a whole area with ordinary textures (map 3's mountains, roofs and rock walls seen from one lookout), left out when they cover at least ten times their own detail that DS3 never draws with them. The port log lists every stand-in and far view and why it was kept or left out.
- **B: Kept.** Every stand-in and far view is drawn (port-maps --stand-ins), as in every build up to 1.0.366: blocky slabs, steps and low-resolution polygons overlap the detailed scenery, but nothing far away is missing. (`port-maps --stand-ins`)

### DS3's ground overlays and two-layer ground (`map-overlays`, rebuilds the content)

Try the next option if: Paving, rubble or a layer of ash looks wrong on a DS3 map since this option, ground looks patchy where DS3's was smooth, or walls, floors, houses or trees are missing.

- **A: Solid, two-layer ground by layer (default).** Every one-layer DS3 map mesh is drawn whole and solid, whatever its vertex transparency says: DS3 draws Firelink Shrine's tunnel walls, Undead Settlement's houses and the Painted World's stone walls with that value at zero, so it is not a fade. Ash painted over brick (two-layer materials) still shows, per triangle, the layer DS3 shows there. The port log's "vertex alpha:" line counts both.
- **B: Solid.** Every vertex is drawn solid and two-layer ground shows its first layer (port-maps --solid-overlays), as in every build up to 1.0.340: dark brick shards lie across the ash around Firelink Shrine, but nothing is left out. (`port-maps --solid-overlays`)
- **C: Faded where ground lies under them.** One-layer meshes keep their vertex transparency as a fade, and triangles DS3 shows at less than half strength are left out where other ground lies right under them (port-maps --fade-overlays), as PR #165 made it: tunnel walls, houses and stone walls lose the faces that have another surface behind them. (`port-maps --fade-overlays`)
- **D: Cut wherever faded.** Every overlay triangle DS3 shows at less than half strength is left out, whether or not anything lies under it (port-maps --cut-faded-overlays), as in builds 1.0.353 to 1.0.383: holes open to the sky in Firelink Shrine's floor and tunnels, the tree above Iudex Gundyr, Undead Settlement's houses and the Painted World's walls. (`port-maps --cut-faded-overlays`)

### Ground and walls that mix several DS3 textures (`map-blend-layers`, rebuilds the content)

Try the next option if: A DS3 map's ground or walls show the wrong texture in patches, or change texture at hard, jagged edges since this option.

- **A: Each triangle shows DS3's layer (default).** DS3 paints two to four ground textures across one terrain mesh (brick, earth, ash and the like), choosing each by a weight stored on every vertex. Elden Ring has no shader in use that blends DS3's layers, so the port splits each such mesh and draws every triangle with the layer DS3 shows there, with that layer's own texture, normal map and tiling. The port log's "terrain blends:" line counts the triangles each layer got.
- **B: First layer only.** Every terrain mesh shows only its first layer (port-maps --first-blend-layer), as in every build up to 1.0.353: dark brick shards lie across the ash around Firelink Shrine and the walls show one texture, but no part of the ground changes texture at a hard edge. (`port-maps --first-blend-layer`)

### Colours of light and fog in the DS3 maps (`map-atmosphere`, rebuilds the content)

Try the next option if: A DS3 map looks washed out, too foggy, too clear, or wrongly coloured since the maps took DS3's colours.

- **A: DS3's colours (default).** Each map's sun and fog take DS3's colours and sun angle, the fog is as thick as Stormveil Castle's, and the colosseum's colour grading is off. How bright each light is stays Elden Ring's.
- **B: The colosseum's own.** The map keeps the Elden Ring colosseum's colour, fog and sun settings (port-maps --host-atmosphere), as in every build before this option: a red tint on map 2 and thick golden fog on map 1. (`port-maps --host-atmosphere`)

### Light the DS3 map pieces get from around them (`map-env-light`, rebuilds the content)

Try the next option if: A DS3 map's walls and ground look flat, too dark or too grey, or shine oddly since the colosseum's colour was taken out of their light.

- **A: Even grey (default).** Elden Ring lights map pieces with light baked for the map they stand in (a light volume and a reflection cube per place). The DS3 maps had the colosseum's: red on map 2, golden on map 1, pale blue on map 3. That light is made one even grey of the same strength, so the map's own textures, sun and fog give the colour.
- **B: The colosseum's own.** The map pieces keep the colosseum's baked light (port-maps --host-env-light), as in every build before this option: red map surfaces on map 2. (`port-maps --host-env-light`)

### DS3's baked light and shade in the DS3 maps (`map-baked-light`, rebuilds the content)

Try the next option if: A DS3 map's rooms, tunnels or courtyards are lit in the wrong places (dark where DS3 is bright, bright where it is dark), look mirrored, or the map looks wrong since its light was baked from DS3.

- **A: DS3's baked light (default).** Each map gets a light volume of its own built from DS3's light probes: tunnels, rooms and shade under roofs are as dark as DS3 baked them and open ground as bright as the sky. Outside the probed area the map keeps one even light.
- **B: One even light everywhere.** No baked light volume (port-maps --flat-env-light), as in builds before this option: the same flat light in tunnels as outside, no shade. (`port-maps --flat-env-light`)
- **C: Baked light mirrored east-west.** The same baked light with the volume mirrored along x (port-maps --baked-light-flip x). Try it if dark patches sit across the map from where the rooms are. (`port-maps --baked-light-flip x`)
- **D: Baked light mirrored north-south.** The same baked light with the volume mirrored along z (port-maps --baked-light-flip z). Try it if C didn't line the dark patches up with the rooms. (`port-maps --baked-light-flip z`)

### Where DS3's lamps and baked light sit in the DS3 maps (`map-light-place`, rebuilds the content)

Try the next option if: Since this option came in, a DS3 map's lamps glow where nothing is, its rooms are lit from the wrong side, or its shade falls in the wrong places when it didn't before.

- **A: Moved onto the map (default).** DS3 keeps a map's lamps and light probes in the map's own space and moves them into place with the map offset the map file holds (map 1: 28 degrees and 200 m, map 3: 156 degrees and 600 m; map 2 has none). The port does the same, so Firelink's lamps light the shrine and the baked shade falls where DS3's does. Only the map's own light file is ported; the files for its other states (the Untended Graves) no longer stack on top.
- **B: Left in the map's own space.** Lamps and light probes stay where earlier builds put them (port-maps --no-light-offset): on maps 1 and 3 that is far from where they belong. (`port-maps --no-light-offset`)

### How enemies find their way in the DS3 maps (`map-navmesh`, rebuilds the content)

Try the next option if: A DS3 map crashes or hangs while loading since launcher 1.0.300, enemies stand still or refuse to cross ground they should walk on, or they walk through closed doors.

- **A: DS3's navmeshes, every face plain ground (default).** The map's navmesh list is built from DS3's, so the game loads DS3's navmeshes: enemies walk around walls and gaps instead of running straight at you. DS3's per-face marks (closed doors, ladders, drops) are cleared, because Elden Ring reads those bits its own way.
- **B: DS3's navmeshes with DS3's face marks.** The same, but each face keeps DS3's mark (port-maps --navmesh-flags ds3). Try it if enemies walk where DS3 kept them out; if they then refuse ground they should cross, Elden Ring reads the marks differently. (`port-maps --navmesh-flags ds3`)
- **off: The host map's navmesh list, untouched.** The list stays the colosseum's, which names none of DS3's navmeshes (port-maps --host-navmesh-list): the game loads no navmesh and enemies run straight at you, as in every build up to 1.0.299. (`port-maps --host-navmesh-list`)

### Enemies and bosses in the DS3 maps (`map-enemies`, rebuilds the content)

Try the next option if: A DS3 map crashes or hangs while loading, enemies stand in walls or fall through the floor, or a boss fight never starts.

- **A: DS3 characters where ported, stand-ins elsewhere (default).** Every DS3 enemy and boss stands where it did in DS3, with its DS3 entity ID so the converted boss and trap events find it. Characters the enemy porter has ported (darksign_chr_map.tsv) are the real DS3 ones; the rest are Elden Ring enemies of the same kind (knight, soldier, dog, dragon...), bosses get Elden Ring bosses.
- **B: Elden Ring stand-ins only.** The same placements, but always the Elden Ring stand-ins, even for ported DS3 characters (port-maps --enemies standin). Tells whether a ported character is what breaks. (`port-maps --enemies standin`)
- **off: Off (no enemies).** The DS3 maps are empty of enemies (port-maps --enemies none), to explore them or to find out whether enemies are what breaks a map. (`port-maps --enemies none`)

### DS3 objects in the DS3 maps (`map-objects`, rebuilds the content)

Try the next option if: A DS3 map crashes, hangs while loading or hitches since objects were added, objects float or are a mess of polygons, or something invisible blocks the way.

- **A: DS3's objects as Elden Ring assets (default).** Every DS3 object (statues, candles, crates, doors, ladders, the bonfire's sword) is converted to an Elden Ring asset and stands where DS3 has it, with its DS3 entity ID so the converted events find it. Objects that don't break also get DS3's collision; doors, lifts and levers move (see "DS3 doors, lifts and levers"); crates and barrels can be walked through until breaking is ported. Report: Work\reports\object-port.tsv.
- **B: Objects without collision.** The same objects, but none of them is solid (port-maps --objects nocollision). Tells whether an object's collision is what blocks the way or breaks the map. (`port-maps --objects nocollision`)
- **off: Off (no objects).** The DS3 maps have no objects (port-maps --objects none), as every build up to 1.0.299 was. (`port-maps --objects none`)

### DS3 corpses in their poses (`map-object-poses`, rebuilds the content)

Try the next option if: A DS3 map crashes or hangs while loading since corpses were posed, or a corpse or skeleton lying in a DS3 map is a tangle of stretched polygons.

- **A: Corpses in DS3's poses (default).** DS3's corpses and skeletons (and any other object a DS3 map file poses with an animation) are drawn in the pose DS3 gives each one: slumped, lying, hanging. The pose is baked into a copy of the model per pose. Report: the "pose" rows of Work\reports\object-port.tsv.
- **B: Unposed.** Corpses stand in the model's bind pose, arms out (DARKSIGN_OBJECT_POSES=0), as every build up to 1.0.42x drew them. (`DARKSIGN_OBJECT_POSES=0`)

### DS3 doors, lifts and levers (`map-doors`, rebuilds the content)

Try the next option if: A DS3 map crashes or hangs while loading since doors and lifts were added, a door or gate blocks the way and won't open, a lift drops you through it, or doors and gates look broken (parts in the wrong place, stretched).

- **A: Doors, lifts and levers move (default).** DS3's objects that move (doors, gates, lifts, levers, chests) get their DS3 animations, collision that moves with them, and DS3's ObjAct events and ObjActParam rows, so a door opens when you use it and a lift carries you when DS3's scripts start it. Locked doors open without their key (keys aren't ported). They move silently. Report: the "moves in Elden Ring" column of Work\reports\object-port.tsv.
- **B: They move, without collision.** The same, but doors, lifts and levers aren't solid (port-maps --moving-objects nocollision). Tells whether their collision is what blocks the way or breaks the map; lifts can't carry you. (`port-maps --moving-objects nocollision`)
- **off: Off (they stand still).** Doors, lifts and levers are drawn standing still, without collision or ObjAct events (port-maps --moving-objects off), as every build before doors were ported was. (`port-maps --moving-objects off`)

### DS3 regions, patrol routes and generators (`map-regions`, rebuilds the content)

Try the next option if: A DS3 map crashes or hangs while loading since regions were added, enemies walk into walls or stand in a pile, or an event fires in the wrong place.

- **A: DS3's regions and map-file events (default).** The areas DS3's events watch (boss rooms, ambush triggers, warp points), enemy patrol routes, platoons and enemy generators are carried into the map with their DS3 entity IDs.
- **B: None.** The map file has no DS3 regions, patrol routes, platoons or generators (port-maps --no-regions), as every build up to 1.0.299 was: events that wait for the player to enter an area never fire and enemies don't patrol. (`port-maps --no-regions`)

### DS3's map effects (candles, fog, light shafts) (`map-effects`, rebuilds the content)

Try the next option if: A DS3 map crashes, hangs while loading or hitches since its effects were added, or a fog bank, light shaft or flame looks wrong or floats in the air.

- **A: DS3's map effects (default).** The effects DS3 places in its maps (Firelink Shrine's candle flames, fog banks, light shafts, crows, flies and fires) are converted with the weapons' effects and stand where DS3 has them. Report: Work\map-sfx.tsv lists any effect that couldn't be converted.
- **B: None.** The DS3 maps have none of DS3's map effects (DARKSIGN_MAP_SFX=0), as every build up to 1.0.366 was. (`DARKSIGN_MAP_SFX=0`)

### DS3's cutscenes (boss intros) (`map-cutscenes`, rebuilds the content)

Try the next option if: The game crashes, hangs or shows a black screen when a DS3 cutscene should play (walking up to Ariandel for the Friede fight, a boss fog, or Play in the F1 menu's Cutscenes tab), a cutscene looks broken and you'd rather go straight to the fight, or a cutscene's sound is wrong or breaks the game's sound.

- **A: DS3's cutscenes with their sound (default).** Every cutscene of the ported DS3 maps is converted from your DS3 files into Elden Ring's format and plays where DS3's scripts play it (and from the F1 menu's Cutscenes tab), with the ported bosses in it. Its sound comes along: DS3's cutscene audio, effects and spoken lines in a sound bank of its own (sd\sAA_BB_NNNN.bnk), armour movement as Elden Ring's. Subtitles aren't shown. Report: the cutscenes section of the port-maps log.
- **B: None.** No DS3 cutscenes (DARKSIGN_CUTSCENES=0): the scripts skip them and a cutscene that moves you only moves you, as builds up to 1.0.38x did. (`DARKSIGN_CUTSCENES=0`)
- **C: Silent cutscenes.** The cutscenes play as in A but without DS3's sound (DARKSIGN_CUTSCENE_SOUND=0): no cutscene sound banks, and the timelines keep their sound events as the first cutscene builds had them. (`DARKSIGN_CUTSCENE_SOUND=0`)
- **D: DS3's timelines as converted.** The cutscenes play as in A, but their timelines keep DS3's field values (DARKSIGN_CUTSCENE_CONFORM=0) instead of taking Elden Ring's shape: events may run past the end of a cut, cut flags and parameter lists stay DS3's, as builds up to 1.0.42x had them. (`DARKSIGN_CUTSCENE_CONFORM=0`)

### Which state of a DS3 map is ported (`map-layers`, rebuilds the content)

Try the next option if: Enemies or objects you expected are missing from a DS3 map since 1.0.327 (for example on the way to Firelink Shrine), or you want to see the Untended Graves' enemies.

- **A: The state the game starts in (default).** Enemies and objects that DS3 keeps for another state of the same map are left out, as its map pieces already were: m40 holds the Untended Graves (Black Knights, Champion Gundyr, the dark shrine) at the same place as the Cemetery of Ash and Firelink Shrine.
- **B: Every state.** Enemies and objects of every state stand in the map together (port-maps --all-layers), as every build up to 1.0.326 had them: the Untended Graves' Black Knights stand around Firelink Shrine. (`port-maps --all-layers`)

### DS3's people (NPCs and hostile humans) (`map-people`, rebuilds the content)

Try the next option if: A DS3 map crashes or hangs while loading since people were added, a person who should be friendly attacks you (or the other way round), or people stand in the way of something.

- **A: DS3's own people, humans in DS3 armour (default).** DS3 characters with a body of their own (Fire Keeper, Ludleth) stand where DS3's scripts seat them, silent, when the enemy porter ported them. DS3's human NPCs and hostile humans (Hawkwood, Sirris, the Sword Master) wear their DS3 armour, converted (each piece hiding the body like the nearest Elden Ring piece), and hold their DS3 weapons (the ported weapon, or Elden Ring's of the same name), on an Elden Ring person's face, body and AI.
- **B: Elden Ring people stand in.** As builds 1.0.326-1.0.353: where DS3 has a human or a talker with no ported body, a dressed Elden Ring person stands, silent; DS3's hostile humans get hostile Elden Ring humans (port-maps --er-people). (`port-maps --er-people`)
- **C: Nobody.** DS3's people are left out of the maps, ported ones too (port-maps --no-people), as every build up to 1.0.299 was. (`port-maps --no-people`)
- **D: DS3's own bodies only, no DS3 armour.** As builds 1.0.354-1.0.366: only DS3 characters with a body of their own stand; DS3's humans are left out (DARKSIGN_PEOPLE_ARMOUR=0). Try it if a person in DS3 armour crashes the game, is invisible or is a mess of stretched polygons. (`DARKSIGN_PEOPLE_ARMOUR=0`)
- **E: DS3 armour, the stand-in's weapons.** As A, but DS3's humans hold the weapons of the Elden Ring person who stands in for them (DARKSIGN_PEOPLE_WEAPONS=0), as up to 1.0.403. Try it if a person holding a ported DS3 weapon crashes the game, T-poses or stands still. (`DARKSIGN_PEOPLE_WEAPONS=0`)
- **F: DS3 armour hides the body like a closed helm.** As A, but every DS3 armour piece hides the body like the most common Elden Ring piece of its slot (DARKSIGN_PEOPLE_HIDES=common), as up to 1.0.42x, where open helms such as Hawkwood's Chain Helm hid his head. Try it if hair, a face or skin pokes through a person's DS3 armour with A. (`DARKSIGN_PEOPLE_HIDES=common`)

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
- **G: DS3's vertex colours.** As A, but the model keeps DS3's vertex colours (white where DS3 has none) and a two-layer material shows its first layer (DARKSIGN_ENEMY_COLOURS=ds3), as in builds up to 1.0.299, where the Soul of Cinder and Pontiff Sulyvahn drew without textures. Use when ported enemies that had textures lose them, or turn red, with A. (`DARKSIGN_ENEMY_COLOURS=ds3`)
- **H: DS3's material numbers.** As A, but every ported model (enemies, weapons, armour, objects, effect models and map pieces) keeps DS3's material numbers, 0 in every material, instead of Elden Ring's numbering (DARKSIGN_MATERIAL_NUMBERS=ds3), as in builds up to 1.0.383, where ported enemies showed their textures and cut-outs only while frostbitten. Use when ported models turn invisible, wear another part's textures or crash the game with A. (`DARKSIGN_MATERIAL_NUMBERS=ds3`)
- **I: The template's body size.** As A, but a ported enemy keeps the Elden Ring template's body capsule (DARKSIGN_ENEMY_BODY_SIZE=template) instead of DS3's size, as up to 1.0.410. Use when ported enemies get stuck in doorways, sink into the floor or float above it with A. (`DARKSIGN_ENEMY_BODY_SIZE=template`)
- **J: The template's weaknesses.** As A, but a ported enemy takes damage of each kind as the Elden Ring template does (DARKSIGN_ENEMY_DAMAGE_RATES=template), as up to 1.0.422, when the Pus of Man had the Giant Beast Skeleton's weakness to strike and resistance to lightning and no weakness to fire. Use when ported enemies take far too much or too little damage with A. (`DARKSIGN_ENEMY_DAMAGE_RATES=template`)
- **K: DS3 fur as one solid layer.** As A, but DS3 fur (Ariandel's wolf) shows the larger of its two layers, uncut (DARKSIGN_ENEMY_FUR=0), as up to 1.0.422, when the wolf's fur drew as white shards. Use when a furry ported enemy turns invisible or bald with A. (`DARKSIGN_ENEMY_FUR=0`)

### What ported DS3 enemies drop (`enemy-drops`, rebuilds the content)

Try the next option if: A ported DS3 enemy drops nothing where DS3 drops something, drops the wrong item or far too often, the game crashes or hangs when a ported enemy dies, or the build's enemy params step says the drops were not ported.

- **A: DS3's drops and chances (default).** Every ported DS3 enemy drops what it drops in DS3, with DS3's chances: ported weapons, and Elden Ring items of the same name. A DS3 item that isn't ported yet (armour, rings, most goods) keeps its chance and gives nothing; Work\reports\enemy-drops.tsv lists them all.
- **B: The Elden Ring template's drops.** Ported enemies keep the drops of the Elden Ring enemy they are built on (DARKSIGN_ENEMY_DROPS=template), as in every build before DS3 drops were ported. (`DARKSIGN_ENEMY_DROPS=template`)

### Ported DS3 enemies' effects, sounds and abilities (`enemy-effects`, rebuilds the content)

Try the next option if: A ported DS3 enemy crashes the game when it spawns or attacks, the game hangs loading it, ported enemies are silent or make wrong sounds, or their effects are missing or wrong.

- **A: DS3's effects, sounds and abilities (default).** Every ported DS3 enemy gets DS3's visual effects in an effect file of its own, DS3's sounds in a sound bank of its own, and the SpEffects and projectiles its DS3 row and moves use (the Jailer's lantern glow and health drain). Work\reports\enemy-effects.tsv says what each enemy got.
- **B: DS3's effects and abilities, the template's sounds.** As A, but ported enemies keep the sounds of the Elden Ring enemy they are built on (DARKSIGN_ENEMY_SOUNDS=0). Use when ported enemies are silent, make wrong sounds, or the game crashes or hangs loading one with A. (`DARKSIGN_ENEMY_SOUNDS=0`)
- **C: The Elden Ring template's effects and sounds.** Ported enemies keep the effects, sounds and abilities of the Elden Ring enemy they are built on (DARKSIGN_ENEMY_EFFECTS=0), as in every build up to 1.0.299. (`DARKSIGN_ENEMY_EFFECTS=0`)
- **D: DS3's effects, plus the template's own.** As A, but a ported enemy also keeps the effects only its Elden Ring template has (DARKSIGN_ENEMY_TEMPLATE_FX=keep), as in builds before this option, when enemies built on the Giant Beast Skeleton came back to life unless hit again. Work\reports\enemy-template-effects.tsv lists what A takes off. (`DARKSIGN_ENEMY_TEMPLATE_FX=keep`)

### Ported DS3 enemies' AI and attacks (`enemy-ai`, rebuilds the content)

Try the next option if: Ported DS3 enemies stand still, never attack, repeat one move, use Elden Ring attacks, never do what DS3's map makes them do (the High Wall's Hollow Soldiers bursting into the Pus of Man), or the game crashes or hangs when a ported enemy notices you.

- **A: DS3's AI and attacks (default).** Every ported DS3 enemy thinks with DS3's own AI script (its battle and logic goals, ported into Elden Ring's AI files, with the think rows they check for renumbered) and DS3's senses; DS3 attacks, waiting poses and map-script moves its Elden Ring template has no slot for are added, and the signals its moves send DS3's map scripts come along. Work\reports\enemy-ai.tsv says what each enemy got.
- **B: DS3's AI, the template's attack slots.** As A, but nothing is added: a DS3 attack, waiting pose or map-script move the template has no slot for is not played (DARKSIGN_ENEMY_EXTRA_MOVES=0). Use when a ported enemy freezes or the game crashes when it attacks with A. (`DARKSIGN_ENEMY_EXTRA_MOVES=0`)
- **C: The Elden Ring template's AI.** Ported enemies think with the AI of the Elden Ring enemy they are built on, which picks DS3 moves by the template's attack numbers (DARKSIGN_ENEMY_AI=template), as in builds before this option. (`DARKSIGN_ENEMY_AI=template`)
- **D: No map-script signals in DS3 moves.** As A, but DS3 move events Elden Ring lays out the same that the template has none of are dropped again (DARKSIGN_ENEMY_SIGNALS=0), as up to 1.0.383, when the High Wall's Hollow Soldiers never burst into the Pus of Man. Use when a ported enemy's move freezes it or crashes the game with A. (`DARKSIGN_ENEMY_SIGNALS=0`)
- **E: Added attacks only.** As A, but only attacks are added: DS3's waiting poses, getting-up moves and map-script moves the template has no slot for are not (DARKSIGN_ENEMY_EXTRA_MOVES=attacks), as up to 1.0.383. Use when a ported enemy freezes, stands in a T-pose or the game crashes as a map starts with A. (`DARKSIGN_ENEMY_EXTRA_MOVES=attacks`)
- **F: DS3's think row numbers in the AI.** As A, but DS3's AI scripts keep comparing the think row they run under with DS3's row numbers (DARKSIGN_ENEMY_THINK_IDS=ds3), as up to 1.0.383; those checks never match a ported row. Use when a ported enemy behaves worse than with 1.0.383. (`DARKSIGN_ENEMY_THINK_IDS=ds3`)
- **G: DS3's effect numbers in the AI.** As A, but DS3's AI scripts keep checking for their own effects by DS3's numbers (DARKSIGN_ENEMY_AI_SPEFFECTS=ds3), as up to 1.0.410, when those checks never matched a ported enemy's effects and Ariandel's wolf only circled. Use when a ported enemy freezes, attacks without pause or the game crashes when it notices you with A. (`DARKSIGN_ENEMY_AI_SPEFFECTS=ds3`)
- **H: Each character's own AI rows only.** As A, but a DS3 enemy that DS3 places with another family's AI (the Cemetery of Ash hollows run the Hollow Soldiers' rows) gets its own character's first AI row instead of that family's (DARKSIGN_ENEMY_SHARED_THINK=0), as up to 1.0.417, when those hollows circled the player without attacking. Use when a ported enemy freezes or the game crashes when it notices you with A. (`DARKSIGN_ENEMY_SHARED_THINK=0`)
- **I: Map-script AI markers on the enemy row.** As A, but the markers DS3's map scripts put on an enemy only to steer its AI (Ariandel's wolves get one when their pack leader is gone) are on its enemy row from the start (DARKSIGN_ENEMY_SCRIPT_MARKERS=keep), as in 1.0.442, when Ariandel's wolf only circled and never attacked. Use when a ported enemy behaves worse than in 1.0.442. (`DARKSIGN_ENEMY_SCRIPT_MARKERS=keep`)

### Ported DS3 enemies' glows (`enemy-glow`, rebuilds the content)

Try the next option if: A ported DS3 enemy's glow is the wrong colour or missing (the Jailer's lantern, Pontiff Sulyvahn's swords, the Soul of Cinder), a part of it glows far too brightly or draws black, or the game crashes or hangs loading a ported enemy that glows.

- **A: DS3's glows (default).** DS3 materials with a glow map are drawn on Elden Ring's glowing character material with DS3's glow texture, and the effects DS3's map scripts keep on an enemy (Pontiff's sword auras) are part of its enemy row.
- **B: Glowing materials, effects from the enemy row only.** As A, but ported enemies get only the effects their DS3 enemy row names, not the ones DS3's map scripts add or take off (DARKSIGN_ENEMY_SCRIPT_EFFECTS=0), as up to 1.0.326. (`DARKSIGN_ENEMY_SCRIPT_EFFECTS=0`)
- **C: No glowing materials.** Glowing DS3 materials are drawn plain, without their glow (DARKSIGN_ENEMY_GLOW=0), as up to 1.0.326. Use when a ported enemy draws black or crashes the game with A. (`DARKSIGN_ENEMY_GLOW=0`)
- **D: Every effect of the enemy row.** As A, but a ported enemy keeps every effect its DS3 row names, also those that lose to a higher priority effect of their category in DS3 (DARKSIGN_ENEMY_CATEGORIES=0), as in 1.0.340, when the Jailer showed its blue and cyan lights over the orange one. (`DARKSIGN_ENEMY_CATEGORIES=0`)
- **E: Glows, without DS3's mid-move mask changes.** As A, but the moves that show or hide parts of a ported enemy's model (Iudex Gundyr's Pus of Man body and red eyes when he transforms) only do so when its Elden Ring template does that too (DARKSIGN_ENEMY_DRAW_MASK=0), as up to 1.0.403. Try it if a ported enemy turns invisible or grows extra parts in the middle of a move. (`DARKSIGN_ENEMY_DRAW_MASK=0`)

### Where the DS3 maps' own flags are kept (`map-flags`, rebuilds the content)

Try the next option if: In a DS3 map, bosses come back, opened doors and shortcuts close again or picked-up items return after a reload, or the pre-flight check says a map flag block is MISSING.

- **A: The slot map's own flags where the game keeps them (default).** A DS3 map's flags move with it into its Elden Ring slot's map flags (e.g. 4500xxxx for m45_00), the way Elden Ring's legacy dungeons number theirs, wherever the game keeps that block; flags of blocks it doesn't keep (all of the Painted World's m19_70, each colosseum's 7th block) get numbers in JortPob's saved blocks, as with B.
- **B: JortPob's saved blocks.** The maps' own flags get new numbers in the saved ranges of the bottom-edge sea tiles (1030294000 and up), the blocks JortPob's Morrowind port keeps all its flags in (DARKSIGN_MAP_FLAGS=common). Saves made with A lose their DS3 map progress. (`DARKSIGN_MAP_FLAGS=common`)
- **C: Every flag in the slot.** As up to 1.0.422: every DS3 map flag moves into its slot's map flags, including blocks the game doesn't keep (DARKSIGN_MAP_FLAGS=slot), where the Painted World's flags were never set and Friede's fight and cutscene pot broke. (`DARKSIGN_MAP_FLAGS=slot`)


## Seamless Co-op

### How Seamless Co-op is loaded (`seamless`, at launch, no rebuild)

Try the next option if: With Seamless Co-op ticked: the game won't start, Seamless' menu doesn't appear, or you can't connect.

- **A: me3 native, offline start (default).** me3 loads your ersc.dll before our DLLs; me3 starts the game offline and Seamless uses Steam's networking.
- **B: me3 native, online start.** Same, but me3 starts the game online (start_online = true), in case Seamless needs the game's network layer up. Our DLL still blocks FromSoftware's servers outside co-op. (me3 `start_online = true`)
- **C: ModEngine2.** Load Seamless through ModEngine2's external_dlls, the way Seamless' own docs show (no modded sounds under ModEngine2). (ModEngine2)

