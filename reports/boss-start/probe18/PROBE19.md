# Probe 19: why 3[24] shows on a friendly talker but not on Gundyr (2026-11-24)

Probe 18 A1: 6000 on the Firelink talkers showed "E Talk" and fired; 9000/74000007/6000 never show on Gundyr, even
with radius 4 (probe 14 A). Gundyr differs from a talker in four ways his scripts set or his row has: hitmask bit 0
off (45005810 instruction 11), invincible (instruction 9), AI off (instruction 7), and a hostile team.
Same tool (p18-Program.cs, new `drop` mode), same restore rules. Plain load at Gundyr each time, walk up beside him,
screenshot, press E. 45005811 is untouched (its 3[24] 74000007 on Gundyr is the test).

1. `p18 drop <m45_00 emevd> <out> 45005810:11` (hitmask stays on). Prompt?
2. `p18 drop <m45_00 emevd> <out> 45005810:11 45005810:9` (also not invincible). Prompt?
3. `p18 drop <m45_00 emevd> <out> 45005810:11 45005810:9 45005810:7` (also AI on; turn on F1 "enemies ignore player",
   Ctrl+Shift+F9, first). Prompt?
4. Hostile control: `p18 near <mod m45_00 msb> 142.87 -68.89 594.63 40`, pick the nearest Enemy with TalkID 0 that is
   not Gundyr (a hollow), then `p18 inject <emevd> <out> 45005990 6000:<it> 9000:<it>`, F1 "enemies ignore player" on,
   walk up to it. Prompt?
Stop at the first run that shows the prompt and say which.
