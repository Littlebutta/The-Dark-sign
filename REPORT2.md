# Overnight test round 2, 28 Nov 2026 (content 1.0.735, 02:04 - 03:31 local)

**Result: every tested enemy fights except the Nameless King. His cause is proven and fixed in the porter
(commit c49a58c on `claude/project-thread-o5ar6f`, not merged). The installed mod is unchanged.**

## F7 key
F7 is **physically held down** (or its switch is stuck). With no script running, a low-level keyboard hook sees
about 30 F7 key-downs per second, none flagged as injected: a device sends them, not an old script. A synthetic
key-up does not clear it (down again within 50 ms). Keyboards present: VID 05AC PID 024F and a Razer device.
Please check the key. For the tests `night-1128\F7Block.ps1` swallowed the physical F7; it is stopped now.

## Part 1a: AI check (F1 spawn at the Cemetery start, 20 s, RT off)
| Enemy | Result | Attack animations seen | Moved |
|---|---|---|---|
| Sister Friede | fights (waits 9 s first) | 3002 3004 3005 3010 | 7.7 m |
| Demon in Pain | fights | 3000 3005 3007 3016-3018 3022 3032 | 9.8 m |
| Gundyr (list has only "Corrupted Gundyr", c7112) | fights | 3001-3014 | 11.3 m |
| Crystal Sage (boss, c7032) | fights, teleports | 3000 3001 | 8.6 m |
| Crystal Sage, Grand Archives (c7033) | fights (ER think row 38100000) | 3010 3012 3015 | 2.9 m |
| Pontiff Sulyvahn | fights | 3000-3013 | 8.7 m |
| Dancer | fights | 3008-3027 | 6.4 m |
| Abyss Watcher | fights | 3005-3014 | 6.5 m |
| Vordt | fights | 3007 3008 3010 3021 | 6.3 m |
| Soul of Cinder | fights | 3000-3023 | 8.3 m |
| **Nameless King** | **stands still** (animation 20, 2 runs) | none | 0 m |
| Lothric Knight, red-eyed (c7027) | fights | 3003 3004 3022 | yes |
| Large Hollow Soldier (c7009) | fights | 3000-3007 | yes |
| Elder Ghru (c7095) | fights | 3000 3005 3007 3009 | yes |
| Corvian Knight (c7139) | fights (waits a few seconds first) | 3002-3026 | yes |

Watch lines: `ai\n735-ai-<name>.txt` (on the PC). The F1 list has no "Iudex Gundyr". Friede fights with her own goal 990057.

## Part 1b: death and respawn at High Wall
Warped to High Wall, god mode off, Gundyr killed the player. The player respawns standing over a round hole in a
dark courtyard facing an arched door (`respawn-2-after.jpg`), not at the bonfire where the warp lands.
**No area banner appears at all** after the respawn: neither `?PlaceName?` nor a name, in 36 shots taken 1.2 s apart from the death to 50 s after it (second run, same respawn spot). So PR #327 could not be confirmed this way.

## Part 1c: Twin Princes intro
`princes-intro-1..6.jpg`: 1-2 show the hall; 3 white outside the windows; 4-5 the camera is on a snowy mountain
with no hall around Lorian; 6 a tilted white plane cuts through the hall. Then the fight runs: health bar, Lorian
teleports (`princes-fight.jpg`). Not diagnosed. The F1 menu stayed open in the shots (would not close, two tries).

## Part 1d: brightness (mean luma 0-255 of 4 shots, share of near-black pixels)
| View | 27 Nov 00:40 (b-) | 27 Nov 03:33 (a-) | 1.0.735 (n735-) | Verdict |
|---|---|---|---|---|
| Firelink | 37.2, 22% black | 37.6, 21% | 63.4, 4% | brighter (+26) |
| Cathedral | 31.8, 12% | 28.8, 17% | 44.8, 4% | brighter (+14) |
| High Wall | 74.2, 8% | 65.0, 13% | 109.4, 3% | brighter; black slabs gone |

No `e-` shots exist in `Work\reports\boot` (a- and b- are the comparison), none of 1.0.733 itself. Per-shot numbers: `luma-compare.tsv` (on the PC).

## Part 2: Nameless King (1 fix attempt, verified)
- **Cause (proven):** DS3's aicommon files `SkeletonKingDefault_Battle.lua` and `SkeletonKingHeatUp_Battle.lua`
  (High Lord Wolnir) still call `REGISTER_GOAL_NO_SUB_GOAL(GOAL_StormyKing501000_Battle)`. `AiPort.PortBattle` took the
  first script that names the goal in any Register call, so goal 501000 (990041) was built from Wolnir's file, which
  gives that goal no table. The real script is `501000_battle.lua` in m32's binder.
- **Fix:** a script "defines" a goal when it calls `RegisterTableGoal` on it or owns its goal functions / luainfo
  entry; the defining script is chosen, an option-only call is the fallback. Systemic: applies to every goal.
- **Scope check:** the AI port run alone (`night-1128\LuaProbe aibuild`) with the unfixed porter gives all 223 script
  binders byte-identical to the installed mod. With the fix only `990041_battle.luabnd` and `aicommon.luabnd`
  change; no goal or think row is renumbered, so regulation.bin is unaffected. No other goal had this fault.
- **In game:** as built, stands still (02:38 and 03:09). With the two fixed binders swapped in (03:07): attacks
  3004-3024, moves over 10 m. Scripts restored afterwards, hashes match.
- **Tests:** new self-test fails without the fix, passes with it; `enemy-selftest` and `map-selftest` pass.
  Ruled out for this fault: Ds3_ rename, aicommon load order, goal renumbering. Not tested: King of the Storm.

## Images on branch `reports/night-1128`
- `before-after-1.0.735.jpg`: the 5 map pairs with large BEFORE (red) / AFTER (green) banners.
- `shots\map-<view>-1-BEFORE.jpg` = `a-<view>-1.png`, 27 Nov 03:3x, the build installed then (before #326-#332).
  `shots\map-<view>-2-AFTER.jpg` = `n735-<view>-1.png`, round 1, build 1.0.735.
- `before-after-nameless-king-fix.jpg`, `shots\fix-nameless-*`: BEFORE = 1.0.735 as built, AFTER = fixed scripts.
  `shots\ai-*`, `respawn-*`, `princes-*`: build 1.0.735, this round.

## Safety checks at the end
- 03:30 local: eldenring.exe closed; no me3, launcher, probe or other PowerShell process left; no jobs.
- Save, save .bak, GraphicsConfig.xml and regulation.bin match `night-1128\backup\hashes.txt` (SHA256). The two
  script binders swapped for one test match their as-built copies in `backup\script`. Build file says 1.0.735.
- Releases sent; only F7 (the physical key) reads as down. Mouse did not move in 3 s (968,561). Blocker stopped.
- Windows dropped the F7 hook once (about 02:40-02:58): three ordinary-enemy runs spawned nothing and were redone.
