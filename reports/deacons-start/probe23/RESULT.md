# Probe 23 result (2026-11-25, build 1.0.636)

**Only banner 31 "Commence" shows. The player is inside area 45052800, but entry flag 1030297714 never comes on.** No "Duty Fulfilled", "Victory", "Enemy Felled" or "Lost Grace Discovered" appeared, and there was no boss bar at any point. The deacons stayed seated. No crash.

## Setup
- p23 was built from `p23-Program.cs` as a scratch net8 csproj. It references `Tools\Porter\SoulsFormats.dll` (same as p18) and copies `oo2core_6_win64.dll` next to the exe. I ran `p23 watch` in place on `Mods\ER\event\m45_05_00_00.emevd.dcx`, which added events 45059950-45059954.
  - Original sha256 `2fc531a2…66bb`, patched `5d014adc…8188`. Restored afterwards; sha256 matches the original.
- me3 launch (`Work\scratch\deacons636\launch.bat`), Overnight test save, `DKSIGN_GOD=1`. The save was restored afterwards; every file's sha256 matches the backup.
- One launch. F1 > DS3 run > Cathedral of the Deep > Deacons "Fight" at 00:25:18 local. The menu was closed with its X right after the click (this time it worked). No movement.

## Banner timeline (t = seconds after the click, one shot per second; `times.txt`)
| t | What's on screen |
|---|---|
| t00-t04 | Loading screen |
| t05 | First in-game frame. **"COMMENCE"** (banner 31: player in area 45052800) |
| t06 | "COMMENCE" still up |
| t07-t08 | "?PlaceName?" (area-name banner, not one of the probe's) |
| t09-t35 | Nothing: no banner, no boss bar, deacons seated |

The load ended at about t05, so the end of the load to +20 s is t05-t25 and every frame is kept. "Duty Fulfilled" would have come by about t07, "Victory" by about t09 and "Enemy Felled" by about t12 or later. None appeared.

## Reading
- The doorway area check works: the player lands inside 45052800.
- Entry flag 1030297714 is never set, though its block (1030297) passes flagcheck. So the event meant to set it on entering 45052800 isn't running, or it sets a different flag.
- The deacons' start event (1030297749) never passes, as expected without the entry flag.
- Log note: unlike the 1.0.636 check runs, this session's log has no "ds3 run: request flag 1039292408 cleared by the event" line after the warp. The warp still happened (`dksign_testmenu.log`).

## Files
- `shots/t04.jpg`-`t25.jpg`, `t30.jpg`, `t35.jpg`: full 1920x1080 client area. The taskbar covers the bottom ~50 px.
- `contact-sheet.jpg`: all 36 frames (t00-t35).
- `dksign_testmenu.log`, `times.txt`.
