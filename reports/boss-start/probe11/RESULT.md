# Probe 11 result (2026-11-24, build 1.0.610)

`taeid swap c7112_div00.anibnd.dcx 3012 3014` (swap-output.txt): a000_003012.hkx (50,364 bytes) now holds
a000_003014.hkx's bytes (57,548), and nothing else changed. The save was confirmed at Gundyr. Afterwards c7112_div00
and the emevd were restored, hashes match (6518d021..., dd26e653...), and the save was put back.

animprobe W40,A3012:0,W10,A3012:0. One launch, a 45 s burst of 1,788 frames 25 ms apart (sheet-1s.png).

- **Force 1** (about burst 17 s): **the same 5 s swing and lunge** as probes 6 and 10 (17-22 s, ending in a new spot).
  It is not a ~3 s raised-sword move.
- **Force 2** (about 27 s): this time it played too, another ~5 s swing and lunge (27-32 s), back toward the middle.

So replacing the bytes of our div00 a000_003012 changed nothing: the clip data that plays for 3012 is not ours.
