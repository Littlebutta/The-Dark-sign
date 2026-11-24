# Probe 3: c7112 loop clips moved into div00 (2026-11-24)

Setup (live Mods\ER, offline, Rtest save copy at the Gundyr arena):
- Copies of c7112_div00/div01.anibnd.dcx rewritten: a000_030002.hkx and a000_030003.hkx (binder IDs 1000030002 and
  1000030003, bytes unchanged, 24284 bytes each) moved from div01 into div00 under
  N:\GR\data\INTERROOT_win64\chr\c7112\hkx_div00_compendium\. div00 now 112 files, div01 120.
- m45_00_00_00.emevd.dcx: event 45005999 (started from event 0): wait 40 s, ForceAnimation 45000800 3014 (loop 0),
  wait 8 s, 30003 (loop 1), wait 8 s, 30002 (loop 1). 2003[18] args copied from event 45005810.

Screenshots p3-00..15, every ~3.5 s from 48 s to 102 s after the load prompt (p3-sheet.png whole frame,
p3-zoom.png cropped on Gundyr).

Result:
- p3-00..04 (48-63 s): idle pose.
- p3-05..07 (66-73 s): a different pose (upright, arms out, sword raised): 3014 plays. An added attack forced
  from a script works.
- p3-08..15 (77-102 s): back to the idle pose and stays there. 30003 and then 30002 (both looped) never show,
  even with the clips in div00.

So moving the clips into div00 does not make 30002/30003 play: "div01 never loads" is not the cause. Since 3014 (an
added Attack_SM state) plays while the added EventLoop_SM states don't, the difference is in the event-loop states
or how ForceAnimation reaches them, not in which binder holds the clip.

Restore: both anibnds and the emevd were copied back from Work\scratch\probe3-backup and their SHA-256 match the
originals (div00 6518d021..., div01 ea53bf43..., emevd dd26e653...).
