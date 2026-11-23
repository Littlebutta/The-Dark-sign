# Gundyr fight start: read-only data (2026-11-23)

Built from a scratch build of `main` (58190ec) plus `port/throws`, written on Devin's PC into
`TheDarkSign\Work\scratch\dsmaproot`. No game was launched to make these files.

Files:
- `ds3-m40_00_00_00.txt`, `ds3-common_func.txt`: DS3's own event scripts, with DS3 instruction names (DarkScript3 ds3-common.emedf).
  Event 14005810 is at line 916, event 14005811 at line 951.
- `ported-m45_00_00_00.txt`, `ported-common_func.txt`, `ported-common.txt`: the converted scripts, with Elden Ring instruction names.
  45005811 is started at line 143; the event itself is at line 883.

## d1: ActionButtonParam, DS3 row 8900 ("Pull out a sword from the Segunda grave")

DS3 8900: regionType 0, dummyPoly1 100, dummyPoly2 -1, Radius 1.5, Angle 180, depth 0, width 0, height 2,
baseHeightOffset -0.25, angleCheckType 0, AllowAngle 180, textBoxType 0, textId 10010195, InvalidFlag -1,
grayoutFlag -1, priority 3, execInvalidTime 3, execButtonCircle 1.

Elden Ring copy: row **74000007** "DS3 action button row 8900 (Pull out a sword from the Segunda grave)":
regionType 0, category 4, dummyPoly1 100, dummyPoly2 -1, radius 1.5, angle 180, depth 0, width 0, height 2,
baseHeightOffset -0.25, angleCheckType 0, allowAngle 180, spotDummyPoly -1, textBoxType 0, isInvalidForRide 0,
isGrayoutForRide 0, isInvalidForCrouching 1, isGrayoutForCrouching 0, textId 74000003, invalidFlag 0,
grayoutFlag 0, overrideActionButtonIdForRide -1, execInvalidTime 3.

## d2: c7112 (ported Iudex Gundyr, DS3 c5110), animations 20002, 20003, 30002

All three are in the built files: TAE entries in `chr\c7112.anibnd.dcx` (c7112.tae), clips in `c7112_div01.anibnd.dcx`
(binder IDs 1000020002, 1000020003, 1000030002). The type-231 events match DS3's c5110 exactly:

| anim | built c7112 | DS3 c5110 |
| --- | --- | --- |
| 20002 | 20 events, types 0 16 67 96 129 224 231 791; **231 at 3.667-4.633, message 40** | 24 events, types 0 16 67 96 131 224 231 791; 231 at 3.667-4.633, message 40 |
| 20003 | 14 events, types 0 16 67 96 112 231 233 791; **231 at 6.467-6.567, message 20** | 10 events, types 0 16 67 96 231 233 791; 231 at 6.467-6.567, message 20 |
| 30002 | 6 events, types 0 224 791 (no 231) | 6 events, types 0 791 (no 231) |

Not checked: whether c7112's behaviour graph (c7112.behbnd, c9997.hkx) has a state for each of these IDs. The
enemy port adds states for map-script moves 20000-29999 and loops 30000-39999 (ChrMoves families), but this
build's graph was not opened.
