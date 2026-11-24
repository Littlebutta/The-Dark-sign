# Map 1 (Firelink) flicker A/B, release 1.0.620 (2026-11-24)

Warped with Ctrl+Shift+N1. The spot is in front of the shrine's main door (flkA-0.png and flkB-0.png), not the open
courtyard. Each run did the same scripted moves: forward 2.5 s, turn, forward 2.5 s, turn back, forward 2 s, with an
11 s capture about every 26 ms. flkX.tsv lists each frame's mean brightness (R+G+B over a sparse grid; 765 = white).

| run | m45_00_0000.gparam.dcx | mean brightness | mean frame-to-frame change | jumps > 8 |
|---|---|---|---|---|
| A | as built | **28.8** | **1.74** | **19** |
| B | renamed to .bak (game falls back) | 87.4 | 0.60 | 2 |

With the built gparam the scene is about 3 times darker and changes much more from frame to frame, with 19 sudden jumps
against 2. In flkA-sheet.png (one frame every 0.5 s) the stone goes dark and bright between neighbouring frames.
Without it, the same moves give a steady, brighter picture. The file was restored afterwards (hash C9D620B90D5C1276
before and after).

## Round 2 (same scripted capture; files from Work\scratch\flicker-ab copied over m45_00_0000.gparam.dcx)
Capture ran at about 25 ms per frame in every run. That's the screenshot loop rate, not the game's frame time.

| run | file | mean brightness | mean frame-to-frame change | jumps > 8 |
|---|---|---|---|---|
| A2 | A-asbuilt (control, re-saved) | 29.0 | 1.46 | 13 |
| C | C-reach300 (fog volume 300 m) | 26.0 | 1.09 | 5 |
| D | D-startdist (fog 75 m, sun-in-fog start 170 m -> 10 m) | 30.6 | 1.19 | 19 |
| (B, first round) | no file | 87.4 | 0.60 | 2 |

The original was restored afterwards (hash C9D620B90D5C1276).

## Round 3: 4 s stand-still (no input) at the start of each 15 s capture, then the same moves
| run | file | mean brightness | still 0-4 s: change / jumps > 8 | moving: change / jumps > 8 |
|---|---|---|---|---|
| E3 | E-hostfog (fog group = colosseum's) | 30.9 | 0.71 / 0 | 1.18 / 7 |
| **F3** | **F-hostlightsets (light sets = colosseum's)** | **89.1** | **0.21 / 0** | **0.81 / 1** |
| B3 | no file | 91.1 | 0.20 / 0 | 0.51 / 0 |
| A3 | as built (original) | 33.6 | 0.96 / 1 | 1.43 / 8 |

Putting the **light sets** back to the colosseum's values gives nearly the no-file result: about 3 times brighter, and
the jumps go from 8 to 1. Putting the fog back changes almost nothing. Standing still, the built file shows 1 jump
and the most frame-to-frame change (0.96), against 0.2 for F and B. Most jumps come while moving. The original
file was restored (C9D620B90D5C1276).
