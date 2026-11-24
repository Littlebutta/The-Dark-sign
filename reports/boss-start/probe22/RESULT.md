# Probe 22 (release 1.0.626), 2026-11-24: **Gundyr's fight starts properly**

**1. The probe 21 save:** every run, probes 20-22 included, copies saves-gundyr-r5 (ER0000.dksign + .bak, unchanged since
2026-11-23 21:10) over the Rtest copy before launching. SHA-256 245be28ad674ad41... for both, so probe 21 loaded the
same character as probe 20. Probe 20's pull was saved only into the Rtest copy, which is overwritten before each run.
So probe 21's no-prompt state wasn't from a stale save.

**2-3. This run:** m45_00 emevd = `p18 warp 4` plus, at the start of event 0, 2003[66] OFF for flags 45000802 and 45000800
(edit.txt). God mode off.
- Load (load-prompt-2s.png): the sword is planted beside kneeling Gundyr, and **"E: Remove sword" shows** right after
  the warp, before I turned or moved.
- E, then a 40 s burst (after-E-1s.png, one frame a second):
  - 0-10 s: **pull** (player at the hilt, red effects)
  - 11 s: dust, **Gundyr stands up**
  - from 11 s: **the boss bar "Iudex Gundyr" is FULL** (boss-bar-full-12.4s.jpg)
  - 13 s on: **he attacks**: a red slash hits the player, and he keeps fighting in the arena through 23 s
  - 25 s: **"YOU DIED"** (god mode off, no input), then the respawn
- The respawn landed at Elden Ring's **Chapel of Anticipation** (place-name banner at 38 s), not a DS3 bonfire. That's a
  separate issue for the death/respawn point.

Restored: emevd dd26e653...; the save is back.
