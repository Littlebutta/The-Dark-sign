# Walk-up test, release 1.0.614 (normal launcher build, 2026-11-24)

**First run** (first-run-closer.png): I walked right up beside him without pressing E. He had been kneeling; once I
was within a step or two he was **standing** next to the player, not attacking. There was no boss bar, and no
interaction prompt appeared.

**Second run** (Gundyr save):
- **Waiting about 21 s** about 8 m away, no input (wait-20s.png): he **stays kneeling** with the sword planted. He
  doesn't rise on his own.
- Then I stepped up beside him (before-E.png): still kneeling, **no prompt shown**.
- Pressed **E**, then a 15 s burst at 36 ms (after-E-15s.png): **nothing happens**. No pull animation on the player,
  he stays kneeling the whole 15 s, and no boss bar appears.

So the kneel now works, but the sword's action (DS3 action button 10010?) doesn't give the player a prompt here. In the
first run he stood when the player got very close (or bumped him) without anything being pressed.
