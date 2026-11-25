# Probe 25: does the post-warp step work from a separate event? (2026-11-25, 1.0.636)

Probe 24: the F1 warp event (common.emevd 1039291408) shows nothing after its Warp Player, while a map-script
watcher passes the same "player in map" check. Likely: the warp event doesn't continue after the load. This probe
tries the fix design by hand: a pending flag before the warp, and a separate event that sets the entry flag after it.

1. Back up and hash Mods\ER\event\common.emevd.dcx and m45_05_00_00.emevd.dcx. Rebuild p23 from ../probe23/p23-Program.cs.
2. `p23 split <common.emevd.dcx> <common.emevd.dcx>` and `p23 watch <m45_05_00_00.emevd.dcx> <m45_05_00_00.emevd.dcx>`.
3. Overnight save, god mode, one F1 Fight warp to Deacons, close the menu, don't move, screenshot every 1 s to 30 s.
   Expect: 31 "Commence", then 20 "Duty Fulfilled" (entry flag on), 33 "Victory", 18 "Enemy Felled"; a BOSS BAR; the
   deacons standing up (anim 1700) and coming at the player. Note anything else (music if the log shows it).
4. Restore both files and the save (hashes), push shots + RESULT.md here.
