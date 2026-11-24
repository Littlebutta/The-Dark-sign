# Probe 10 result (2026-11-24, build 1.0.610)

**(1) survey** (survey.txt, 1,015 lines): c7112.tae and c7006.tae both carry ID 203060 and c3060.sib, flagged FOREIGN.
c0000's TAEs carry 2000+section and c9990.SIB, also flagged FOREIGN.

**(2) vanilla c3060 clips** (clips-vanilla-c3060.txt): a000_003012 is **5.1 s** (101 tracks), a000_020003 is 7 s, and
a000_030003 is 1.333 s. c3060 has no a000_003014, a000_030000 or a000_030002. The 5.1 s matches the 5 s swing and
lunge seen in probe 6, while our DS3 3012 is 2.03 s.

**(3) set** (set-output.txt): c7112.tae ID 203060 / c3060.sib became 207112 / c7112.sib (245 entries, nothing else changed).

**(4-5) Game** (save confirmed at Gundyr; animprobe W40,A3012:0,W10,A30002:1,W10,A3014:0; a 50 s burst of 1,936 frames
26 ms apart, sheet-1s.png). Gundyr loaded normally, no T-pose.
| force | result |
|---|---|
| 3012 (about burst 19 s) | **the same 5 s swing and lunge** as probe 6 (19-24 s, ending in a new spot), not a short 2 s DS3 swing |
| 30002 (about 29 s) | **nothing**: he keeps standing |
| 3014 (about 39 s) | **nothing visible** to 49 s |

So a TAE ID and sib of its own changed nothing visible. Afterwards c7112.anibnd and the emevd were restored, the hashes
match (da94704f..., dd26e653...), and the save was put back.
