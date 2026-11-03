# Benchmark: how Elden Ring mods add weapons, movesets, sounds and effects

Researched 2026-11-03 (cloud only, no PC access). Every source below is also listed in [CREDITS.md](../../CREDITS.md).
Use the checklist at the end for **each new addition** (weapon, armor, spell, enemy) before building it.

## Summary

The community pipeline for a new weapon with its own moveset is:

1. **Params** in Smithbox: a new EquipParamWeapon row, a free TAE section, `spAtkcategory` ("Special Motion
   Category") pointing at that section, SwordArtsParam for the weapon art.
2. **TAE** in DSAnimStudio: duplicate a similar TAE into the free section (70 to 399 for weapon movesets), then
   swap in new animations and edit hit, sound and effect events.
3. **Behaviour** with ERClipGeneratorTool: "Import from Anibnd" adds one `hkbClipGenerator` per new animation to
   `c0000.behbnd`, under the CustomManualSelectorGenerator for that animation ID.
4. **HKS**: ivi's decompiled `c0000.hks`, edited where the source TAE (or a weapon art TAE) is named.
5. **Sounds**: copy the Wwise Event, Action, container, Sound and .wem objects into `cs_main.bnk` (rewwise
   bnk2json, er_soundbank_helper, now Yonder) and load with a loader that supports banks (vswarte's ModEngine2
   fork, or me3).
6. **Effects**: FXR files edited with the @cccode/fxr library or FXR Playground.

Our porter already does steps 2, 3, 5 and 6 automatically, and in the same way. The two places where we differ,
and where the community approach looks easier, are **motion categories** and **weapon arts**.

## Findings that change what we do

### 1. Use `spAtkcategory` for new movesets instead of the motion-category DLL hook (verify on Friede first)

- Ours: each ported weapon gets a new `wepmotionCategory` (u8). Categories above 255 need
  `Mods\ER\motion-categories.txt` plus the test-menu DLL hook, which last logged "motion: game code not found" and
  fell back.
- Community: Themyys' *Introduction to Moveset Modding* leaves the Weapon Motion Category unchanged and sets the
  **Special Motion Category** (`spAtkcategory`, u16 at +0xEE). Paramdex describes it as "special attack category,
  50 to 999 allowed". The guide says it overrides the motion category's animations and is the field for custom
  movesets. Weapon moveset TAEs should use sections 70 to 399.
- Why it matters: no DLL hook and no 255 ceiling. The porter currently never writes Elden Ring's
  `spAtkcategory` (it only reads DS3's, where the field means the weapon art).
- Open questions for the PC test: how many of 70 to 399 are free in Elden Ring 2.7.2 (we need about 111 movesets),
  and whether moves the new section doesn't define fall back to the base `wepmotionCategory` (which would let us
  drop the ER-only ImportOtherAnim copies).

### 2. Weapon arts go through SwordArtsParam and TAE 600 to 999

- Elden Ring arts are TAE sections 600 to 999. SwordArtsParam's "Sword Arts Type" is the section minus 600
  (a726 is type 126). The weapon points at the art with `swordArtsParamId`.
- `c0000.hks` lists every art TAE in the functions of the art it was copied from; the new ID must be added wherever
  the source appears (with the same minus-600 offset). ivi's hks is required for arts even if unedited.
- For DS3 arts (Friede's a232 etc.), the port should create a SwordArtsParam row and an a6xx to a9xx TAE, not
  touch `spAtkcategory`.

### 3. Jump attacks replay on landing unless bound to a variable

- Themyys: for weapon movesets, bind the Start Time of every clip whose animation ends in `70` to the behaviour
  variable `TimeActEditor_10` (up to five), otherwise jump attacks play again on landing. Check our generated
  clips do this when Devin tests jump attacks.

### 4. Dodges and idles live elsewhere

- Rolls are in a000. `wepmotionOneHandId` / `wepmotionBothHandId` ("Motion Position") pick the 1H/2H idle TAE
  (0 to 254) and override a000. DS3 idle/roll differences need those, not the moveset section.

### 5. The animation ID limit has no community workaround

- ERClipGeneratorTool gives each new generator `max(m_animationInternalId) + 1`, exactly as MovesetPort does, and
  skips animations that already have a generator. No mod adds thousands of clips, so our ~30,000 of 32,767 is
  our own problem to solve before DS1R and Bloodborne (for example by not creating generators for moves that only
  ImportOtherAnim the template, if finding 1 lets them fall back).

## Findings that confirm what we do

| Area | Community | Ours | Gap |
|---|---|---|---|
| New weapon rows | Smithbox, new EquipParamWeapon rows | Porter writes rows from Paramdex defs | None |
| Weapon models | Older armor/weapon ports replaced vanilla `parts` files (KRDCD, Noctis) | New model IDs, nothing replaced | Ours is better |
| TAE | DSAnimStudio, duplicate a similar TAE, edit events | Built from DS3 TAE + ER template, new binder IDs in order | Same; binder ID order rule matches the Nexus run-animation guide |
| Behaviour | ERClipGeneratorTool, one clip per new animation under the CMSG for its ID | Same via HKLib | Add the TimeActEditor_10 binding (finding 3) |
| Animations | Retargeted by hand in DSAnimStudio (Rainer's video); Blubber's repository already has DS3 player weapon animations and Sister Friede retargeted to the ER player | Automatic HKX conversion, 103 of 150 bones bound | Use Blubber's Friede/DS3 clips as a visual reference to judge our conversion (reference only, not shipped) |
| Sounds | Copy Event, Action, container, Sound and .wem into cs_main; children placed below their parent and added to the parent's child list in ascending order; volume via the container's `Volume` (about -15 loud to -3 quiet) | Same objects cloned into cs_main from DS3 FMOD audio decoded to PCM wems | Check child-list sort order and volume; set any `StateInfo` on copied PlaySound events to 0 |
| Sound loader | Official ModEngine2 can't load banks; mods ship vswarte's ME2 fork, newer ones use me3 | Switching to me3 | None |
| Effects | @cccode/fxr, FXR Playground; FXRs can also play sounds (Challenger Andy) | @cccode/fxr via Jint | Check DS3 FXRs for sound references |
| HKS | ivi's EldenRingHKS for any art or special R2 behaviour | Not used yet | Needed for arts (finding 2) |

## Bosses: Midir and Sister Friede

No public mod was found that ports Midir or Friede into Elden Ring as an enemy. The closest work:

- Blubber's *Player Animation Repositories* include Sister Friede's animations (via Mayhem's DS3 player-skeleton
  versions) and many DS3 enemy and boss animation sets retargeted to the Elden Ring player.
- Chocotavo's video on porting Lady Maria (Bloodborne) as a custom Elden Ring NPC, including cloth simulation.
- Rainer's Elden Ring animation retargeting video.

Those are the starting points for an enemy port; nothing found replaces a full chr, behaviour and AI port.

## Per-addition checklist

For each new weapon, armor piece, spell or enemy:

1. Search Nexus (Elden Ring and DS3), the soulsmodding wiki tutorial index and GitHub for an existing port or tool.
   Note what it does in the table above if it's new.
2. Moveset: free TAE section in range (weapons 70 to 399, spells 400 to 599, arts 600 to 999, idles 0 to 254),
   `spAtkcategory` for the moveset, SwordArtsParam + hks entry for the art.
3. Behaviour: one clip per new animation, jump attacks bound to TimeActEditor_10, count against the 32,767 limit.
4. Sounds: Wwise objects cloned into cs_main in parent order, child lists sorted, volume checked, me3 loader.
5. Effects: FXR converted with @cccode/fxr, resources remapped, any FXR sounds ported too.
6. Credit every tool, mod or guide used in CREDITS.md in the same PR.

## Sources

- Themyys, *Introduction to Moveset Modding (Elden Ring)*: https://docs.google.com/document/d/1quFnkKMRAx5mApbB7LRDPByYZyz6J2-7dFtUwk-NQpM
- Themyys with Challenger Andy, *Soundbank Editing*: https://docs.google.com/document/d/1lNov-a0DwnMY2yZywH3hFYzuoDfndofguvZmAnLDo-U
- Soulsmodding wiki tutorial index: http://soulsmodding.wikidot.com/tutorial:main
- The12thAvenger, ERClipGeneratorTool: https://github.com/The12thAvenger/ERClipGeneratorTool
- ividyon, EldenRingHKS: https://github.com/ividyon/EldenRingHKS
- Paramdex ER EquipParamWeapon def: https://github.com/soulsmods/Paramdex/blob/master/ER/Defs/EquipParamWeapon.xml
- ndahn, er_soundbank_helper: https://github.com/ndahn/er_soundbank_helper and Yonder: https://github.com/ndahn/yonder
- vswarte, rewwise: https://github.com/vswarte/rewwise
- Blubber, Player Animation Repositories: https://www.nexusmods.com/eldenring/mods/3027
- Romano, DS3/Sekiro weapon moveset pack (credits ERSoundBankLoader, Blubber's repositories): https://www.nexusmods.com/eldenring/mods/9319
- Nexus article, merging animation mods: https://www.nexusmods.com/eldenring/articles/228
- Nexus article, transplanting run animations (binder ID order): https://www.nexusmods.com/eldenring/articles/789
- nikonikoneko, Weapon Catalysts (hks by special motion category): https://www.nexusmods.com/eldenring/mods/7222
- KRDCD, Ringed Knight Set: https://www.nexusmods.com/eldenring/mods/1061 ; Noctis, Armor of Thorns: https://www.nexusmods.com/eldenring/mods/3115
- Mods bundling vswarte's ModEngine2 fork for sound: https://www.nexusmods.com/eldenring/mods/6384 , https://www.nexusmods.com/eldenring/mods/6340
