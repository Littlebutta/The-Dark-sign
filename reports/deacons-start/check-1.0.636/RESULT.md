# Deacons of the Deep fight start: in-game check of PR #286 (build 1.0.636)

**Result: the fight does not start.** Two warps, no boss bar, and the deacons stay seated at the altar, both from the landing spot and right next to the altar. Run B was skipped because Run A failed. No crash.

## Build
- `Mods\ER\darksign_build.txt`: **1.0.636**.
- Newest port report `Work\logs\port-maps-20261124-231859.txt` has the PR #286 line (`port-log-deacons-line.txt`):
  `boss arenas: Deacons of the Deep lands at -457.3 -196.5 -621.6 (area 領域_ボス部屋侵入（枢機卿団） at the boss's floor + 0.5 m), entity 45059970; flags: defeated 45050800, entry 45055805, first encounter 0; 8 fight events reset; entry set once the player stands in area 45052800`
- No launcher update or re-port was run.

## Setup
- me3 with `Tools\me3\darksign-er.me3` (test-menu DLL 2026-11-24 20:03), same env as `Work\scratch\probe-live-rtest.bat` plus `DKSIGN_GOD=1`. Save: the Rtest/Overnight save in `Work\scratch\saves-overnight`, backed up to `Work\scratch\deacons636-backup` first and restored afterwards (sha256 of every file matches).
- "Enemies ignore player" was **off** the whole time (checkbox unticked; no "enemies ignore player" line in the log).
- Warp: F1 > DS3 run > Cathedral of the Deep > Deacons of the Deep > **Fight** (there is no separate "Bosses" tab in this DLL).

## Run A (stand still)
- 23:50:44 warp requested; flag 1039292408 cleared by the event after 984 ms. Loading screen (`A0`) ended about 5 s after the click.
- The F1 key sent after the click didn't close the menu (pressed during the load), so the menu stayed on screen in the timed shots. The player did not move. The area under the menu where a boss bar would sit is visible and empty.
- `A1` (+3 s), `A2` (+8 s), `A3` (+15 s): player in the doorway facing the altar; deacons seated in rows by the altar (red eyes), **no boss bar, no movement**.
- `A4` (about 70 s after landing, menu closed): same, no bar, deacons idle.
- `A5`: walked forward about 3 s (W held) up to the deacons in front of the altar: **still no bar, deacons idle**. I couldn't check music (no audio capture); the log has no music line for m45_05 after the warp, while earlier sessions log `music: ...` lines on map warps.

## Second warp (repeat of A)
- 23:52:50 warp again from next to the altar; this time the request flag was cleared after 7203 ms. Menu again stayed open through the load.
- `A6` (~10 s) and `A7` (~30 s, menu closed): same landing spot, **no bar, deacons idle**.

## Run B
Skipped: Run A didn't start the fight.

## Crash
None. The game closed normally (window close) and me3 exited.

## Files
- `shots/`: the 8 screenshots above (full 1920x1080 client area, JPG; the Windows taskbar covers the bottom ~50 px).
- `dksign_testmenu-lines.txt`: this session's test-menu log with the per-block flag write/read checks removed.
- `darksign_build.txt`, `port-log-deacons-line.txt`.

## Notes for the fix
- The landing spot is at the doorway; standing there and walking up to the altar both failed, so either flag 45055805 is never set (area 45052800 not hit, or its event isn't running), or it is set and the deacons' start event doesn't react to it. Checking flag 45055805 live (e.g. a test-menu flag read after the warp) would tell which.
