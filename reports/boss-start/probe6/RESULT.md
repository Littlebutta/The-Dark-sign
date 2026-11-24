# Probe 6 result (2026-11-24, build 1.0.610)

Save: the Rtest copy was confirmed identical to saves-gundyr-r5 (Gundyr arena) before the launch. Files were restored
afterwards and the hashes match (anibnd da94704f..., emevd dd26e653...).

TAE edit (taeswap-output.txt): 30002 now has 30000's 11 events and plays a000_030000.hkt. 30003 keeps its own 6 events and
plays a000_030000.hkt.

Sequence W40, A3012:0, W8, A30002:1, W11, A30003:1, W13, A30000:1. One launch, a 70 s burst of 2,755 frames 25 ms apart
(sheet-1s.png, frame-diff.tsv). The forces fall at burst t = 26, 34, 45 and 58 s. That timing is anchored by the
control at 26 s and by the known 30000 collapse at 58 s, which is exactly 32 s later as the sequence says.

| force | result |
|---|---|
| 3012 (template attack, control) | **attack**: at 26 s he swings and lunges to the right, about 5 s, and ends standing in a new spot. The control works. |
| 30002 (30000's events and file) | **nothing**: he keeps standing side-on through 34-37 s (around-30002.png). |
| 30003 (own events, file a000_030000) | **upright stiff pose**: at about 44.5 s he switches to an upright pose facing the camera and holds it to 57 s (around-30003.png). |
| 30000 (unchanged) | **collapse with tendrils**: instant collapse with teal tendrils at 58 s, lying 59-62 s, getting up 63-66 s. Same as probe 5. |

So 30002, with exactly 30000's TAE events and clip, does nothing, while 30000 collapses. What happens follows the
state the anim ID lands in, not the TAE entry's events or file.

## refsurvey (refsurvey.txt)
Clips without root motion are normal in vanilla Elden Ring, so lacking it doesn't by itself stop a clip playing:
- c3060_div00 35 of 79, c3060_div01 57 of 111, c3100 66 of 124, and c0000_a00_hi 604 of 1,189 have no
  hkaDefaultAnimatedReferenceFrame.
- Mod: c7112_div00 35 of 107 and c7112_div01 81 of 119 without; c7006_div00 70 of 202 and c7006_div01 63 of 128 without.
- c3060.anibnd, c7112.anibnd and c7006.anibnd hold no clips, which is why they show 0/0.
c3100 is extracted as c3100.anibnd only; it has no _div files.
