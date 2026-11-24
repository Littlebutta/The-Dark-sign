# Probe 20: was the player ever close enough for the prompt? (2026-11-24)

probe18/p18a1-sword.png shows the player about 3-4 m from Gundyr and the sword. Row 74000007 reaches 1.5 m around
his dummy 100 (9000: 1 m, 6000 on the c1000_9002 talk dummy: its own small radius), so the walk-ups may never have
been in range. The Firelink "E Talk" prompt showed because an invisible c1000 talk dummy stood at the warp spot.

Tool: p18-Program.cs here (adds `warp`). Same hash/restore rules.
1. `p18 warp <m45_00 emevd> <out> 4`: 4 s after load, the player is short-warped to Gundyr's dummy 100 with
   45005811's own 2004[41] (the exact spot the pull warps to). 45005811 is untouched (3[24] 74000007 on Gundyr).
   Plain load at Gundyr, don't move after the warp; screenshot at 6 s and 8 s. Prompt? Then press E: does the pull
   play (player 60750, Gundyr's pull, stand-up at about 10 s)? PR #282 isn't in this build, so he'll still die at the
   stand-up; that's expected.
2. If no prompt: turn the camera / take one step toward the sword hilt, screenshot, E again.
