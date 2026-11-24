# Probe 20 result (release 1.0.620, 2026-11-24): **the "Remove sword" prompt shows at dummy 100, and E starts the pull**

Edit (warp-edit.txt): a test event waits 4 s, then 2004[41] short-warps the player to Gundyr's dummy 100 (args
10270000 02000000 60A8AE02 64000000), the spot the pull itself warps to.

Plain load at Gundyr, a screenshot every 2 s (load-and-prompt-2s.png):
- Frames 2-4: the player at the arena entrance.
- **From frame 5 (about 8-10 s after the load prompt): the player stands at the sword, beside the kneeling Gundyr, and
  "E: Remove sword" shows** (prompt-remove-sword.png). It stays on through frames 5-10 with no movement.
- Pressed E, then a 15 s burst (after-E-1s.png, one frame a second): **the pull plays** (0-6 s: player at the hilt,
  red effects), the player steps back (7-9 s), **Gundyr stands up** (10 s), dust, **the "Iudex Gundyr" boss bar shows**
  (11-13 s), he's gone, and the bonfire flames at 14 s, as expected without PR #282.

So the row, the category, the event and the E handling all work. The prompt only shows within row 74000007's 1.5 m
of dummy 100, and dummy 100 is about 1.6 m from Gundyr's root, beside the hilt. My earlier walk-ups stopped 2-4 m
away, outside the circle. In DS3 you walk right up to the hilt.

Restored: emevd dd26e653...; the save is back.
