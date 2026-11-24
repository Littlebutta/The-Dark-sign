# Probe 7: selector numbers of the states the port added (2026-11-24)

## Finding (offline, raw files of 1.0.610)
Elden Ring gives every state's CustomManualSelectorGenerator an m_userData of (state machine number << 16) | the
state's index in that machine's m_states. Vanilla c3061's c9997.hkx: 866 of 866 selectors follow it, no exception.
c7112 (Gundyr): exactly the 14 states the port added break it, because ChrMoves.AddStates copies the donor's
selector with its number:
- EventLoop_SM: Event30001 and Event30002 both carry 0x14B0002, the number of Event30003.
- Attack_SM: 1500/1600 carry 3000's, 3014 carries 3013's, 3025-3029 carry 3024's, 6000-6003 carry 3036's.
c7006 (High Wall hollow): IdleUnique702-710 all carry 701's number, IdleUniqueToIdle1702-1710 carry 1701's, and
13 added attacks / 5 event states share numbers the same way.

That matches every probe: the added states (30001, 30002) show nothing; 30003, whose number they share, shows a
stiff pose; states with a number of their own (30000, 30008, 3012) play.

## Probe
udfix-Program.cs rewrites only those numbers (low 16 bits = index) in a behbnd and prints each change.

1. `udfix Mods\ER\chr\c7112.behbnd.dcx` and `udfix Mods\ER\chr\c7006.behbnd.dcx` (keep the output).
2. Run A, Gundyr (Rtest save at Gundyr), no emevd change: does he kneel on load (45005810 forces 30002)? Walk up,
   press E on the sword prompt: does the player pull, does Gundyr stand, does the boss bar show?
3. Run B, Gundyr, animprobe `W40,A3014:0,W8,A30001:1,W8,A30003:1,W8,A30002:1`: what each shows.
4. If there's time, Run C at the High Wall start: do the hollows kneel or pray until hit?
Restore both behbnds (and the emevd) after, hash-check.
