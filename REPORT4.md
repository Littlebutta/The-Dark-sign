# Overnight test round 4, 28 Nov 2026 (content 1.0.739, 05:15 - 07:15 local)

**Result: no regression. On 1.0.739 as built, 10 of the 13 cutscenes checked play in the right place (5 fixed by
PR #334, 5 were right already and still are); 3 cannot be judged (black or fog in both builds). The Twin Princes'
second cut is still wrong; no second fix was written because no rule could be proven.**

## Build
- Launcher self-updated 1.0.737 -> 1.0.739 at 05:46 (tag `launcher-v1.0.739`, main 470c605 with CutsceneAnchor).
- Rebuild 05:47:30 - 06:42:00: port-weapons 11:43, port-maps 54:29. No failed step; validation 0 FAIL, 53 WARN.
- `darksign_build.txt` = 1.0.739; regulation.bin hash unchanged (A9F4CF66...).
- Port log: 42 cutscenes report "cut offsets moved". The built `s38_10_0000.cutscenebnd.dcx` is byte-identical to
  the file made in round 3 with `LuaProbe csfix`, so round 3's swap test tested exactly what is now built.

## The 13 cutscenes (F1 Cutscenes tab, 8 shots each, same moments; BEFORE 1.0.737, AFTER 1.0.739)
| Cutscene | DS3 file | 1.0.737 | 1.0.739 | Verdict |
|---|---|---|---|---|
| Abyss Watchers intro | s33_00_0010 | on snowy mountains | in the arena | right place now |
| Irithyll scene 0020 (Aldrich) | s37_00_0020 | in open sky | inside the cathedral | right place now |
| Yhorm the Giant intro | s39_00_0020 | on a rock in the sky | in the throne hall | right place now |
| Cathedral scene 0050 (Deacons) | s35_00_0050 | on a hillside | at the altar | right place now (*) |
| Catacombs goblet scene (Wolnir) | s38_00_0000 | on a bare cliff | on its altar | right place now |
| Pontiff Sulyvahn scene 0010 | s37_00_0010 | black, then play | black, then play | cannot judge |
| Kiln scene 0000 (Soul of Cinder) | s41_00_0000 | grey fog, embers | same | cannot judge |
| Lothric Castle scene 0000 (Armour) | s30_01_0000 | 2 dark frames | same | cannot judge (not moved) |
| Oceiros intro | s30_00_0040 | garden | same | unchanged and right |
| Nameless King intro | s32_00_0010 | storm, dragon | same | unchanged and right |
| Vordt intro (control) | s30_00_0030 | gate, mist | same | unchanged and right |
| Dancer intro (control) | s30_00_0010 | chapel | same | unchanged and right |
| Sister Friede intro (control) | s45_00_0000 | chapel | same | unchanged and right |

(*) Deacons: the first cut (4 shots) shows only a blurred brown surface on 1.0.739; the later cuts are at the altar.
Names: the list has no "Aldrich" or "Deacons" entry; I took the 48 s Irithyll scene and the 65 s Cathedral scene.
All 26 plays started on the first click; nothing hung. Sheets: `sheet-cs737-A/B.jpg`, `sheet-cs739-A/B.jpg` (PC).

## Health of 1.0.739
- Nameless King: fights (12 "moving" lines). Sister Friede: fights (9).
- High Wall sweep: renders, mean luma 108.8 (1.0.737: 109.0). Firelink: renders, 63.4 (62.9).

## Step 4: Twin Princes' intro (s34_01_0000), attempts used: 1 of 2, no second fix
- New facts. Camera tracks can be sampled now (`LuaProbe ds3cut`, float masks stripped per block). With the built
  offsets the camera is at floor height (146-147) in cut0010, 0030, 0040, 0050 and 0060, and the dummy d0000 sits at
  Lothric's place (c5251_0000, 37.8 / 146.2 / 341.6) in all of those. So the fix places five of six cuts right.
- cut0021 ("the mantle of Lord interests me none") is the odd one: camera and dummies are 10.3 m above the floor
  and 23 m from Lothric's place in DS3's own data, its collision track has no rotation, and both the timeline
  offset (149.27) and the Havok collision track (-149.3) agree. No rule from the data puts it on the floor.
- In play the hall is complete on all sides and overhead (`n737-archsweep-*`), so the sky seen in cut0021 is the
  camera being above or outside the hall, not missing map pieces.
- The intro also ends early in game: gameplay resumes about 40 s in, the listed length is 76 s; cut0040-0060
  were not seen with certainty.
- Ideas not tested: DS3 may hide or replace the hall for that cut through the cast collision's display groups
  (h34_01_00_00_995000 is a cutscene-only collision); Elden Ring's kind 237 "HitPartsSpecified" may be needed.

## Seen on the way
- The F1 Cutscenes list can be driven unattended: `night-1128\Cs13.ps1` (finds the Play buttons by colour, checks
  the DLL log for the cutscene that started, re-aims if another row was hit).
- `Luma.ps1` still overwrites `luma.tsv`; it was saved and put back. Round 4 numbers: `luma-739.tsv` (PC).

## On branch `reports/night-1128`
- `before-after-cutscenes-1.0.739-part1.jpg` (7 pairs) and `-part2.jpg` (6 pairs): red BEFORE = build 1.0.737,
  green AFTER = build 1.0.739, one pair per cutscene, cutscene name in each banner.

## Safety checks at the end (07:10 local)
- eldenring.exe and the launcher closed; no me3, probe or other PowerShell process; no jobs.
- Save, save .bak, GraphicsConfig.xml and regulation.bin match `night-1128\backup\hashes.txt`.
- Nothing was swapped into Mods\ER this round: no file there is newer than the end of the rebuild.
- F7 blocker stopped; releases sent; only F7 (the physical key) reads as down. Mouse still for 3 s (968,561).
