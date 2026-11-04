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
| Map events | DS3's map script | the slot's own script is replaced with an empty one | `Maps/RunEvents.cs` |
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

## Open questions (answered by the first `map-survey` and in-game test)

1. Does WarpPlayer into m45_00 work offline, and does the arena load with our layout?
2. Do Elden Ring map textures live in `map/m45/m45_0000.tpfbhd` like DS3's?
3. Does the game accept collision as XML tagfiles inside the binder (JortPob says yes), or does it need binary?
4. Is the simd tree needed (`--simd` keeps DS3's)?
5. Do the cloned map materials draw the DS3 textures correctly (opaque, alpha, two-layer)?

## Next stages

1. Navmesh (enemies need it to walk): generate per map, as NavMeshStudio does with Recast.
2. Enemies, objects, items and bonfires from the MSB, with the enemy porter ("Bring Midir and Friede into Elden Ring").
3. DS3 map events (doors, elevators, fog walls, boss arenas) converted to Elden Ring EMEVD.
4. More slots for the remaining DS3 maps.
