# Probe 14: the missing "Remove sword" prompt (release 1.0.614, 2026-11-24)

**0) Dummy poly 100** (dummy-poly-100.txt): present in both models with identical data. Built c7112.chrbnd and DS3
c5110.chrbnd: 113 dummies and 299 bones each. Ref 100: parent Dummy_Static (bone 251), no attach bone, position
(0.271, 0, -1.582), forward (0, 0, 0.379), up (0, 0.144, 0).

Current ActionButtonParam 74000007 (runA-param.txt): regionType 0, category 4, dummyPoly1 100, radius 1.5,
height 2, baseHeightOffset -0.25, textId 74000003, execInvalidTime 3.

**Run A** (74000007 set to dummyPoly1 -1, radius 4, height 4, baseHeightOffset -1): walked up beside Gundyr, facing
him. **No prompt** appeared. Pressing E did nothing: no pull, and he stays kneeling (runA.png).

**Run B** (regulation restored; event 45005811 instruction 10, 3[24], args FF000000 87266904 60A8AE02, i.e. group -1,
button 74000007, entity 45000800, changed to button 6000): **no prompt**, and E did nothing (runB.png).

**Run C** (original files plus test event 45005998 from event 0: 3[24] in group 0 on button 74000007 / entity
45000800, then force 3012 twice): **inconclusive**. This walk-up ended about 6 m short of him (runC-burst.png, first
frame), the E press didn't land next to him, and nothing happened. While it ran, a Windows Snipping Tool overlay came
up and dimmed the screen. SnippingTool.exe was already running on the PC and I left it alone.

Restored: regulation 26412e47..., emevd dd26e653...; the save is back at Gundyr.
