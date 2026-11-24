# Probe 21 (release 1.0.626, which includes ad1d9a9 / PR #282), 2026-11-24

Full launcher rebuild on 1.0.626. The m45_00 emevd hash is unchanged (dd26e653). Same test as probe 20: `p18 warp` (4 s,
player to dummy 100), plain load at Gundyr, god mode on (Ctrl+Shift+F5) about 20 s after the load prompt, a
screenshot at about 26 s, E, then a 40 s burst (after-E-1s.png, one frame a second).

**Result: no prompt and no pull this time.** The player stands beside Gundyr's back, about where probe 20 had the
prompt, facing away from the hilt (prompt-and-end.png). No "Remove sword" text shows. After E nothing changes for 40 s:
Gundyr stays crouched and dark, there's no boss bar, and the player doesn't move.

Differences from probe 20: the 1.0.626 build, god mode toggled before E, and the player's facing after the warp
(here he faces away from the hilt). I couldn't tell which of these hid the prompt in this run. A hand check (walk to
the hilt, face it, E) would settle it. Restored: emevd dd26e653...; the save is back.
