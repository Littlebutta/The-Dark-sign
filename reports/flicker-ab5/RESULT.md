# Map 1 flicker A/B, round 5: A-asbuilt vs I, 6 interleaved runs, release 1.0.636 (2026-11-25)

**Short answer: at a fixed spot with no enemies, I is exactly as steady as A. It isn't steadier, and it isn't worse.**
Both are flicker-free here: 0 jumps over 8 in the still phase in all 6 runs, and the biggest still-phase jump was
1.4-1.6 in every run. The one large, repeatable difference is brightness: I is about 43% brighter standing still
(117.9 vs 82.4) and 64% brighter during the pan (112.6 vs 68.4). The run-to-run spread is tiny (SD 0.7-1.5).

**Caveat: this isn't the Firelink spot.** The test ran at the Cemetery of Ash start, not the Firelink spot from rounds
2-3, because a save at the Firelink spot can't hold still (see "Spot" below). The Cemetery spot gave clean,
repeatable runs, but round 4 already showed it doesn't show the Firelink flicker. So this round says I adds no
instability and brightens the scene. It doesn't prove I fixes the Firelink flicker. Call for Firelink: **unclear**,
still.

## Spot (deviation from the brief)
- Devin's save (backup5, 20012440...029D) loads at the Firelink spot from rounds 2-3. About 13-14 s after the load,
  the scripted red-haired NPC and the armoured knight attack, and they kill the player. The respawn is at the
  Cemetery of Ash bonfire. Evidence is in `evidence/firelink-load-attack-sheet.png` (one frame every 0.5 s: calm
  until frame ~27, then the fight) and `evidence/firelink-death-respawn-sheet.png`.
- Moving away (holding W for 6 s straight after the load) didn't escape them either. The player died and respawned at
  the Cemetery again. Rule 2: that's two tries at holding the Firelink spot, so I stopped there.
- The warp (Ctrl+Shift+N1) also landed at the Cemetery start this time.
- So the test save (`testsave5`, F4FD0517...37CB1) is the save written after that respawn. It loads straight at the
  Cemetery of Ash start with no warp, no enemies near and about 57 fps. I put it back before every run and checked
  its hash each time.

## Method (same script, `scripts/Flk5Run.ps1`, every run)
Close the game. Copy the variant gparam in and put the test save back, both hash-checked. Check that GitHub's newest
release is still launcher-v1.0.636. PLAY in the launcher. The script stops if PLAY didn't start the game cleanly, and
checks that the game's parent chain is me3-launcher <- TheDarkSignLauncher. Then title -> Enter, Enter (Continue),
Enter, and wait 10 s.
- Ctrl+Shift+F8 (perf record) is pressed **5 s before** the capture. In Devin's binds, Shift on its own is backstep,
  so the hotkey makes the character backstep and the camera recentre. In the first two tries, F8 was pressed at
  capture start and the still phase began with that motion. I kept those as `A0-settling` / `A0-backstep` and left
  them out of the stats.
- Capture: about 16.7 ms per frame, 15.5 s, brightness = mean R+G+B over a 48x27 grid (765 = white), the same measure as
  rounds 2-4. **Still** = 0-8 s with no input. **Pan** = 8-14 s, camera only: 1800 mouse counts left over 3 s, then
  back over 3 s. That's about 90 degrees, calibrated on the compass (`evidence/pan-calibration-compass.png`), and the
  view returns to the start. Closing F8 at 14.5 s. "Jump" = frame-to-frame change over 8.
- Ray tracing MAX in every run. GraphicsConfig.xml was never written (1293A274...C6C6 before, during and after).

