# Probe 13 result (2026-11-24, build 1.0.610): **Gundyr kneels** once his NpcParam stops pointing at c3060

**survey** (survey.txt): 585 ported NpcParam rows. Only 10 use normalChangeAnimChrId (3 models), but **221 carry a
RetargetReferenceChrId** copied from their template: 3060 x82 (Gundyr c7112, the High Wall hollows c7006 ...), 4350
x33, 4310 x23, 3450 x19, 3000 x18, 3660 x17 ... Most also carry normalChangeTexChrId = the same template.

**set** (set-output.txt): Gundyr's rows 71120000 and 71120001 had RetargetReferenceChrId 3060 -> -1 and
normalChangeTexChrId 3060 -> -1 (the other three fields were already -1). Read back OK. To avoid a download, the tool
used the porter's own Paramdex cache (Work\cache) instead of %TEMP%.

**Game** (save at Gundyr; animprobe W40,A3012:0,W10,A30002:1,W10,A3014:0; a 50 s burst of 2,032 frames 25 ms apart):
- **On load he kneels**, sword planted (pF-ref.png, gundyr-kneel-before-after.png). This is the map script's own
  30002 force (event 45005810), which never showed before. He holds the kneel through burst 0-11 s (sheet-1s.png).
- About 12-16 s: he rises and swings (the forced 3012; a DS3-looking swing, not the template's 5 s lunge).
- About 17 s: a dark burst, then a huge tendril form fills the view up to 49 s. It looks like his DS3 phase-two
  transformation, very close to the camera. I can't attribute each later force reliably, because the transformation
  covers the frame.

Afterwards regulation.bin and the emevd were restored, hashes match (regulation 14d0baa8..., emevd dd26e653...), and
the save was put back.

Conclusion: the template's RetargetReferenceChrId (and the texture swap) made the game play c3060's animations for
c7112. Clearing them lets Gundyr's own DS3 clips play, kneel included. Fixing it systemically means the enemy port
writing -1 there for every ported model (or its own ID): 221 rows.
