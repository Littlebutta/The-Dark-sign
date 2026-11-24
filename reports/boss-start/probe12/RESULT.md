# Probe 12 result (2026-11-24, build 1.0.610): the game takes Gundyr's clips from c3060's div binder

plant (plant-output.txt): a NEW Mods\ER\chr\c3060_div00.anibnd.dcx was written, vanilla c3060_div00 (82 files) with
its a000_003012.hkx (51,732 bytes) holding our DS3 a000_003014.hkx (57,548). No c7112 file was touched. The save was
confirmed at Gundyr. Afterwards the new file was deleted, the emevd restored, hashes match (c7112_div00 6518d021...,
c7112.anibnd da94704f..., emevd dd26e653...), and the save was put back.

**me3 log** (me3-log-c3060-c7112.txt, 2026-11-24_03-43-04.log): the game opened our c3060_div00 override, in the
same burst as Gundyr's files:
- 1700-1702 c7112.anibnd
- 1775-1777 c7112_div00.anibnd
- 1778-1780 c7112_div01.anibnd
- **1787-1789 c3060_div00.anibnd** (override)
- 1805-1807 c7112.behbnd

**Game** (animprobe W40,A3012:0,W10,A3012:0; a 45 s burst of 1,826 frames 25 ms apart; sheet-1s.png):
- **Force 1** (about burst 14 s): **a different, shorter move**. He rises and leaps forward, landing at about 17-18 s,
  about 3-4 s in all, and ends to the **left**. It is not the 5 s swing and lunge to the right of probes 6, 10 and 11.
- **Force 2** (about 24 s): the same leaping move again (24-27 s), ending further left.

So 3012 now plays our DS3 3014 clip: the game reads the clip for this Gundyr state from **c3060_div00** (the
template's div binder), not from c7112_div00.
