# Probe 19 result (release 1.0.620, 2026-11-24)

Each run: Gundyr save, plain load, the usual walk-up beside him, screenshot, E, shots 2 s and 6 s after (p19-N-sheet.png).

| run | 45005810 lines dropped (to wait 0) | prompt? | E |
|---|---|---|---|
| 1 | 11 (2004[27] hitmask, args 60A8AE0200010000) | **no** | nothing; he stays kneeling |
| 2 | 11, 9 (2004[15] invincible) | **no** | nothing |
| 3 | 11, 9, 7 (2004[01] AI), Ctrl+Shift+F9 pressed after load | **inconclusive** | the walk-up ended away from the arena centre, with Gundyr not in view (with AI on he may have moved), so the test never reached him |

**Run 4 (hostile control): not done.** p18 near (p19-near.txt) shows no hostile Enemy within reach. Inside 40 m there
are only Gundyr, **c1000_9002 (entity 45000952, TalkID 899001000, at the sword, 0.3 m from Gundyr)**, the sword asset
45001952 and player/region parts. The nearest hostile is c7043_9005 (45000209), 46 m away outside the arena, which I
can't steer to blind.

Note: c1000_9002 is a talk dummy standing exactly at the sword. It was in probe 18's 6000 inject list, and no prompt
showed beside it either.

Restored: emevd dd26e653...; the save is back.
