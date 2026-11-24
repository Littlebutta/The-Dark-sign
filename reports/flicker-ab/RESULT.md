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
