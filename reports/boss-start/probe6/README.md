# Probe 6 (2026-11-24)

## In game (asked through the coordinator)
`taeswap Mods\ER\chr\c7112.anibnd.dcx events:30002=30000 file:30002=30000 file:30003=30000`, then animprobe
`W40,A3012:0,W8,A30002:1,W11,A30003:1,W13,A30000:1`.

## Offline survey: refsurvey-Program.cs (read-only)
Offline finding from the raw files: the DS3 clips the forced poses use, a000_030002 (= 030003/030008) and a000_020003,
carry **no root motion** (no hkaDefaultAnimatedReferenceFrame), while 3012, 3014 and 30001 do. The port copies root
motion only when the DS3 file has it (AnimPort.ConvertTimed). Question: does Elden Ring ship any animation without it?

Run on:
1. vanilla Elden Ring binders (extract ER): /chr/c3060.anibnd.dcx and its _div files, /chr/c3100.anibnd.dcx (any
   other enemy), and one player binder such as /chr/c0000_a00_hi.anibnd.dcx;
2. the mod's Mods\ER\chr\c7112*.anibnd.dcx and c7006*.anibnd.dcx (the High Wall hollow).
