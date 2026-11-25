# Map 1 (Firelink) flicker A/B, round 2: release 1.0.630 (2026-11-24)

**Short answer: ray tracing causes the flicker, together with the built gparam's light sets.** If either one goes,
the flicker stops:
- Ray tracing OFF (K): 0 jumps and the lowest frame-to-frame change, about 2 times brighter, and **41 fps instead of
  13-16**.
- Ray tracing still MAX, but the gparam light sets set to the colosseum's values with our own sun angle (I): 0 jumps,
  about 3 times brighter. The frame rate stays at 15 fps.

The sun angle doesn't matter (G is no better than as built), and neither does the light-set layout (H, "all sets
like set 0", still flickers). What matters is the light-set *values*.

## Method
These runs follow reports/flicker-ab round 3 (scripts in `scripts/`). Before each run the game was fully closed and
started again through the launcher's PLAY, so it re-read both files. Each run then: warp with Ctrl+Shift+N1 (in front
of the shrine door), wait 18 s, capture for 16 s, stand still until 4.0 s, then W 2.5 s, turn, W 2.5 s, turn back,
W 2 s. Screenshots came about every 16 ms. Brightness = mean R+G+B over a 48x27 grid (765 = white). The grid is
denser than in round 1-3, so compare the numbers within this round only. Ctrl+Shift+F8 started the frame-time
recording at about 0.3 s and stopped it at about 12.4 s, so it covers 12 s.
"Jumps" = frame-to-frame changes in brightness bigger than 8.

| run | change | brightness all / still / moving | change still / moving | jumps > 8 still / moving | fps | median frame |
|---|---|---|---|---|---|---|
| A630 | as built (RT MAX) | 33.5 / 38.3 / 34.1 | 0.50 / 0.88 | 1 / 10 | 12.8 | 73.7 ms |
| A630b | as built, repeat | 25.6 / 34.4 / 23.9 | 0.74 / 0.52 | 1 / 3 | 15.6 | 61.7 ms |
| **K** | **RaytracingQuality OFF** | **63.2 / 70.7 / 65.4** | **0.21 / 0.36** | **0 / 0** | **40.8** | **24.1 ms** |
| G | G-hostsunangle | 29.8 / 37.0 / 29.3 | 0.88 / 0.80 | 4 / 10 | 14.1 | 68.0 ms |
| H | H-allsets-like-0 | 28.6 / 32.6 / 28.2 | 0.55 / 0.65 | 2 / 8 | 15.3 | 63.2 ms |
| **I** | **I-hostlightsets-but-sunangle** | **82.1 / 91.9 / 85.4** | **0.19 / 0.44** | **0 / 0** | 15.2 | 62.6 ms |
| L (extra) | as built, RaytracingQuality LOW | 25.7 / 33.6 / 24.5 | 0.76 / 0.83 | 1 / 1 | 29.7 | 31.0 ms |

Full perf summaries, in the same order:
- A630: 155 frames, 12.8 fps, median 73.7 ms, 95% under 121.7 ms, 138 hitches > 50 ms, 31 > 100 ms
- A630b: 189 frames, 15.6 fps, median 61.7 ms, 95% under 96.9 ms, 153 > 50 ms, 6 > 100 ms
- K: 491 frames, 40.8 fps, median 24.1 ms, 95% under 26.7 ms, 0 > 50 ms
- G: 170 frames, 14.1 fps, median 68.0 ms, 95% under 115.0 ms, 139 > 50 ms, 20 > 100 ms
- H: 184 frames, 15.3 fps, median 63.2 ms, 95% under 103.7 ms, 143 > 50 ms, 10 > 100 ms
- I: 183 frames, 15.2 fps, median 62.6 ms, 95% under 104.8 ms, 132 > 50 ms, 13 > 100 ms
- L: 354 frames, 29.7 fps, median 31.0 ms, 95% under 50.9 ms, 20 > 50 ms, 0 > 100 ms

## Reading
- The as-built control varies a lot between two runs (12 vs 4 jumps), so a single jump count only means something if
  it's large. Results that held up: **K and I are the only runs with 0 jumps and both still and moving change under
  0.45**. Every run with the built light sets and ray tracing on is dark (24-38) and has jumps.
- Ray tracing at LOW (L) is just as dark as MAX and still has a jump or two, but it runs twice as fast as MAX.
  Turning ray tracing down doesn't fix the darkness. Only OFF, or the colosseum's light-set values, does.
- At RT MAX the game runs at 13-16 fps on map 1 with every gparam tried. The frame cost comes from ray tracing, not
  the gparam (I is bright and steady but still 15 fps). Frame times this long probably make the flicker worse: the
  RT shadow/AO denoiser builds up its image over several frames, and at 60-70 ms per frame it gets fewer frames to
  settle. That's my inference and I haven't tested it.
- The screenshot loop costs some GPU and CPU time, but it's the same in every run.
- The player position in the F8 log moves from 1.3 to 3.7 during the first ~2 s after the warp, even though no keys
  are pressed. The character is still settling, and that happens the same way in every run.

## Does ray tracing change sun shadows only at some levels?
Nothing official breaks it down per level. What I found: Elden Ring's ray tracing covers **shadows and ambient
occlusion only** (no reflections). PC has four levels; consoles only have on/off, and their RT shadows are said to
match PC High/Maximum. The normal ShadowQuality setting controls raster shadows, not ray-traced ones (a Nexus mod
author says so explicitly). One weak source says High ray-traces major light sources plus ambient light and Maximum
raises the precision. I couldn't confirm that. GraphicsConfig.xml has only one `RaytracingQuality` value (OFF / LOW /
... / MAX), with no separate sun-shadow switch. `Auto-detectBestRenderingSettings` is ON in this profile, but the
game kept the values I wrote. Measured here: LOW behaves like MAX for darkness, so the sun-shadow/AO path that darkens
the scene is already on at LOW.
Sources: [GamesRadar](https://www.gamesradar.com/after-months-of-waiting-elden-rings-ray-tracing-capabilities-leave-a-lot-to-be-desired),
[Wccftech](https://wccftech.com/elden-ring-ray-tracing-update-pc-test-improved-but-at-what-cost/),
[Laptop Mag](https://laptopmag.com/features/elden-ring-ray-tracing-comparisons-just-how-good-is-it),
[Nexus mod 6802](https://nexusmods.com/eldenring/mods/6802),
[evezone (weak)](https://evezone.evetech.co.za/performance-pulse/elden-ring-ray-tracing-high-maximum),
[GamingBolt](https://gamingbolt.com/elden-ring-ray-tracing-on-pc-requires-geforce-rtx-3060-ti-at-minimum).

## Files
`<run>.tsv` (ms, brightness per frame), `<run>-0.png` (first frame), `<run>-sheet.png` (one frame every 0.5 s),
`<run>-times.txt` (input times in capture ms), `runs.txt` (raw summary lines; the A630 line there shows the wrong perf
line, and the right one is in the table above).

## Restore
Both files were backed up to Work\scratch\flicker-ab\backup\ before the runs and copied back after them. Check after
the last run, with the game closed:
- m45_00_0000.gparam.dcx: SHA256 714961B801CEE9867ABEF40313E71C26EDC42F1E8EDACBE402347E45B030AF49 (matches the original)
- GraphicsConfig.xml: SHA256 1293A274B5A4759E5B77FE303B3BB27114C21799927810109D6176CA3225C6C6 (matches the original,
  RaytracingQuality MAX)

The game rewrites GraphicsConfig.xml when it starts. The text stays the same but the bytes change, so the original
was copied back after the last game was closed. No rebuild was started.
