# Probe 15: clear "sword pulled" (45000802) on the Gundyr fight warp (release 1.0.620, 2026-11-24)

common.emevd copy (flagfix-output.txt): in event 1039291400 (waits on 1039292400 and warps), I inserted 2003[66]
Set Event Flag (type 0, flag 45000802, OFF), args 00000000 62A8AE02 00000000, right before its 2003[14] warp
(2D000000 32CCAE02 ...). One event changed.

Run: Gundyr save; F1 > DS3 run > Firelink Shrine / Cemetery of Ash > Corrupted Gundyr **Fight**. The warp landed in
the arena (p15-look.png, frame 2: Gundyr kneeling with the sword planted).
- Walked up beside him, facing him and the sword (p15-near.png, p15-near2.png): **no prompt**.
- Pressed E, then a 15 s burst at 29 ms (after-E-15s.png, one frame a second): **nothing**. No pull animation, he stays
  kneeling, no boss bar.
- Step 4 (fog wall 45001800 prompt): not done, because I don't know where that wall is in the arena from this camera.

common.emevd was restored (hash a324c2c7..., the same as the backup), and the save was put back.
