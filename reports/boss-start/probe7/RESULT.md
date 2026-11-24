# Probe 7 result (2026-11-24, build 1.0.610)

udfix renumbered 14 selectors in c7112.behbnd and 31 in c7006.behbnd (udfix-c7112.txt, udfix-c7006.txt). In c7112:
Attack1500/1600/3014/3025-3029/6000-6003, and EventLoop_SM Event30001 0x14B0002 -> 0x14B0009 and Event30002 -> 0x14B000A.
Both behbnds were restored afterwards. Hashes match: c7112.behbnd c2c4fda2..., c7006.behbnd c4b16b35..., plus the
c7112.anibnd and emevd as before. The Rtest save is back to the Gundyr copy.

**Run A** (Gundyr, emevd unchanged): **no kneel**. About 30 s and 50 s after load he stands in the normal idle stance
(runA-*.png). I didn't try walking up and pressing E on the sword, because steering to it blind isn't reliable.

**Run B** (animprobe W40,A3014:0,W8,A30001:1,W8,A30003:1,W8,A30002:1, burst of 2,149 frames 26 ms apart, runB-sheet-1s.png).
The forces fall at about burst t = 25.5 / 33.5 / 41.5 / 49.5 s. That's anchored by the 30003 pose onset and by 3014's
short twitch about 3.5 s after its force, which probe 5 saw too.
| force | result |
|---|---|
| 3014 | brief sword twitch about 3.5 s later, then idle (as in probe 5) |
| 30001 | **nothing**, he stays idle |
| 30003 | **upright stiff pose** from about 41.5 s, held about 8 s |
| 30002 | **nothing**: back to idle at about 50 s and stays there |

So renumbering the selectors changed nothing visible: 30001 and 30002 still show nothing, and 30003 still shows the stiff pose.

**Run C** (High Wall, the save that had been left at the High Wall bonfire, c7006 renumbered): no hollow is in view
from the bonfire in four camera directions (runC-highwall-bonfire.png). Inconclusive.
