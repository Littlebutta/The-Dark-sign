# Probe 24 result (2026-11-25, build 1.0.636)

**The F1 warp event (common.emevd 1039291408) never shows either of its banners: no 13 "Lost Grace Discovered" and no 22 "Map Found". The separate map-script watcher passes `3[08] Player In Map 45 5 0 0`** (16 "Demigod Felled"). So the map check itself works. The warp event either never finishes its 3 s wait or isn't running after the load. No boss bar, the deacons stayed seated, no crash.

## Setup
- p23 rebuilt from `probe23/p23-Program.cs` at 9cf5b74. `p23 warp` on `Mods\ER\event\common.emevd.dcx` reported: "1039291408: banner 13 before 3[08] (index 16), banner 22 after it". `p23 watch` on `m45_05_00_00.emevd.dcx` added events 45059950-45059955 (45059954 = banner 2, 45059955 = 3[08] + 2 s, banner 16).
- Original hashes: common `fedc9179…afcef1`, m45_05 `2fc531a2…66bb`. Both were restored afterwards and match. The Overnight save was restored and every file's sha256 matches the backup.
- One launch (me3, Overnight save, `DKSIGN_GOD=1`). F1 Fight warp to Deacons at 00:30:50 local; the menu was closed with its X right after. No movement.

## Banner timeline (t = seconds after the click; `times.txt`)
| t | On screen |
|---|---|
| t00-t05 | Loading screen |
| t06-t07 | **"COMMENCE"** (31: player in area 45052800) |
| t08 | COMMENCE fading into **"DEMIGOD FELLED"** |
| t09-t10 | **"DEMIGOD FELLED"** (16: map-script watcher passed 3[08] 45 5 0 0, +2 s) |
| t11-t35 | Nothing |

- Not seen at any time: 13 "Lost Grace Discovered" (warp event reached 3[08]), 22 "Map Found" (warp event passed 3[08]), 20, 33, 18, 2.
- Unlike probe 23 there was no "?PlaceName?" banner this time.
- As in probe 23, the log has **no** "ds3 run: request flag 1039292408 cleared by the event" line after the warp (`dksign_testmenu.log`). The 1.0.636 check runs earlier did log it (after 984 ms and 7203 ms). That fits the warp event not getting past its first wait after the map load.

## Files
- `shots/t05.jpg`-`t31.jpg` (full 1920x1080 client area; the taskbar covers the bottom ~50 px), `contact-sheet.jpg` (t00-t35), `dksign_testmenu.log`, `times.txt`.
