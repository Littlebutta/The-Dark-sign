# Overnight test, 28 Nov 2026 (launcher and content 1.0.735)

**Result: DS3 maps render on 1.0.735. No black frames, no crashes.** Step D (black-frame hunt) was not needed.

## Build and rebuild
- Before: content 1.0.733, built 27 Nov 17:00-18:55 local (115 min). Launcher self-updated to 1.0.735 on start.
- Rebuild (launcher "Rebuild content"): 28 Nov 00:37:08 - 01:30:37 local. port-weapons 11:39, port-maps 53:25. No failed step; validation 0 FAIL, 53 WARN.
- `Mods\ER\darksign_build.txt` = 1.0.735. regulation.bin hash is the same as on 1.0.733 (A9F4CF66...).

## Maps, ray tracing off (4 shots each, 90 degrees apart)
| Warp | Map | Result | Mean luma | Look |
|---|---|---|---|---|
| start | Cemetery of Ash | renders | 55.7 | grey rubble, dark walls |
| 1 | Firelink Shrine | renders | 63.2 | shrine lit, NPC visible |
| 2 | High Wall | renders | 109.2 | bright sky, towers, no black slabs |
| 3 | Undead Settlement | renders | 86.4 | grass, huts, cliffs |
| 4 | Painted World (Friede chapel) | renders | 66.3 | chapel, snow, candles |
| 5 | Lothric Castle | renders | 90.4 | sunlit, statues, fog door |
| 6 | Road of Sacrifices | renders | 43.2 | indoor ruin, mossy floor |
| 7 | Cathedral (Deacons arena) | renders | 44.6 | dark hall, HUD shown |
| 8 | Catacombs | renders | 43.6 | dark, lit shrine stairs |
| 9 | Irithyll | renders | 48.2 | dark church interior |

No shot is under mean luma 6. Full table: `luma.tsv` (near-black = luma under 10).

## Ray tracing on (owner's GraphicsConfig, RaytracingQuality MAX)
- High Wall: renders, mean 108.6. Firelink: renders, mean 61.1. Same look as RT off.

## Boss warps (PR #331)
- `darksign_bosses.tsv` lists 20 boss warps, including Dragonslayer Armour (flag 1039292406) and Twin Princes as "Lorian, Elder Prince" (1039292413). Both show a Fight button in F1 > DS3 run.
- Dragonslayer Armour: warp works, boss attacks at once, health bar "Dragonslayer Armour" shows (F1 menu was left open in these 3 shots).
- Twin Princes: warp works, intro cutscene plays with subtitles, then health bar "Lorian, Elder Prince" shows. The boss stays on the far dais in the shots. In the cutscene the camera sees outside the hall (snowy mountains behind Lorian, shots 1-2): worth a look.

## Found on the way
- **The F7 key reads as held down on this PC** (GetAsyncKeyState 0x76 stays down; injected key-ups do not clear it, so it is probably something resting on the keyboard or a stuck key). With F7 down, every Ctrl+Shift hotkey also toggles no-clip. In the first sweep that made every second warp (maps 2, 4, 3, 8) fall through the floor and die. Those shots are kept as `boot\x735nc-*.png`; the maps were redone with no-clip off and render. This did not happen on 27 Nov.
- F1 menu "Fight" buttons are small: clicks must land about 5 px lower than the old click math gives.

## Files
- `before-after-1.0.735.jpg`: BEFORE (27 Nov build, red) next to AFTER (1.0.735, green) for 5 maps.
- `sheet-n735-A.jpg`, `-B.jpg` (first sweep, includes the 4 fall-through rows), `sheet-n735-C.jpg` (the 4 redone maps), `sheet-n735rt.jpg`, `sheet-boss-dsa.jpg`, `sheet-boss-princes.jpg`.
- `shots\` has 57 JPEGs (quality 85): n735-{start,firelink,highwall,cathedral,painted,lothric,map3,map6,map8,map9}-1..4, n735rt-{highwall,firelink}-1..4, n735-boss-dsa-1..3, n735-boss-princes-1..6. They are 1280 px wide, the capture size; not upscaled to 1920. PNG originals are in `Work\reports\boot`.
- `luma.tsv`, `portlog-lines.txt` (698 matching lines, first 80 kept), `runB.txt` (run log).
- Scripts: `Work\scratch\night-1128\` (Night1128.ps1, RunB.ps1, Luma.ps1, Sheet.ps1).

## Safety checks at the end (02:02 local)
- eldenring.exe closed; me3 and the launcher are not running.
- ER0000.dksign, ER0000.dksign.bak and GraphicsConfig.xml restored from `night-1128\backup`; SHA256 of all three match `backup\hashes.txt`.
- Mods\ER: nothing was swapped in or moved aside; regulation.bin matches the backup hash.
- Key and mouse-button releases sent. Only F7 still reads as down, and it was down before I sent any input.
- Mouse did not move over 3 s (968,561). No PowerShell jobs or other PowerShell processes left running.
