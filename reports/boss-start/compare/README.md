# c7112 (Gundyr) vs c3061 and DS3 c5110: TAE, clips, 30001 probe (2026-11-24)

Files: tae-c7112-c3061-c5110.txt (every event of 3012, 3014, 30000-30003 and the a001 twins, type 0 JumpTable and
231 marked), clips.txt (duration, frames, class, type, blend hint), p4-00..09 + p4-zoom.png (30001 probe),
cmpdump-Program.cs (the reader).

## TAE
- c7112: all of 3012, 3014, 30000, 30001, 30002, 30003 are Standard entries with their own file
  (ImportsHKX False, IsLoopByDefault False).
- DS3 c5110: 30000 is IsLoopByDefault **True** (ported c7112 30000 lost that); **30001 is ImportOtherAnim from
  1010001** (a001_010001, the 2-frame kneeling pose) with 0 events; c7112 30001 is a Standard entry with 8 events.
  DS3 has **no 30003** at all.
- 30002 JumpTables: c7112 keeps 51, 24, 132 (0-25 s) and 28 (0-0.442 s); DS3 had 7 (0-13.3 s), 51, 24, 49 (0-8.2 s).
  DS3's 7 and 49 are gone, 132 and 28 are new (from the template). No type 231 event in any of these entries in
  either game (see the file for every event).
- c3061.anibnd (vanilla ER, extracted) holds only its TAE and has none of these IDs; its clips live in c9997, which
  isn't extracted here.

## Clips (clips.txt)
| clip | c7112 (ER) | DS3 c5110 |
|---|---|---|
| a000_003012 | 2.033 s, 62 frames, spline, NORMAL, div00 | same |
| a000_003014 | 3.133 s, 95 frames, NORMAL, div00 | same |
| a000_030000 | 0.333 s, **2 frames**, NORMAL, div01 | same (static pose) |
| a000_030001 | 0.033 s, 2 frames, 260 tracks, div01 | none (entry imports a001_010001, 0.033 s, 2 frames) |
| a000_030002 | 0.333 s, **2 frames**, NORMAL, div01 | same (static pose) |
| a000_030003 | byte-identical to 030002 (SHA 99BE6BF6...) | doesn't exist |

All NORMAL blend, hkaSplineCompressedAnimation, none additive.

Important for reading the probes: 30000, 30002 and 30003 are **2-frame static poses**, not motions. Looping one
holds a single pose; if that pose is close to the idle stance, "doesn't play" can't be told from "plays" in a
screenshot. 30001 is the one with a clearly different pose (kneeling).

## 30001 probe (optional item)
Forced 2003[18] 45000800 anim 30001 loop 1, 40 s after load (event 45005999 from event 0). p4-00..09, 48-90 s:
Gundyr stays in the standing idle stance in every frame; no kneel. **30001 does not play.** The emevd is restored;
both anibnds and the emevd hash-match the originals (div00 6518d021..., div01 ea53bf43..., emevd dd26e653...).

## HKS (item 3): not done
c7112.hks is compiled Havok Script bytecode (header 1B 4C 75 61 51 0E, 35,007 bytes) and there is no HKS decompiler
on this PC. The usual community tool is DSLuaDecompiler (katalash; HKS support used for DS3/Sekiro/ER); fetching it
needs Devin's OK. The per-function string list from earlier (c7112-hks-event-strings.txt) shows Event30002_onUpdate
and the other event states call EventCommonFunction and reference W_Idle.
