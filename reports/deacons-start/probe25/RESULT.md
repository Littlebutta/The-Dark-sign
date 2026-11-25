# Probe 25 result (2026-11-25, build 1.0.636)

**The split design didn't start the fight. The banners are the same as probe 24: "Commence" and "Demigod Felled" only.** No "Duty Fulfilled", "Victory" or "Enemy Felled", **no boss bar**, and the deacons stayed seated through t40. No crash.

## Setup
- p23 rebuilt from `probe23/p23-Program.cs` at 6cc0655. `p23 split` on common.emevd.dcx reported: "1039291408: pending flag on before the warp (index 14); event 1039291990 sets the entry flag after the load". `p23 watch` on m45_05_00_00.emevd.dcx added events 45059950-45059955 as in probe 24.
- Original hashes: common `fedc9179…afcef1`, m45_05 `2fc531a2…66bb`. Both were restored and match. The Overnight save was restored and every file's sha256 matches the backup.
- One launch (me3, Overnight save, `DKSIGN_GOD=1`). F1 Fight warp to Deacons at 00:36:31 local, menu closed with its X, no movement. Shots every 1 s to t40.

## Banner timeline (t = seconds after the click; `times.txt`)
| t | On screen |
|---|---|
| t00-t04 | Loading screen |
| t05-t06 | **"COMMENCE"** (31: player in area 45052800) |
| t07 | "?PlaceName?" (area-name banner) |
| t08-t10 | **"DEMIGOD FELLED"** (16: map-script watcher passed 3[08] 45 5 0 0), fading at t10 |
| t11-t40 | Nothing; no bar; deacons seated |

- Not seen: 20 (entry flag 1030297714), 33, 18, 2.
- Log: no music line after the warp; the only music lines are at start-up ("98 DS3 music regions listed"). Again no "request flag 1039292408 cleared by the event" line.

## Reading
Setting the entry flag from a separate event 1039291990 also never happened, or it set the flag somewhere watcher 45059951 doesn't see. Either 1039291990 isn't running (not started, or ended before the load), its pending-flag condition never matched, or the pending flag set before the warp didn't survive the map load. Probe 24 already showed the warp event never reached its post-wait banner. One possibility: the warp request event ends before the map changes, so the pending flag was never set at all.

## Files
- `shots/t04.jpg`-`t35.jpg`, `t40.jpg` (full 1920x1080 client area; the taskbar covers the bottom ~50 px), `contact-sheet.jpg` (t00-t40), `dksign_testmenu.log`, `times.txt`.
