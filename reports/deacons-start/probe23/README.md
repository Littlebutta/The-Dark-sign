# Probe 23: which step of the Deacons fight start fails (2026-11-25, build 1.0.636)

On 1.0.636 (with PR #286) the F1 warp to Deacons still gives no boss bar, and the deacons stay seated even when the
player doesn't move. Block 1030297 is kept (flagcheck). This probe adds watcher events to the ported Cathedral
script that show a banner at each step. No game data other than Mods\ER\event\m45_05_00_00.emevd.dcx changes.

Tool: p23-Program.cs (a scratch csproj referencing Porter/third_party/SoulsFormatsNEXT, like p18; DCX needs oo2core).
1. Back up Mods\ER\event\m45_05_00_00.emevd.dcx (hash it). `p23 watch <that file> <that file>`.
2. Back up saves as usual. Launch, F1 DS3 run > Cathedral of the Deep > Deacons "Fight". Close the F1 menu before the load ends.
3. Don't move. Screenshot every 1 s from the end of the loading screen to 20 s (or record). Note each banner and its time:
   - 31 "Commence": player inside area 45052800 (the doorway box)
   - 20 "Duty Fulfilled": entry flag 1030297714 is on (+1 s)
   - 33 "Victory": both at once (+3 s)
   - 18 "Enemy Felled": the deacons' start event passed its wait (flag 1030297749) (+6 s)
   - 13 "Lost Grace Discovered": defeated flag on (should never show)
   Is there a boss bar at any point?
4. Close the game, restore the emevd and saves (hashes), push screenshots + RESULT.md to reports/deacons-start/probe23/.
