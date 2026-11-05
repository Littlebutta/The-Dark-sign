# Porting Dark Souls III maps into Elden Ring

Goal (Devin, 2026-11-03): port the DS3 maps into Elden Ring with everything working, and add an F1 menu tab that
starts a DS3 run inside Elden Ring.

## How it works

`DarkSignPorter port-maps` converts every DS3 map that has an Elden Ring slot in one run. The launcher runs it right
after `port-weapons`. If it fails, the weapons still load and the game starts; the log is in
`TheDarkSign\Work\logs\port-maps-*.txt`.

| Piece | DS3 | Elden Ring | Code |
|---|---|---|---|
| Layout | `map/mapstudio/m40_00_00_00.msb.dcx` (MSB3) | `map/mapstudio/m45_00_00_00.msb.dcx` (MSBE); each part is a copy of a vanilla part of the same kind with DS3's name, model, transform and entity ID | `Maps/MsbPort.cs` |
| Map pieces | `map/m40_00_00_00/m40_00_00_00_NNNNNN.mapbnd.dcx` | `map/m45/m45_00_00_00/...`; materials become cloned Elden Ring map matbins (`DKS_40_00_00_00_NNNN`) in `allmaterial_dlc01` | `Maps/MapModelPort.cs` |
| Textures | `map/m40/m40_000N.tpfbhd/.tpfbdt` | added to `map/m45/m45_0000.tpfbhd/.tpfbdt`, plus a flat metallic map | `Maps/MapTextures.cs` |
| Collision | `h40_00_00_00.hkxbhd` (Havok 2014 packfile) | `h45_00_00_00.hkxbhd` (Havok 2018 XML tagfile, same compressed-mesh tree); every triangle is checked after conversion | `Maps/CollisionPort.cs`, `Maps/MapCollisions.cs` |
| Navmesh | `map/m40_00_00_00/m40_00_00_00.nvmhktbnd.dcx` (Havok AI 2014) | `map/m45/m45_00_00_00/m45_00_00_00.nvmhktbnd.dcx` (Havok AI 2018 tagfiles); faces, edges, cluster graph and AABB tree copied as-is, costs hkHalf → float | `Maps/NavmeshPort.cs`, `Maps/MapNavmesh.cs` |
| Map events | `event/m40_00_00_00.emevd.dcx` | `event/m45_00_00_00.emevd.dcx`, converted instruction by instruction (see "Event scripts" below); the slot's own Elden Ring events no longer run | `Maps/EventPort.cs`, `Maps/MapEvents.cs` |
| Common events | `event/common_func.emevd.dcx` (only the events the ported maps start, and the ones those start) | added to `event/common_func.emevd.dcx` under IDs 103x291000-103x291999 | `Maps/MapEvents.cs`, `Maps/FlagMap.cs` |
| Enemies and bosses | MSB3 enemy parts | MSBE enemy parts at DS3's spot, on DS3's collision, with DS3's entity ID moved to the slot. The character is the ported DS3 one when `Mods\ER\darksign_chr_map.tsv` lists it, otherwise an Elden Ring stand-in of the same kind copied from a vanilla legacy-dungeon placement; DS3 bosses (boss health bar in DS3's events) get Elden Ring bosses. NPCs are left out. Fallbacks `map-enemies`. | `Maps/EnemyPort.cs` |
| Map connections | MSB3 connect collisions | MSBE connect collisions copied from a legacy dungeon's, pointing at the neighbouring map's slot (ones to unported maps are left out) | `Maps/MsbPort.cs` |
| Run start/leave | | two events in `common.emevd` wait for flags the F1 menu sets, then use the game's WarpPlayer | `Maps/RunEvents.cs`, `TestMenu/src/overlay.cpp` |

### Map slots

Elden Ring may not load map IDs it doesn't ship (JortPob, the Morrowind port, only reuses existing ones), so the
DS3 maps take over the colosseum arenas, which single-player never visits:

| DS3 | Elden Ring slot |
|---|---|
| m40_00 Firelink Shrine / Cemetery of Ash | m45_00 |
| m30_00 High Wall of Lothric | m45_01 |
| m31_00 Undead Settlement | m45_02 |

The other DS3 maps need more slots (or proof that new IDs load) and are listed in `MapSlots.Ds3Maps`.
Entity IDs move with the map: 4000xxxx becomes 4500xxxx. The run starts at the DS3 player point with entity
x990-x999, which gets entity 45000990.

### F1 menu, "DS3 run" tab

- **Start DS3 run at the Cemetery of Ash** sets flag 1039292100. An event in common.emevd clears it, sets
  1039290100 (run active, saved) and warps to m45_00_00_00 entity 45000990.
- **Leave DS3 run** sets 1039292101. The event clears the run flag and warps to the first Roundtable Hold warp target
  found in Elden Ring's own scripts.
- The tab lists the ported maps from `Mods\ER\darksign_maps.tsv`.

Flags are in the block of the empty sea tile m60_39_29 (1039290000+), the way JortPob allocates its flags.

## Checks

- `DarkSignPorter map-selftest` runs offline (no game files) and in CI. It checks collision conversion (XML and
  binary), template settings, layout conversion and write/read, start-point choice, group masks, run event
  instruction layouts against DarkScript3's definitions, and the return-point search.
