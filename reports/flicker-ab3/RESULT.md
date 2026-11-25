# Map 1 (Firelink) flicker A/B, round 3: release 1.0.632 (2026-11-24)

**Short answer: neither J nor M brings back I's result, and the test is confounded.** Both J (corner-shade weights
only) and M (local lights 1/2 only) had far fewer brightness jumps than the as-built control, in both of their runs.
But both stayed dark (24-26, against I's 82 in round 2), and in 1.0.632 enemies attack the player at the warp spot,
so every run has a fight in it. My call: **J is the closest to steady** (lowest frame-to-frame change, 0 still jumps
in both runs); M is a little less steady while moving. Neither one alone reproduces I. Either the fix needs both
together (or another light-set value), or the fight in the scene is masking the difference. I can't separate those
two from these runs.

## Method
Same as round 2 (scripts in `scripts/`, plus `Flk3One.ps1`, which copies the variant into place, closes the game, boots
it through the launcher's PLAY with FlkBoot.ps1 and runs FlkRun.ps1). For every run the game was fully closed, the
gparam copied in, the game started from PLAY. Then: Ctrl+Shift+N1, wait 18 s, capture for 16 s (about every 16 ms),
stand still until 4.0 s, then W 2.5 s, turn, W 2.5 s, turn back, W 2 s. Ctrl+Shift+F8 recorded frame times for 12 s.
At boot, FlkBoot printed the gparam hash, which proves the game read the intended file for each run (see the table).
GraphicsConfig.xml stayed untouched: RaytracingQuality MAX, SHA256 1293A274... in every run.
Brightness = mean R+G+B over a 48x27 grid (765 = white). "Jumps" = frame-to-frame changes bigger than 8.
The control was A-asbuilt from Work\scratch\flicker-ab (SHA256 1C8288D8...), not the installed 1.0.632 file.

The first A632 attempt didn't count. A Windows Terminal window was in front, so the game never got the keys and the
capture showed the title screen. I minimized that window and ran A632 again. Only the valid run is in the table.

| run | file (hash at boot) | brightness all / still / moving | mean change still / moving | jumps > 8 still / moving | biggest jump | fps | median frame |
|---|---|---|---|---|---|---|---|
| A632 | A-asbuilt (1C8288D8) | 34.5 / 36.4 / 37.2 | 0.91 / 0.88 | 8 / 16 | 20.1 | 25.4* | 17.6 ms* |
| J | J-hostssao (BCF719F0) | 24.1 / 27.0 / 24.5 | 0.39 / 0.28 | 0 / 1 | 18.8 | 12.3 | 81.0 ms |
| M | M-hostlocallights (E37A9B79) | 24.1 / 29.5 / 24.6 | 0.38 / 0.58 | 0 / 4 | 12.7 | 16.2 | 60.1 ms |
| A632b | A-asbuilt, repeat (1C8288D8) | 36.4 / 40.2 / 33.9 | 0.76 / 0.80 | 3 / 7 | 15.9 | 14.9 | 62.6 ms |
| Mb | M, repeat (E37A9B79) | 24.7 / 16.6 / 26.3 | 0.39 / 0.70 | 0 / 4 | 10.6 | 20.6 | 43.4 ms |
| Jb | J, repeat (BCF719F0) | 26.4 / 32.6 / 28.9 | 0.29 / 0.37 | 0 / 2 | 10.6 | 10.7 | 91.7 ms |

\* In A632 the frame times were mixed: median 17.6 ms but 111 hitches over 50 ms. Some of the 12 s probably ran
fast, maybe during a loading or menu moment. The other runs show the usual RT MAX 11-21 fps.

Full perf lines are in `runs.txt`.

## Reading
- **Control:** both as-built runs flicker clearly (24 and 10 jumps in total), with mean change 0.76-0.91. That's the
  same picture as round 2.
- **J** (SsaoWeight0/1/3 = 0.5/0/0): 0 still jumps in both runs, 1-2 while moving, and the lowest mean change of all
  runs (0.28-0.39). That's close to I's change numbers (0.19 / 0.44). But it's darker than the control, not 3x
  brighter like I.
- **M** (local lights 1/2): 0 still jumps in both runs, but 4 moving jumps in both and a higher moving change
  (0.58-0.70). It's better than the control and worse than J. It's dark too.
- So the darkness of the built light sets doesn't come from either part alone. I's brightness needs something J and
  M don't change on their own, or both together.
- **Confound:** on this build, at the Ctrl+Shift+N1 spot, a red-haired NPC and an armoured knight fight the player
  during the capture (see `Jb-sheet.png`, frames 6-30, and `A632-0.png`). Blood, sparks and the moving enemies cause
  brightness jumps on their own, and the "stand still" part isn't really still. The jump counts here are therefore
  not cleanly comparable to round 2. All runs share the same confound, so the ordering (control >> M > J) still means
  something, but the absolute numbers don't. The next round should warp somewhere with no enemies (or turn enemy AI
  off), and could try J and M together.

## Files
`<run>.tsv` (ms, brightness per frame), `<run>-0.png` (first frame), `<run>-sheet.png` (one frame every 0.5 s),
`<run>-times.txt` (input times in capture ms), `runs.txt` (raw summary lines), `scripts/` (scripts used).

## Restore
Before the runs, the installed `Mods\ER\param\drawparam\m45_00_0000.gparam.dcx` was backed up to
`Work\scratch\flicker-ab\backup3\` (SHA256 714961B801CEE9867ABEF40313E71C26EDC42F1E8EDACBE402347E45B030AF49). After the
last run, with the game closed, it was copied back. Verified: SHA256 714961B801CEE9867ABEF40313E71C26EDC42F1E8EDACBE402347E45B030AF49.
GraphicsConfig.xml was never written; after the runs its SHA256 is still 1293A274B5A4759E5B77FE303B3BB27114C21799927810109D6176CA3225C6C6.
Game and launcher are closed. No rebuild was started.
