# Probe 24: does the F1 warp event get past "IF Player In Map 45 5 0 0"? (2026-11-25, 1.0.636)

Probe 23: the player is in the doorway area (banner 31) but entry flag 1030297714 never turns on. The F1 warp event
(common.emevd 1039291408) sets it after: wait 3 s, 3[08] IF Player In Map (45, 5, 0, 0), wait 2 s, 3[02] in area.
This probe shows which of those it reaches. Tool: ../probe23/p23-Program.cs (rebuild; now has `warp`).

1. Back up and hash Mods\ER\event\common.emevd.dcx and m45_05_00_00.emevd.dcx.
2. `p23 warp <common.emevd.dcx> <common.emevd.dcx>` and `p23 watch <m45_05_00_00.emevd.dcx> <m45_05_00_00.emevd.dcx>`.
3. Overnight save, god mode, one F1 Fight warp to Deacons, close the menu, don't move, screenshot every 1 s to 25 s.
   Banners:
   - 13 "Lost Grace Discovered": the warp event finished its 3 s wait and reached 3[08]
   - 22 "Map Found": the warp event passed 3[08] (player in map 45 5 0 0)
   - 16 "Demigod Felled": a separate watcher in the map script passed 3[08] 45 5 0 0 (+2 s)
   - 31 "Commence", 20 "Duty Fulfilled", 33 "Victory", 18 "Enemy Felled" as in probe 23; 2 "You Died" = defeated flag
4. Restore both files and the save (hashes), push shots + RESULT.md here.
