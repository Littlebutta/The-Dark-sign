# Deacons of the Deep fight start: read-only data (2026-11-24)

Build installed: 1.0.632 (`darksign_build.txt`). DS3 m35_00_00_00 (Cathedral of the Deep) maps to Elden Ring slot
**m45_05_00_00** (`darksign_bosses.tsv`, warp request flag 1039292408). No game was launched and no mod file was changed.

Files:
- `ds3-m35_00_00_00.txt`: DS3 `Work\extract\DS3\event\m35_00_00_00.emevd`, DS3 instruction names (DarkScript3 ds3-common.emedf).
- `ported-m45_05_00_00.txt`, `ported-common.txt`, `ported-common_func.txt`: the installed `Mods\ER\event\` scripts, Elden Ring names.
  Same dump tool and layout as reports/boss-start.
- `darksign_bosses.tsv`, `darksign_flags.tsv`: raw copies from `Mods\ER`.
- `ds3-m35_00_00_00.msb`: DS3 MSB. The DS3 install is not unpacked (no `Game\map\mapstudio` folder), so this is the
  porter's extracted copy from `Work\extract\DS3\map\mapstudio\m35_00_00_00.msb` (already DCX-free, MSB3 bytes), not a `.msb.dcx`.
- `ported-m45_05_00_00.msb`: `Mods\ER\map\mapstudio\m45_05_00_00.msb.dcx` with DCX removed (raw MSB bytes).
- `port-log-m35-lines.txt`: matching lines (m35, m45_05, boss arenas, Deacon, 3500800, 3502800, 13505810, 13505805; case-insensitive,
  with line numbers) from `port-maps-20261124-213059.txt` and `porter-port-maps-20261124-203938.log` (the 1.0.632 build run).
- `enemy-port-deacons.tsv`: header plus `Work\reports\enemy-port.tsv` rows for c5220, c5221, c5222, c5223, c5225.
