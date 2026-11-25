# Map 1 flicker A/B, round 4: N (J+M together), release 1.0.636 (2026-11-24)

**Short answer: N doesn't match I, for brightness or for steadiness.** The test didn't go as planned, though. Ctrl+Shift+F9
didn't stop the attack. The install was rebuilt to 1.0.636. The warp landed at two different spots, so the 4 runs
split 3 + 1.
- At the Cemetery of Ash spot (A, N, I, all at the same spot with no enemies): N is by far the darkest (38.6, against
  104 for as-built and 142 for I). I is brighter than as-built, as in round 2. N is not.
- At the Firelink spot from rounds 2-3 (N repeat only): N is dark (26.6) and has 1 / 7 jumps, which is as-built level
  (round 3 as-built: 3 / 7 and 8 / 16; round 2 I: 0 / 0).
- So putting the colosseum's corner-shade weights and local lights together (N) doesn't give I's result. Whatever
  makes I bright and steady is among the other light-set values I changes.

## What happened, in order
1. **Install changed to 1.0.636.** The launcher had been closed after round 3. When I started it again it was
   v1.0.636. My script's PLAY click started a full rebuild (52 min) instead of the game. I let it finish rather than
   cancel it halfway. The rebuilt `m45_00_0000.gparam.dcx` has the same SHA256 as 1.0.632's (714961B8...AF49), so the
   gparam base is unchanged. The rest of the mod is 1.0.636.
2. **Ctrl+Shift+F9 turns on but doesn't stop the attack.** The log says `enemies ignore player: on ... applied (bytes
   90 90)` at eldenring.exe+33ee8b. In the first control run (`A-f9only-*`) the red-haired NPC and the armoured knight
   still fought the player at the Firelink spot, and the HP bar drops in the sheet. I kept that run as evidence.
3. **Added Ctrl+Shift+F4 (clear spawned enemies)** after F9 in every later run. It logged "No spawned enemies to
   clear", so those attackers aren't spawned enemies. F4 did nothing either way.
4. **Warp target / load time.** After that run, the game continued from a save in the Cemetery of Ash, and the warp
   (Ctrl+Shift+N1) load took longer than the 18 s wait. Two captures started on the loading screen (`A-badload*`; the
   second one landed in the Cemetery). I raised the wait to 60 s. A, N and I then all landed at the same spot: the
   Cemetery of Ash start, among the trees (`A-0.png`, `I-0.png`). No enemies were near.
5. **The N repeat landed at Firelink instead** (`Nb-0.png`, same view as rounds 2-3). Rule 2 (two tries per problem)
   applies here: this was the second time I couldn't control the spot, so I stopped instead of trying a third fix and
   rerunning everything.

## Table
Same method and numbers as round 3 (brightness = mean R+G+B over a 48x27 grid; jump = frame-to-frame change > 8;
still = first 4 s; then W 2.5 s, turn, W 2.5 s, turn back, W 2 s; captures about every 16 ms; F8 records 12 s of frame
times). Ray tracing MAX in every run; GraphicsConfig.xml was never written (1293A274...).

| run | file (hash at boot) | spot | brightness all / still / moving | mean change still / moving | jumps still / moving | biggest jump | fps | median frame |
|---|---|---|---|---|---|---|---|---|
| A-f9only (evidence) | A-asbuilt (1C8288D8) | Firelink, fight | 36.4 / 47.6 / 36.6 | 0.56 / 0.61 | 4 / 5 | 13.5 | 14.5 | 64.5 ms |
| **A** | A-asbuilt (1C8288D8) | Cemetery | 103.9 / 108.5 / 89.3 | 0.75 / 2.87 | 0 / 40 | 72.0 | 54.2 | 16.7 ms |
| **N** | N-hostssao-locallights (E80E5AAD) | Cemetery | 38.6 / 32.5 / 37.2 | 0.16 / 0.51 | 0 / 1 | 11.7 | 54.8 | 16.7 ms |
| **I** | I-hostlightsets-but-sunangle (2059EA05) | Cemetery | 142.0 / 142.4 / 131.0 | 0.80 / 2.81 | 1 / 39 | 72.7 | 54.4 | 16.7 ms |
| Nb | N (E80E5AAD) | Firelink | 26.6 / 23.2 / 27.5 | 0.53 / 0.85 | 1 / 7 | 18.0 | 17.7 | 49.8 ms |

## Reading
- **Cemetery spot:** the game runs at about 54 fps here, against 11-21 at Firelink. The moving-phase jumps for A and I
  (40 / 39, biggest 72) come mostly from the camera turning through bright sky and the walk through a dark gap
  (frames 15-21 in `A-sheet.png`), so this spot doesn't tell flicker apart from scene changes while moving. N has fewer
  jumps only because the whole scene is about 3 times darker. Still phase: all three are steady (0-1 jumps).
- **Brightness:** N is darker than as-built at both spots. It's the opposite of I (brighter than as-built at the
  Cemetery here, and about 3x as bright at Firelink in round 2).
- **Steadiness at Firelink:** a single N run is at as-built level, not I's 0 / 0. That's one run, so it's weak on its
  own, but it agrees with the brightness result.

## Next round
To get a clean comparison, the warp spot has to be the same in every run, and the Firelink attackers have to go:
- Fix the save position first (for example warp, quit cleanly so the save sits at Firelink, then run each variant).
- Find out why F9 doesn't stop the Firelink NPC and knight. They may be scripted (event) enemies, not ones the
  "ignore player" patch covers.
- To find the light-set value behind I's result, split I's remaining light-set values (everything except corner-shade
  and local lights).

## Files
`<run>.tsv` (ms, brightness per frame), `<run>-0.png` (first frame), `<run>-sheet.png` (one frame every 0.5 s),
`<run>-times.txt` (input times in capture ms), `runs.txt` (raw summary lines), and `scripts/`. `FlkRun4.ps1` is
FlkRun.ps1 plus F9 + F4 after the warp and a 60 s wait. `Flk4One.ps1` is the per-run wrapper. `A-badload*` are the two
captures that started during the load.

## Restore
Before the runs, the installed gparam was backed up to `Work\scratch\flicker-ab\backup4\` (SHA256
714961B801CEE9867ABEF40313E71C26EDC42F1E8EDACBE402347E45B030AF49). The 1.0.636 rebuild produced the same hash. After
the last run, with the game closed, I copied it back and verified it: 714961B801CEE9867ABEF40313E71C26EDC42F1E8EDACBE402347E45B030AF49.
GraphicsConfig.xml SHA256 is unchanged: 1293A274B5A4759E5B77FE303B3BB27114C21799927810109D6176CA3225C6C6. Game, launcher
and porter are closed. The only rebuild was the one the launcher started on its own when it updated to 1.0.636.
