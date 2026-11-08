# Loading the DS3 maps under their own map IDs

Devin's question (2026-11-08): would the ported maps load better as their own maps, instead of taking over the
colosseum arenas (m45_00-02) and their names and logic? This note is the assessment and the plan. It is written
before any rewrite; the decisive facts come from one in-game test described below.

## What is in place already

The porter has had both methods since PR #46 and #56 (launcher fallback `map-slots`, `docs/fallbacks.md`):

- **A (default): colosseum slots.** Cemetery of Ash -> m45_00, High Wall -> m45_01, Undead Settlement -> m45_02,
  plus a **probe**: the Cemetery again under **m50_00_00_00**, a map ID Elden Ring doesn't ship, built from m45_00's
  file formats. The F1 menu's fourth warp (Ctrl+Shift+4, request flag 1039292203) goes there.
- **B: new map IDs.** Every DS3 map under its own new ID (m50_00, m51_00, m52_00), `DARKSIGN_MAP_SLOTS=new`.

So "load them as their own maps" needs no rewrite: the code exists, and the probe is the test of whether the game
accepts it. **The probe has never been warped to**: `dksign_testmenu.log` on Devin's PC (2026-11-08) shows the
flagcheck, two item gives and one "Start DS3 run" request, no warp request.

## What the game told us so far

The in-game flag check (dksign_testmenu.dll, run once a character loads) asks the game to keep one flag per block
the port uses. Result in Devin's 2026-11-08 log:

| Blocks | Result |
|---|---|
| 45000-45029 (the three colosseum slots' map flags) | kept, except 45006, 45016, 45026 |
| 50000-50009 (the m50 probe's map flags) | **all MISSING: "the game has no such block"** |
| 1030290, 1039290, 1039292 (JortPob-style sea-tile flags) | kept |

So the game keeps no flags for a map it doesn't know, and today it doesn't know m50. The map check of the same build
also printed the first bytes of Elden Ring's flag allocation lists, which turned out to be **plain text**:

```
legacymap.eventflagalloclist.dcx         ...\r\n0,m10_00_00_00,0\r\n1,m10_01_00_00,...
openmap.eventflagalloclist.dcx           ...\r\n200,m60_41_38_00,...
openmap_dlc02.eventflagalloclist.dcx     ...\r\n350,m61_53_46_...
```

One line per map, `index,mAA_BB_CC_DD,type`, numbered across the four files. That is the first concrete hook for a
new map ID: a map gets flag blocks by being listed. `FlagAlloc.WriteForNewIds` (this PR) now adds a line for every
new-ID slot of a build (the m50 probe included), copied from its template's line, and the map check reads the lists
properly instead of scanning them for binary values (its nine "format unconfirmed" warnings were noise).

Whether the game also **loads** a map it doesn't list is a separate question. Every map the game ships has a block
in its world tables; the flag check suggests those tables are built from data files the mod can extend, but the
warp is the only proof. The crashes seen so far don't bear on it: the 1.0.217 map crash was 6530 map piece meshes
pointing at a bone that doesn't exist, plus 15 collisions dropped by a threading bug (PR #90). Both would crash the
same way under any map ID.

## How other modders do it (RULE 3)

- **JortPob** (infernoplus, the Morrowind port) reuses existing IDs: it rebuilds Elden Ring's own m60 open-world
  tiles and keeps its saved flags in the empty sea tiles along the bottom edge of the world. It adds no map ID.
  Its flag layout is what our run and global flags already follow (CREDITS.md).
- **Dark Souls III**: the Souls Modding map list marks m36_00 "God Grave (Cut)" as an ID the game ships with no
  content; IDs like that are the community's home for extra maps. No DS3 mod loading an unshipped ID was found either.
- No published Elden Ring mod was found that loads a brand-new legacy map ID. Map for Goblins' notes and libER
  document the world structures but say nothing about adding maps. The Souls Modding Elden Ring map list names
  three maps with "Unknown" contents (m11_70, m11_72, m19_70): IDs the game ships and single-player never visits,
  candidate slots if new IDs turn out not to load.
- **Seamless Co-op** (ersc-docs) asks only that every player run the same mods and the same regulation; nothing
  about map IDs. Players on a map the game doesn't ship is untested with it, which is one more reason to prove the
  probe before moving every map.

## What each method costs

| | A: colosseum slots | B: own map IDs |
|---|---|---|
| Proven to load | the warp happens and the load reached the map (crashed on content, since fixed) | unknown until the probe |
| Flags | kept (blocks exist) | kept once the map is in the allocation list (this PR) |
| Leftover host files | the arena's `.nva`, `.mfr`, `.btl` lights, envmaps, `breakgeom`, `onav` still describe the colosseum; WorldMapLegacyConvParam and any arena-specific engine behaviour come along | none: the map starts clean; anything the game requires per map (and we don't write) is simply absent |
| Slots | 3 arenas, then "Unknown" IDs m11_70/m11_72/m19_70, then sea tiles the JortPob way | one per DS3 map, no limit |
| Locked save | Rescue save loads the vanilla arena under the same ID | nothing to fall back to; a save left in a crashing new-ID map needs the save file edited |
| Co-op | map IDs every client knows | untested |

## Recommendation

Don't rewrite anything yet. Decide with one test, on a **throwaway character**, in the next playtest build (which
carries this PR's allocation-list change, so the test is fair):

1. Load the throwaway character, wait for the pre-flight's flagcheck lines. If block 50000 now reads **ok**, the
   allocation list is the game's map registry for flags, and the game's data files can register a map.
2. Press **Ctrl+Shift+4** (warp to "Cemetery of Ash (new map ID test)"). Three outcomes:
   - **Loads and looks like the Cemetery:** switch the default to method B (one line in `MapSlots.All`), give every
     DS3 map its own ID, and drop the colosseum handling (Rescue save, empty arena scripts). Also extend Rescue save
     to a new-ID save: write a tiny valid map under that ID for one launch.
   - **Nothing happens, or an endless load:** the game refuses unknown IDs. Stay on A, and take the extra slots
     from IDs the game ships: first the three "Unknown" maps, then sea tiles.
   - **Crash:** send the Save test report; the crash offset says whether it died in map lookup (ID refused) or in
     content (same bugs as the colosseum slot, which then say nothing about IDs).
3. Whatever the outcome, keep fixing map content under the method that loads: those bugs are the same under both.

Under RULE 2 this is the first attempt at "maps under their own IDs"; the second and last is whatever the crash
offset points at.
