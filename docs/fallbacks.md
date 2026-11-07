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

- **A: Overlay and hotkeys (default).** F1 opens the menu (drawn into the game's DirectX 12 frames). The hotkeys work too: Ctrl+Shift+F1 starts the DS3 run, Ctrl+Shift+F2 leaves it, Ctrl+Shift+1-9 warps to a ported map, Ctrl+Shift+F3 gives every DS3 weapon.
- **B: Hotkeys only.** No overlay (nothing hooks DirectX 12); the same hotkeys do the main jobs. A beep means the game took it, two beeps that it couldn't (load a character first). (game `DKSIGN_NO_OVERLAY=1`)

### Content build speed (`build-threads`, rebuilds the content)

Try the next option if: The content build fails or gives different results from one run to the next.

- **A: Every core (default).** port-weapons and port-maps run side by side, each on every core.
- **B: One core.** Everything one step at a time (DARKSIGN_THREADS=1), as builds were before 1.0.12x. Slower, but rules out a threading bug. (`DARKSIGN_THREADS=1`)


## Weapon movesets

### Where a weapon's DS3 moveset goes (`moveset-slot`, rebuilds the content)

Try the next option if: A ported weapon swings with Elden Ring's moves (R1 or R2), or with a different Elden Ring weapon's moves.

- **A: spAtkcategory (default).** The DS3 moveset goes in the weapon's Special Motion Category, the way Elden Ring's unique weapons and community moveset mods do it; the row keeps its template's motion category.
- **B: wepmotionCategory.** The DS3 moveset replaces the weapon's motion category itself (builds up to 1.0.12x); the template's special section, if it has one, is left in place. (`DARKSIGN_MOVESET_FIELD=wmc`)
- **C: Both fields.** The DS3 moveset goes in spAtkcategory and in wepmotionCategory, so whichever one the game reads for a move finds it. Class rules (powerstance, guard) then follow the new section, a copy of the template's. (`DARKSIGN_MOVESET_FIELD=both`)

### Which DS3 animation replaces each move (R2, charged R2) (`anim-map`, rebuilds the content)

Try the next option if: Some moves of a ported weapon are DS3 but others (often R2, charged R2 or two-handed attacks) still look like Elden Ring.

- **A: Behaviour selector names (default).** An Elden Ring move gets the DS3 animation whose behaviour selector has the same name, when each game's selector holds exactly one animation.
- **B: Names, then neighbours' offsets.** Moves the names don't pair take the ID offset their paired neighbours share (e.g. Elden Ring 0305xx -> DS3 0303xx), when that DS3 animation exists for the weapon. (`DARKSIGN_ANIM_MAP=offsets`)
- **C: B plus the hand-checked heavy table.** B, plus the heavy-attack pairs found while porting Friede's scythe (Elden Ring 030500/030501/030505 = DS3 030320/030330/030321, two-handed 032xxx = DS3 034xxx), which win. (`DARKSIGN_ANIM_MAP=table`)

### When the next attack can start (R1 chains) (`combo-windows`, rebuilds the content)

Try the next option if: R1 doesn't chain to the second swing, swings are cut short, or you can act too early or too late.

- **A: Elden Ring windows, stretched (default).** Elden Ring's combo and cancel windows from the template move, stretched onto the DS3 swing between matching hits and recovery points. No window after the last hit opens before DS3's own first one, multi-hit DS3 swings anchor on their last hit, and the template clip's length limits are cleared, so no move is cut short.
- **B: DS3's own windows.** DS3's JumpTable events copied as they are (DARKSIGN_DS3_JUMPTABLES=1). Some DS3 IDs mean different things in Elden Ring. (`DARKSIGN_DS3_JUMPTABLES=1`)
- **C: Elden Ring windows, shifted.** Elden Ring's windows moved to the first DS3 hit and not stretched (DARKSIGN_SWING_ALIGN=0), as the first moveset builds did. (`DARKSIGN_SWING_ALIGN=0`)
- **D: As Friede's first clean R1 (1.0.89).** A without the 2026-11-05 guard (DARKSIGN_TIMING_GUARD=0): windows may open before DS3's own recovery, and the template clip's length limits stay. Use it if A makes chains feel late. (`DARKSIGN_TIMING_GUARD=0`)

### Moves DS3 doesn't have (Elden Ring-only actions) (`er-moves`, rebuilds the content)

Try the next option if: The game hangs while loading, or Elden Ring-only moves of a ported weapon (e.g. guard counter) T-pose or do nothing.

- **A: Import the template's animation (default).** The new category points at the template's own animation, as Elden Ring's categories do. Keeps the animation binder small.
- **B: Copy the template's animation.** A full copy under the new category (DARKSIGN_COPY_ER_ANIMS=1). Bigger binder (a 4x binder once hung loading). (`DARKSIGN_COPY_ER_ANIMS=1`)

### DS3 animation format (`anim-format`, rebuilds the content)

Try the next option if: DS3 moves look broken (twisting limbs, jitter, sliding) or the game hangs while loading.

- **A: Spline-compressed (default).** DS3 animations re-encoded as Havok spline animations, like Elden Ring's own.
- **B: Interleaved (uncompressed).** Every frame stored as is (DARKSIGN_ANIM_INTERLEAVED=1). Exact, but about 5x bigger: the first build like this hung loading. (`DARKSIGN_ANIM_INTERLEAVED=1`)

### Jump attacks (`jump-attacks`, rebuilds the content)

Try the next option if: A ported weapon's jump attack plays again when you land, or the landing freezes.

- **A: Template clip settings (default).** Each new behaviour clip shares its Elden Ring template clip's variable bindings, as copied.
- **B: Start Time bound to TimeActEditor_10.** Every new clip of a jump attack (animation ID ending in 70) gets its own binding of Start Time to TimeActEditor_10, as Themyys' moveset guide says jump attacks need. (`DARKSIGN_JUMP_BINDING=1`)

### TAE event groups for new DS3 events (`event-groups`, rebuilds the content)

Try the next option if: DS3 effects or sounds of some moves never happen while others do, or a move freezes the character.

- **A: Add missing groups (default).** A DS3 event whose type the Elden Ring move doesn't use gets an event group copied from another move of the template.
- **B: Only the move's own groups.** Such events are left out (DARKSIGN_ADD_EVENT_GROUPS=0). (`DARKSIGN_ADD_EVENT_GROUPS=0`)


## Two-handed split

### Split weapons (Friede's scythe, twinblades) (`two-hand`, rebuilds the content)

Try the next option if: Two-handing a split weapon doesn't split it, or the second blade floats, sticks to the body or sits in the wrong hand.

- **A: DS3 positions, matched by bone (default).** Each model's DS3 dummy poly translated to the Elden Ring player dummy on the same bone.
- **B: An Elden Ring split layout.** The weapon takes a whole WepAbsorpPosParam row from an Elden Ring weapon that already holds a second model in the left hand when two-handed (the game's own working layout). (`DARKSIGN_ABSORP=er`)
- **C: DS3 dummy IDs as they are.** DS3's dummy poly IDs copied without matching them through the bones (both games number most hand and back dummies alike). (`DARKSIGN_ABSORP=raw`)
- **off: Off (template positions).** Ported weapons keep their template's WepAbsorpPosParam row (DARKSIGN_ABSORP=0): no split. Tells whether the split is what breaks something. (`DARKSIGN_ABSORP=0`)


## Weapon sounds and effects

### DS3 weapon sounds (`sounds`, rebuilds the content)

Try the next option if: Swings are silent, play the wrong sound, or sound pitched down or muffled.

- **A: Embedded, own container, registered (default).** DS3 samples decoded to PCM and embedded in cs_main.bnk, each Sound in a random container of its own under the template's actor-mixer (the layout Yonder and Spellforge use). The new sound numbers are listed in the game's own sound index (sound\soundbanksinfo.mobnkinfo), the swing events are one-shot 129s that play to the end, and each sample also gets a loose copy in sd\wem (what made Nightreign Ultimate Voices' new sounds play).
- **B: Streamed loose files.** Same layout, but each sample is a loose .wem in sd\wem\NN\ that me3 streams (DARKSIGN_STREAM_SOUNDS=1), the way RogueRing ships its sounds. (`DARKSIGN_STREAM_SOUNDS=1`)
- **C: Inside Elden Ring's whoosh container.** The Sound goes under the template's own container (DARKSIGN_SOUND_CONTAINERS=0), as builds up to 1.0.12x did. That container lowers the pitch and filters it. (`DARKSIGN_SOUND_CONTAINERS=0`)
- **D: Played by our DLL (no Wwise).** Elden Ring's sound bank is left alone. The DS3 samples are written as WAV files and the test menu DLL plays them on its own audio stream whenever a ported swing's sound event runs (DOOM RING's approach). Always audible, but not 3D-positioned. Elden Ring 2.7.2 only. (`DARKSIGN_SOUND_METHOD=dll`, game `DKSIGN_DLL_SOUNDS=1`)
- **E: A bank of our own, loaded by Torrent.** cs_main.bnk is left exactly as the game has it. The DS3 sounds go into a bank of their own (sd\cs_c9990.bnk, built like the community's custom banks) that Torrent's NpcParam rows load, the way Yonder's guide keeps a mod bank always loaded; the game's index lists it. Use it if A or B silence the whole game. (`DARKSIGN_SOUND_BANK=own`)
- **F: As builds up to 1.0.161.** A without this round's changes: the game's sound index isn't edited, the swing events keep the template's playback settings, and there are no loose copies (DARKSIGN_SOUND_INDEX=0, DARKSIGN_SOUND_ONESHOT=0, DARKSIGN_SOUND_MIRROR=0). Use it if A breaks something B, C and E don't. (`DARKSIGN_SOUND_INDEX=0`, `DARKSIGN_SOUND_ONESHOT=0`, `DARKSIGN_SOUND_MIRROR=0`)
- **off: Off (Elden Ring sounds).** Ported weapons keep the Elden Ring template's swing sounds (DARKSIGN_DS3_SOUND_EVENTS=0). Not DS3, but never silent. (`DARKSIGN_DS3_SOUND_EVENTS=0`)

### DS3 weapon effects (trails, sparks) (`effects`, rebuilds the content)

Try the next option if: The game crashes when a ported weapon swings, or its effects are missing or wrong.

- **A: DS3 effects (default).** DS3's FXR effects converted to Elden Ring's format (@cccode/fxr), with the sounds they play.
- **B: DS3 effects without their sounds.** Same effects; the sounds inside them are left out (DARKSIGN_FXR_SOUNDS=0). (`DARKSIGN_FXR_SOUNDS=0`)
- **C: DS3 and Elden Ring effects together.** Moves spawn DS3's effects and keep the template's Elden Ring trails and sparks too (DARKSIGN_KEEP_ER_EFFECTS=1), so a DS3 effect that doesn't show still leaves Elden Ring's instead of nothing. (`DARKSIGN_KEEP_ER_EFFECTS=1`)
- **off: Off (Elden Ring effects).** Ported weapons keep the template's effects (DARKSIGN_DS3_EFFECT_EVENTS=0). (`DARKSIGN_DS3_EFFECT_EVENTS=0`)


## DS3 maps

### Which Elden Ring map IDs hold the DS3 maps (`map-slots`, rebuilds the content)

Try the next option if: Warping into a DS3 map fails (nothing happens, endless loading, crash, or you land in the colosseum).

- **A: Colosseum slots (default).** Firelink/Cemetery of Ash, High Wall and Undead Settlement replace the three colosseum arenas (m45_00-02), which single-player never visits; plus an m50 test copy of the Cemetery.
- **B: New map IDs.** Each DS3 map gets a map ID Elden Ring doesn't ship (m50_00, m51_00, m52_00), built from the colosseums' file formats. Works if the F1 menu's m50 test warp works. (`DARKSIGN_MAP_SLOTS=new`)

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

- **A: Everything visible (default).** Every map part draws everywhere in the map (Elden Ring's draw groups ignored).
- **B: DS3's draw groups.** Parts keep DS3's draw and display groups (port-maps --ds3-groups), as DS3 culls them. (`port-maps --ds3-groups`)

### Enemies and bosses in the DS3 maps (`map-enemies`, rebuilds the content)

Try the next option if: A DS3 map crashes or hangs while loading, enemies stand in walls or fall through the floor, or a boss fight never starts.

- **A: DS3 characters where ported, stand-ins elsewhere (default).** Every DS3 enemy and boss stands where it did in DS3, with its DS3 entity ID so the converted boss and trap events find it. Characters the enemy porter has ported (darksign_chr_map.tsv) are the real DS3 ones; the rest are Elden Ring enemies of the same kind (knight, soldier, dog, dragon...), bosses get Elden Ring bosses.
- **B: Elden Ring stand-ins only.** The same placements, but always the Elden Ring stand-ins, even for ported DS3 characters (port-maps --enemies standin). Tells whether a ported character is what breaks. (`port-maps --enemies standin`)
- **off: Off (no enemies).** The DS3 maps are empty of enemies (port-maps --enemies none), to explore them or to find out whether enemies are what breaks a map. (`port-maps --enemies none`)

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

