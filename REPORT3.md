# Overnight test round 3, 28 Nov 2026 (content 1.0.737, 03:35 - 05:15 local)

**Result: 1.0.737 is installed and the Nameless King fights as built. The respawn "fault" is not a fault. Cutscenes on
most maps played in the wrong place; the cause is proven and fixed in the porter (commit 6d6cc77 on
`claude/project-thread-o5ar6f`, not merged). The Twin Princes' intro is only partly fixed: handoff below.**

## Step 1: build 1.0.737
- Launcher self-updated 1.0.735 -> 1.0.737 at 03:45 (tag `launcher-v1.0.737` = 6063525).
- Rebuild 03:46:05 - 04:42:53: port-weapons 11:49, port-maps 56:48. No failed step; validation 0 FAIL, 53 WARN.
- `darksign_build.txt` = 1.0.737. regulation.bin hash unchanged (A9F4CF66...). `990041_battle` now holds DS3's
  StormyKing501000 script (33 KB, was Wolnir's 10 KB).

| Check on 1.0.737 as built | Result |
|---|---|
| Nameless King (F1 spawn, 20 s) | **fights**: attacks 3008-3023, moves, 12 "moving" lines |
| Sister Friede | fights (3002-3036) |
| Corrupted Gundyr | fights (3000-3029) |
| Lothric Knight, red-eyed | fights (3003-3005) |
| High Wall sweep | renders, mean luma 109.0 (1.0.735: 109.4) |
| Firelink sweep | renders, mean luma 62.9 (1.0.735: 63.4) |

## Step 2a: respawn at High Wall (no fault, no fix)
- Round 2 misread the picture. The F8 position recorder gives (-0.5, 1.2, -5.1) after the F1 warp and
  (-5.4, 1.4, -6.9) after the respawn: 5 m apart, same height. The map has the warp point (45010990) 3.9 m from
  the High Wall bonfire's standing point (45010980), and the respawn event of PR #283 (45019900-05) picks 980.
- The player respawns **at the High Wall bonfire, facing it**; the camera behind looks at the tower door, which is
  the "dark courtyard". The "round hole" is at the bonfire's spot under the player.
- The old "respawns at Chapel of Anticipation" note is closed by #283: confirmed here.
- Area banner: it **does show** on arrival ("High Wall of Lothric", `highwall-arrival-banner.jpg`; "Farron Keep" and
  "Smouldering Lake" seen too), so #327 works. It does not show again on a respawn in the same area.
- Not tested: resting first. At High Wall it cannot tell the cases apart (the same bonfire either way).

## Step 2b: cutscenes in the wrong place (1 fix attempt; verified on one cutscene, partly on the Princes)
- **Cause (proven):** each cut plays around its own origin; its "offset.sib" timeline event (kind 265) says where
  that origin is. DS3 measures the offset from a collision the cut casts. Most DS3 maps place every collision with
  the map's own place in DS3's world (Catacombs 842.8 / -340.4 / -1406.2, turned 75.4 degrees; Farron, Irithyll,
  the Dungeon, Cathedral, Undead Settlement, Kiln likewise; the Princes' hall -17.5 / -3.6 / -6.3). The port drops
  that collision from the cast (Elden Ring has no such part) and kept the offset unchanged.
- **Scope:** 129 of 216 cuts, in 42 of the 72 ported cutscenes, were misplaced. High Wall and Painted World maps
  sit at the origin, which is why Vordt's, the Dancer's and Friede's intros were right.
- **Fix (6d6cc77):** new `CutsceneAnchor`: each cut's offset is moved and turned by the place of the first placed
  collision its Havok file animates. Systemic, for every cutscene. New self-test; both self-tests pass.
- **Checks:** (1) DS3 data: with the collision's place, the Abyss Watchers' intro origin is at height -248.2; the
  Watchers stand at -248.3 (without it: -11.6). (2) In game, swapping the as-built file for one with the same
  function applied (`LuaProbe csfix`): Catacombs goblet scene s38_00_0000, as built on a bare cliff, fixed on its
  altar in the chamber (`before-after-cutscene-place-fix.jpg`). Files restored afterwards.
- **Twin Princes (s34_01_0000), still open:** with the fix the first cut shows the bed alcove
  (`before-after-princes-first-cut.jpg`); the later cuts show hall wall and a white surface (probably the bed,
  not confirmed). The cuts with Lothric's 2nd and 3rd lines
  (cut0021, cut0030) still look up at Lorian from under the hall (`princes-3-still-open.jpg`).
  Facts for whoever continues: in those two cuts DS3 puts c5250_0000 13.4 and 15.8 m below the cut origin; the
  timeline offset heights are 149.27 / 149.27 / 149.27 / 149.27 / 149.27 / 135.06 while the Havok files animate
  the collision at -135.1 / -149.3 / -149.3 / -149.3 / -149.3 / -135.1 (cut0010 disagrees by 14.2 m). Neither
  rule alone puts every cut on the floor (146.2). The camera tracks could not be sampled (float track). Next
  step: sample the camera, or try the Havok collision track as the offset for this cutscene only as a probe.
- Ruled out: CutsceneMapIdParam (RequireMapId 0), a neighbour map loading, draw-alpha events hiding the hall,
  Elden Ring using map coordinates (its own cutscenes use the same per-cut offset; rotation sign checked on
  s10_00_0010).
- Not tested in game: the other 41 moved cutscenes. The Abyss Watchers' boss warp plays no intro.

## New things seen
- The F1 boss warp for the Abyss Watchers starts the fight without the intro.
- F1 Cutscenes tab works for playing a single cutscene (72 listed).
- `Luma.ps1` overwrites `luma.tsv`; round 1's table was restored from the branch copy, round 3 numbers are in
  `luma-737.tsv` on the PC.

## Images added to branch `reports/night-1128`
- `before-after-nameless-king-1.0.737.jpg`: BEFORE build 1.0.735 as built, AFTER build 1.0.737 as built.
- `before-after-cutscene-place-fix.jpg`: BEFORE build 1.0.737 as built, AFTER 1.0.737 with the fixed file.
- `before-after-princes-first-cut.jpg`: BEFORE build 1.0.735, AFTER 1.0.737 with the fixed file.
- `shots3\`: the single frames, plus the High Wall warp landing, respawn and arrival banner (build 1.0.735).

## Safety checks at the end (05:13 local)
- eldenring.exe and the launcher closed; no me3, probe or other PowerShell process; no jobs.
- Save, save .bak and GraphicsConfig.xml match `night-1128\backup\hashes.txt`. regulation.bin matches too (the
  rebuild did not change it). The three cutscene files swapped for tests match their as-built 1.0.737 copies in
  `backup\cutscene`.
- F7 blocker stopped; releases sent; only F7 (the physical key) reads as down. Mouse still for 3 s (968,561).