## Per run
| run | gparam | brightness still / pan | mean change still / pan | jumps >8 still / pan | biggest jump still / pan | fps (F8, 20 s) |
|---|---|---|---|---|---|---|
| A1 | 1C8288D8 | 81.9 / 69.0 | 0.340 / 2.363 | 0 / 29 | 1.5 / 25.3 | 57.6 |
| I1 | 2059EA05 | 117.2 / 111.6 | 0.369 / 2.445 | 0 / 34 | 1.6 / 29.3 | 56.7 |
| A2 | 1C8288D8 | 81.3 / 68.5 | 0.336 / 2.512 | 0 / 33 | 1.5 / 29.9 | 57.4 |
| I2 | 2059EA05 | 117.9 / 112.2 | 0.368 / 2.522 | 0 / 33 | 1.5 / 22.6 | 56.9 |
| A3 | 1C8288D8 | 84.1 / 67.8 | 0.353 / 2.070 | 0 / 23 | 1.6 / 27.0 | 56.3 |
| I3 | 2059EA05 | 118.6 / 113.9 | 0.375 / 2.210 | 0 / 24 | 1.4 / 32.7 | 56.6 |

## Per variant (mean ± SD, n = 3)
| | brightness still | brightness pan | mean change still | mean change pan | jumps still | jumps pan | biggest jump pan | fps |
|---|---|---|---|---|---|---|---|---|
| A | 82.4 ± 1.5 | 68.4 ± 0.6 | 0.343 ± 0.009 | 2.32 ± 0.23 | 0 | 28.3 ± 5.0 | 27.4 ± 2.3 | 57.1 ± 0.7 |
| I | 117.9 ± 0.7 | 112.6 ± 1.2 | 0.371 ± 0.004 | 2.39 ± 0.16 | 0 | 30.3 ± 5.5 | 28.2 ± 5.1 | 56.7 ± 0.2 |

## Reading
- **Still:** both are rock steady. I's mean change is 8% higher in absolute terms (0.371 vs 0.343), but I is 43%
  brighter, so relative to brightness it's lower (0.31% vs 0.42% of the level). Neither has a single jump over 2.
- **Pan:** the jumps come from the camera sweeping across sky and rock (frames 18-28 in the sheets), not from flicker.
  A and I match within run-to-run noise (28 ± 5 vs 30 ± 6 jumps, biggest 27 vs 28).
- **fps:** the same (57.1 vs 56.7).
- **Call:** at this spot, I is **the same** as A for steadiness and clearly brighter. For the Firelink flicker the
  question stays **unclear**. A clean Firelink A/B needs either the two attackers kept off for 20 s (they're scripted
  people, and F9/F4 didn't stop them in round 4) or a Firelink save point outside their aggro range, reached some other
  way than walking from the load point.

## Files
`A1..A3`, `I1..I3`: `<run>.tsv` (ms, brightness), `<run>-0.png` (first frame), `<run>-sheet.png` (one frame every
0.5 s), `<run>-times.txt` (input times in capture ms; perf-start is negative = before the capture), `<run>-cap.txt`.
`first-frame-A.png` / `first-frame-I.png` = the first frames of A1 / I1. `runs.txt` = raw lines, `summary.tsv` = per run +
per variant mean / SD / range. `A0-*` = the two discarded tries (F8 backstep during the still phase). `scripts/` =
Flk5Run.ps1, Flk5Summary.ps1, FlkCapture.ps1. `evidence/` = Firelink attack/death sheets and the pan calibration.

## Restore
With the game closed: ER0000.dksign and ER0000.dksign.bak back to
20012440E953CFB1422D3DD389875D913C059245AD2D6794C29FDED7E2EB029D (both). m45_00_0000.gparam.dcx back to
714961B801CEE9867ABEF40313E71C26EDC42F1E8EDACBE402347E45B030AF49. GraphicsConfig.xml is unchanged:
1293A274B5A4759E5B77FE303B3BB27114C21799927810109D6176CA3225C6C6. Backups are in Work\scratch\flicker-ab\backup5\. Game,
me3 and launcher are closed. The launcher stayed on v1.0.636, offered no update and did no rebuild.
