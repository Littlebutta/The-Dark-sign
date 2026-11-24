# Probe 18 result (release 1.0.620, 2026-11-24)

## Part A
**A1** (A1-inject.txt): injected into m45_00's emevd: 9000 (ER Examine, category 5) on the sword asset 45001952 and on
Gundyr 45000800, plus 6000 (ER Talk, category 4) on **all 22 ported talkers** in m45_00 (the c0000_9000-9012 Firelink
NPCs, 45000700-45000790 and 45000950-45000952). Each one plays player anim 60750 when it fires.
- **Sword asset and Gundyr: no prompt.** I stood beside him and the sword (p18a1-sword.png); E did nothing
  (p18a1-sword-afterE.png).
- **Firelink** (Ctrl+Shift+N1 warp, standing in front of the shrine door, with one NPC on the stairs to the right about
  10 m away): an **"E Talk" prompt showed** at the warp spot (p18a1-firelink.png). Pressing E made the player move and
  animate, the prompt went away, and **no dialogue box opened** (p18a1-talkE-1.png, p18a1-talkE-3.png). That looks like our
  injected 6000 event firing (player anim 60750) rather than an ESD talk, but I can't be sure from screenshots which
  talker it belonged to.

**A2** (Roundtable Hold vanilla control): **not run**. I have no save or warp that reaches m11_10 under the mod.

## Part B (Gundyr save; saves-gundyr-r5 is the only Gundyr save, so I couldn't test a never-fought one)
**B1** `bypass` (B1-edit.txt; 0.5 s sheet B1-0.5s.png, frames left to right, 10 per row): pull 0-9.5 s, Gundyr
stands up about 9.5-10 s, dust, then **the boss bar shows about 10.5-11 s while he's still standing**. He's **gone by about
11.5 s**, and the bonfire flames up at 12.5-15 s and stays lit.
**B2** `bypass nosp` (45005810 instruction 28, SpEffect 700005800, replaced by a 0 s wait; B2-nosp-0.5s.png):
**the same**. Pull, stand at about 10 s, dust and the bar at about 11-12 s, gone at about 12.5 s, bonfire at about 13.5 s.

So SpEffect 700005800 isn't what ends the fight: he disappears about 1 s after the bar shows, with or without it.
Restored: m45_00 emevd dd26e653...; the save is back.
