# Probe 4: Gundyr 30001 (kneel), build 1.0.610, 2026-11-24

Setup: live Mods\ER from the 1.0.610 rebuild, offline, Rtest save copy at Gundyr. Event 45005999 (started from
event 0) waits 40 s, then 2003[18] on 45000800. A fast capture (FastBurst.ps1) grabs the game's client area every
~30 ms (960x540 JPEGs) from 60 s after the load prompt. Earlier runs put the forced anim at ~66 s (probe 3: 3014
showed at 66-73 s). Each *-frame-diff.tsv is the mean pixel difference on Gundyr from the burst's first frame.
A kneel would be a jump far above the 8-20 range seen, which comes from lighting drift.

(A) Files unchanged; 30001 loop 1 forced 8 times, 6 s apart. Burst of 547 frames, 29 ms apart, over 16 s (two or
three forces). **No kneel, not even one frame.** The diff never jumps (max 16 over 250 ms steps), and the sheet
(p5a-sheet) shows the standing idle in every crop.

(B) Only c7112.anibnd TAE entry 30001 IsLoopByDefault set to True. A full TAE dump of entries 0-10, 3000-3030,
7000-7020, 20000-20010 and 30000-30010 before and after differs only in that flag. 30001 loop 1 forced once. Burst
of 467 frames, 30 ms apart, over 14 s, plus a shot every ~3 s from 76 to 118 s (p5b-zoom). **No kneel, stays
standing.** The diff hump at 4.5-7.5 s is the background brightening, not a pose change (p5b-sheet).

(B2) From the original files, only the type 0 JumpTable 132 event (0-2.5 s) deleted from entry 30001. Its
JumpTables went from 51 24 132 28 to 51 24 28. Side effect of the TAE rewrite: that entry's type 224 event params
were padded from 16 to 24 bytes (zeros). 30001 loop 1 forced once. Same burst (473 frames, 30 ms) plus shots
76-118 s. **No kneel, stays standing.**

All files restored from Work\scratch\probe4-backup; hashes match (c7112.anibnd da94704f..., emevd dd26e653...).

(E) see E-jumptables.txt and E-tae-full.txt. They were read from the 1.0.610 build, because the rebuild replaced
1.0.606; TAE porting didn't change between them as far as I know, but that isn't verified. Summary:
- DS3 c1100 (hollow): 700: 28 49 51 | 701: 49 28 51 | 702: 49 28 51 | 3000: 5 23 86 79 78
- ported c7006 (= c1100): 700: 7 49 50 8 | 701: 8 49 50 24 51 113 | 702: 8 49 50 24 51 113 | 3000: 5 23 86 79 78
- c7112 (= c5110): 3012: 5 23 86 79 78 | 30000: 28 49 50 24 51 | 30001/30002/30003: 51 24 132 28
So 132 is only on c7112's 30001-30003. The hollows' ported wait poses don't carry it, and lost DS3's 28.

Caveat: these runs had no positive control (an anim known to play) in the same session. Probe 3's 3014 at ~66 s,
with the same timing and save, is the closest one.
