# Sword hitbox / bonfire evidence (read-only pull, 2026-11-25)

Collected on Devin's PC from mod build **1.0.667** (built 17:27-18:18 MST). No game was launched, no build was
started, nothing under `TheDarkSign\Mods` or the saves was changed.

**A rebuild (launcher/porter 1.0.669) started at 19:56 while this was being collected** and cleared parts of
`Mods\ER`. See `BUILD.txt`. What that cost is listed under "Not produced" below.

| # | File | What | Made with |
| --- | --- | --- | --- |
| 1 | `BUILD.txt` | versions, logs, time stamps, the rebuild | file listings, launcher log |
| 2 | `portlog-lines.txt` | matching lines (+3 after) of `porter-port-maps-20261125-172703.log` | text filter |
| 2 | `hitshapes-lines.txt`, `enemy-port.tsv` | "hit shapes" / "cloth bones": these are not in the console log, they are in the notes column of `Work\reports\enemy-port.tsv` (one row per enemy) | text filter; whole file copied too |
| 2 | (full logs) | not in the repo: `logs/` is git-ignored here; they stay on the PC as `TheDarkSign\Work\logs\porter-port-maps-20261125-172703.log` and `porter-port-weapons-20261125-172703.log` | - |
| 3 | `ported-m45_00_00_00.txt`, `ported-common.txt`, `ported-common_func.txt` | the mod's event scripts as built by 1.0.667 | `tool\shprobe emevd <file> er-common.emedf.json` |
| 4 | `ds3-2003-72.txt` | DS3 2003[72] with 6 lines either side and who starts the event | `shprobe scan` over `Work\extract\DS3\event` (31 files) |
| 4 | `ds3-player-anims.txt` | DS3 2003[18] / 2003[01] on entity 10000 | same |
| 5 | `flags-2026-11-24-build-1.0.632.tsv`, `flags-2026-11-24-dsmaproot.tsv`, `flags-2026-11-22-build-1.0.582.tsv` | OLDER copies of `darksign_flags.tsv` | copy (paths below) |
| 6 | `c7112.chrbnd.dcx` | the mod's file as built 17:37:47 | copy |
| 6 | `tae-mod-a000-selected.txt` | mod `c0000.anibnd.dcx` a00.tae, 7 entries | `shprobe tae` (same layout as the porter's tae-dump) |
| 6 | `tae-ds3-a000-selected.txt`, `tae-er-a000-selected.txt` | DS3 / vanilla Elden Ring, same entries | `DarkSignPorter tae-dump DS3 0` / `ER 0`, sections filtered |
| 7 | `o000100-anims.txt` | DS3 o000100 and o400100: files, model, animation files, TAE events | `DarkSignPorter object-diag model` |
| 7 | `ported-bonfire-asset.txt`, `ported-asset-unpacked\` | the asset of entity 45001950 and its binders (decompressed) | `shprobe msbpart`, `object-diag model` |
| 8 | `actionbutton.txt` (+ both params, all rows, as TSV) | ER row 74000002, DS3 row 9351 | `DarkSignPorter dump-param` |

## Things worth knowing before reading

- The event dump tool that made `reports/boss-start/*.txt` was not found on the PC, so `tool\shprobe` was written to
  give the same layout. Two differences: float arguments show the raw 32-bit value followed by the value, e.g.
  `Reaction Distance=1084227584(5f)`; and `Parameters=` of an Initialize instruction lists every parameter word, not
  only the first.
- DS3 2003[72] (Set Bonfire Warping State) occurs **once** in all 31 DS3 scripts: `m40_00_00_00` event 14005101 line 12,
  right after `2003[18]` plays animation 60430 on entity **4001950** (the object, not the player).
- Entity 45001950 in the ported map is asset **AEG920_612**, which `darksign_objects.tsv` gives as the port of DS3
  **o400100**, not o000100 (o000100 -> AEG920_000). Both are in items 7.
- Mod player TAE: the seven entries are **identical to vanilla Elden Ring's** (0 differing lines). Event counts:
  60430 has 2 events (DS3: 47), 60750 has 4 (DS3: 46), 60760 has 3 (DS3: 35), 63010 has 51 (DS3: 27).
  60431 is in none of the three a00.tae files.
- ActionButtonParam 74000002 does not exist in vanilla Elden Ring; the row is from the mod's regulation.bin
  (copied at 19:56 while it still carried its 18:15:47 time stamp).

## Not produced

- **The current `darksign_flags.tsv` (item 5).** It was in `Mods\ER` at 19:51 (135,726 bytes, written 18:15:47) and
  was deleted by the 19:56 rebuild before it was copied. The three older copies are:
  - `Work\src-deacons-check\reports\deacons-start\darksign_flags.tsv` (next to darksign_build.txt 1.0.632; 135,362 bytes; the date on disk, 2026-11-24 23:54, is the checkout time)
  - `Work\scratch\dsmaproot\TheDarkSign\Mods\ER\darksign_flags.tsv` (2026-11-24 05:09:44; 96,784 bytes; a scratch build of main + port/throws)
  - `Work\scratch\modsER-backup-20261124\darksign_flags.tsv` (2026-11-22 21:15:35; build 1.0.582; 93,820 bytes)
  The flags the 1.0.667 build used are visible in the three `ported-*.txt` dumps, and `flags:` lines are in
  `portlog-lines.txt`. If a copy of the file the 1.0.669 build writes is added later it is named
  `flags-current-1.0.669.tsv`.
- `aeg920_000_l.geomhkxbnd.dcx` (collision of the o000100 port) was removed by the rebuild before its file list was taken.
- Only the a00.tae of c0000 was dumped; other TAE files of the player were not searched for 60431.
