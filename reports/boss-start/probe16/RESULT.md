# Probe 16 (release 1.0.620, 2026-11-24): the sword event runs; only the prompt fails

**Run A** (runA-edit.txt): event 45005811 instruction 10, 3[24] FF000000 87266904 60A8AE02 (button 74000007 on 45000800),
replaced by 3[00] FF000000 62A8AE02 (group -1, flag 45000802 OFF). I then pressed the F1 Fight sequence and captured
25 s. **Nothing happened**: the player stayed wedged by the entrance wall and there was no pull (runA-25s.png,
runA-look.png). I can't confirm the F1 Fight click landed; the player never moved.

**Run B** (runB-edit.txt): the same edit, plus at the very start of 45005811 a 2003[18] on the player (10000, anim
60750) and a 2 s wait. Plain load at Gundyr, no F1, a 30 s capture starting about 8 s after the load prompt
(runB-30s.png, one frame a second):
- About 6 s: the player is short-warped next to the kneeling Gundyr and **the pull plays**: player at the sword,
  Gundyr reacting, red/black effects, 6-12 s (runB-pull-9s.jpg).
- About 13-16 s: **Gundyr stands up** beside the player (runB-standing-13.7s.jpg).
- About 17-19 s: dust and smoke, then the **boss bar "Iudex Gundyr" shows** (runB-bossbar-18.6s.jpg). It is empty,
  and he's gone.
- About 20-22 s: a bonfire kindles in flames where he knelt, and it stays lit to 29 s.

So once the condition is the flag rather than the action button, the event runs and the whole pull works: player
anim, Gundyr's 20003, stand-up, boss bar. **Only the "Remove sword" prompt / action-button check fails.** The fight then
ends at once: his bar shows empty and the coiled-sword bonfire appears. This test save (saves-gundyr-r5) may already
count him as defeated, or he spawns with 0 HP. That needs checking.

Restored: m45_00_00_00.emevd (dd26e653...); the save is back. Run C (test prompt on a nearby asset) not done yet.
