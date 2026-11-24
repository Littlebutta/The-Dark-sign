# High Wall (DS3 m30_00_00_00): animations the map script forces (2026-11-24, read-only)

high-wall-forced-anims.tsv lists every ForceAnimation (2003[18]) in m30_00_00_00.emevd, direct or through a
common_func event whose ForceAnimation takes entity/anim from its parameters (20005290, 20005210/13/20/21/24,
20005200/02, 20005411/15/17 ...), with the part and model from m30_00_00_00.msb, the script's loop flag, the c-model's
TAE entry (IsLoopByDefault) and the clip's length and frames (DS3 files). "?" = entity not an enemy in that MSB.

Hollow wait poses (kneel, pray, sit, lean; c1100 unless noted), all forced with loop 1:
| anim | model | TAE | IsLoopByDefault | clip | frames |
|---|---|---|---|---|---|
| 700 | c1100 | Standard | False | 6.667 s | 201 |
| 701 | c1100 / c1200 | Standard | False | 4.667 s / 2 s | 141 / 61 |
| 702 | c1100 / c1190 | Standard | False | 6.333 s / 3.333 s | 191 / 101 |
| 703, 705, 706 | c1100 | Standard | False | 6.667 s | 201 |
| 710 | c1100 | Standard | False | 2.333 s | 71 |
Wake-ups forced with loop 0: 1700-1706 (1.4-1.8 s), 1710 (3.8 s).

So in DS3 every forced wait pose has IsLoopByDefault False and relies on the script's loop flag, but each is a
real multi-second clip (61-201 frames). None is a 2-frame pose like Gundyr's 30000/30002 (0.333 s, 2 frames) or
30001 (imports a001_010001, 0.033 s, 2 frames).