- `DarkSignPorter map-survey` reports both games' map files for the slots on a PC with the games installed: paths,
  binder entry names, the DS3 start point, the host collision template, map materials and texture folders, and the
  warp targets. Run it first on the PC; it answers the open questions below.

### Event scripts

Both games run the same event system, and DarkScript3 documents every instruction of each (`ds3-common.emedf.json`,
`er-common.emedf.json`, downloaded at a pinned commit into the work cache), so the conversion is table-driven:

- Each DS3 instruction is matched to the Elden Ring one of the same name, or a known rename ("Object" became "Asset",
  "(De)activate Object" became "Change Asset Enable State", and so on). 261 of DS3's 360 instructions have one;
  `DarkSignPorter event-coverage <ds3.json> <er.json>` lists every pairing and what has none.
- Arguments are paired by name, then by what they hold (entity, flag, event), then by shared words; Elden Ring-only
  arguments keep DarkScript3's default.
- IDs move by what they are: the map's entities, events and flags become the slot's (14000500 -> 45000500), a warp or
  flag of another ported DS3 map goes to that map's slot, and DS3's global flags get new ones from the JortPob-style
  blocks (1030290000-1038290000, numbers 0-999). The assignments are saved in `Mods\ER\darksign_flags.tsv` and reused,
  so a save keeps its DS3 progress across rebuilds.
- Event parameters follow their argument, and values passed to an event move by what the event uses them for.
- A command with no Elden Ring form becomes a no-op on the same line (so skips and labels stay right). An event whose
  conditions can't convert is left out, with the lines that start it. A cutscene that warps becomes the warp; one
  that doesn't is skipped until cutscenes are ported.
- The report (`port-maps` log) counts events kept, instructions converted and the most common misses.

- DS3's "IF Action Button" conditions (doors, levers, chests, elevators, boss fog) become "IF Action Button in Area".
  Each distinct prompt gets an ActionButtonParam row (74000000+) in the mod's regulation, copied from row 6000 the way
  JortPob does, with DS3's dummy poly, reach and angle. The prompt text is Elden Ring's when the words match ("Open"),
  otherwise DS3's words are added to `menu_dlc02`'s ActionButtonText (74000000+). `Maps/ActionButtons.cs`.

Not converted yet: sound and BGM instructions, map ceremonies and world tendency. Doors and levers only work once the
DS3 objects themselves are ported (the next stage); until then the events wait for assets that aren't there.

### Enemy placement and the enemy porter

The enemy porter (thread "Bring Midir and Friede into Elden Ring") writes `Mods\ER\darksign_chr_map.tsv`, tab-separated,
one line per ported character, and port-maps uses it on its next run:

```
npc <DS3 NpcParam ID> <ER model> <ER NpcParam ID> <ER ThinkParam ID> [name]   one DS3 NpcParam row
chr <DS3 model>       <ER model> <ER NpcParam ID> <ER ThinkParam ID> [name]   any row of a DS3 model
```

An `npc` line wins over a `chr` line. Lines starting with `#` are ignored. Until a character is listed it gets a stand-in.

port-maps also writes `darksign_enemies.tsv` (every DS3 enemy it placed, with its slot part and entity) and
`darksign_er_enemies.tsv` (the Elden Ring enemies below) for the cross-game randomizer's enemy shuffle.

Stand-ins: port-maps reads every enemy placed in Elden Ring's legacy dungeons (Stranded Graveyard, Stormveil, Raya Lucaria,
Volcano Manor, Leyndell...) with its NpcParam name, sorts both games' names into kinds (knight, soldier, archer, mage,
skeleton, dog, rat, bird, crab, worm, serpent, lizard, tree, ghost, beast, giant, dragon) and takes the earliest
dungeon's enemy of the same kind, or the most common one when the kind has none. Bosses are spread over Elden Ring's
bosses (of the same kind when there is one). Stand-ins keep their Elden Ring stats; the player keeps Elden Ring's
jump, guard counter and the rest. Patrol routes and DS3's boss name text aren't ported yet.

## Open questions (answered by the first `map-survey` and in-game test)

1. Does WarpPlayer into m45_00 work offline, and does the arena load with our layout?
2. Do Elden Ring map textures live in `map/m45/m45_0000.tpfbhd` like DS3's?
3. Does the game accept collision as XML tagfiles inside the binder (JortPob says yes), or does it need binary?
4. Is the simd tree needed (`--simd` keeps DS3's)?
5. Do the cloned map materials draw the DS3 textures correctly (opaque, alpha, two-layer)?
6. Does Elden Ring need anything besides the .nvmhktbnd for a navmesh (an MSB entry or an .nva file)? `map-survey` lists every file type in both map folders.

## Next stages

1. Navmesh: converted since PR #44. If Elden Ring rejects converted navmeshes, fall back to generating them, as NavMeshStudio does with Recast.
2. Enemies, objects, items and bonfires from the MSB, with the enemy porter ("Bring Midir and Friede into Elden Ring").
3. DS3 map events: converted (see "Event scripts"). Next: action buttons (doors, levers, fog walls) as
   ActionButtonParam rows, map-to-map connect collisions, then cutscenes (remo files, camera and animations via Havok).
4. More slots for the remaining DS3 maps.
