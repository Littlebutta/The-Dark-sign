# Probe 8 result (2026-11-24, build 1.0.610)

graphprobe (graphprobe-output.txt): Attack3012's a000 clip changed from a000_003012 (internal ID 33, speed 1) to
a000_003014 (ID 213, speed 0.4). The Rtest save was confirmed at Gundyr. Afterwards c7112.behbnd and the emevd were
restored, hashes match (c2c4fda2..., dd26e653...), and the save was put back.

animprobe W40,A3012:0,W10,A3012:0. One launch, a 45 s burst of 1,786 frames 25 ms apart (sheet-1s.png).

- **Force 1** (about burst t = 27 s): a **short sword swing of under a second** (27.3-28.2 s, force1-26-29s.png),
  then idle. It is **not** probe 6's 5 s swing-and-lunge, and **not** a slow ~8 s motion either. It looks the same as
  the brief twitch 3014 gave in probes 5 and 7.
- **Force 2** (10 s later): no visible change (force2-36-39s.png and the 1 s sheet up to 44 s).

So the game does read our c9997.hkx: 3012 now shows 3014's motion, not its own lunge. But 3014's clip, played through
that state, gives only a sub-second movement, not its 3.13 s clip, let alone 0.4x speed. The slow speed didn't show,
and the second force did nothing.
