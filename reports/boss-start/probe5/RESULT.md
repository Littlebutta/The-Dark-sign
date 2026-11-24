# Probe 5 result (2026-11-24, build 1.0.610)

TAE edit (taeswap, output in taeswap-output.txt): `events:30003=30000 blendonly:30008 file:30000=30002`.
- 30003 got 30000's 11 events.
- 30008 kept only its type 16 events, which here means 0 events.
- 30000 now plays a000_030002.hkt.

The edited anibnd and the emevd were restored after each run, and the hashes match (anibnd da94704f..., emevd dd26e653...).

Save: the shared Rtest copy (saves-overnight) had been left at the High Wall by my cutscene test, so the first attempt
loaded there and was discarded. I restored it from saves-gundyr-r5, and the state it was in is kept in
Work\scratch\saves-overnight-highwall-0228. Both runs below start at the Gundyr arena.

**Run 1**: forces 8 s apart, sequence W40,A3014:0,W8,A30000:1,W8,A30003:1,W8,A30008:1,W8,A30002:1 as asked. Burst of
1,768 frames over 50 s, 28 ms apart (run1-sheet-1s.png, run1-frame-diff.tsv).
**Run 2**: same files, uneven gaps (A3014, W5, A30000, W11, A30003, W7, A30008, W13, A30002), so each change can be tied to
one force without a clock. Burst of 2,910 frames over 75 s, 26 ms apart (run2-*.png, run2-frame-diff.tsv).

The two runs agree when the forces are placed at run 1 burst t = -7.5, 0.5, 8.5, 16.5, 24.5 and run 2 t = 14, 19, 30, 37, 50:

| force | what Gundyr does |
|---|---|
| 3014 (control) | **No clear attack.** Run 2 shows only a short sword twitch about 3 s later (17.0-17.3 s), which may not be related. Not a usable positive control here. |
| 30000 (30000's events, file a000_030002) | **Something else:** an instant collapse with teal tendril effects, then lying flat, then getting up over about 7 s (run 2 19.1-27 s). |
| 30003 (30000's events, own file, bytes equal to 030002) | **Stands** in an upright, arms-out pose and holds it about 7 s (run 2 about 29.5-36.7 s). It's the same pose probe 3 saw at 66-73 s. |
| 30008 (no events) | **Something else:** the same instant collapse with teal tendrils, lying, getting up (run 2 37.0-44 s), then back to idle. |
| 30002 (unchanged) | **Stays in idle**, no visible change (run 2 from 50 s, run 1 from 24.5 s). |

**No kneel in any of the five.** The collapse comes from both 30000 (with 30000's events) and 30008 (with no events), so
it doesn't come from the TAE events. The upright arms-out hold appears on the entry that plays the 030003/030002 bytes
under its own name.
