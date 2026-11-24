# Probe 5: TAE swap on c7112 (Gundyr), 2026-11-24

Question: the DS3 kneel pose (30002 / 30003) never shows when a script forces it, while 30000 (a DS3 lying pose with
the same clip format: 2 frames, 0.333 s, 193 tracks) does. The graph state of 30003 is the template's own, untouched,
and c7112.hks runs the same single line for every Event300xx state. What is left is the TAE entry (events) and the
state. This probe splits them.

Setup (Rtest save copy at Gundyr, live Mods\ER of the current build, backups + hash checks as before):

    taeswap Mods\ER\chr\c7112.anibnd.dcx events:30003=30000 blendonly:30008 file:30000=30002

- 30003: same kneel clip, but the events of 30000 (the entry that plays).
- 30008: same kneel clip content (a000_030008 is byte-identical to 030002), no events at all.
- 30000: its own events and state, but it plays the kneel clip a000_030002.
- 30002: unchanged (negative control).

Force sequence on 45000800 with animprobe (sequence argument):
`W40,A3014:0,W8,A30000:1,W8,A30003:1,W8,A30008:1,W8,A30002:1`

Read the result as:
- 30000 kneels: Event30000's state plays the kneel clip, so the clip is fine.
- 30003 kneels: the TAE events are the cause (30003's state is unchanged).
- 30008 kneels: an entry with no events plays, so one of the ported events blocks it.
- 30002 stays standing: the control still fails.
