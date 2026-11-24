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
