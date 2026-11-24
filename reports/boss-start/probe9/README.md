# Probe 9: internal animation IDs the port registers (2026-11-24)

Probe 8: Attack3012 pointed at a000_003014 with the port-registered internal ID 213 -> a sub-second twitch. Is the
port-registered ID the problem? (The player moveset port, which works, never appends to m_animationNames.)

1. `idprobe Mods\ER\chr\c7112.behbnd.dcx`: Attack3012's clip names a000_003014 but keeps the template ID 33;
   Event30002's clip (added state) takes Event30003's template ID 146.
2. Gundyr, animprobe `W40,A3012:0,W10,A30002:1,W10,A3012:0`.
3. Report: does 3012 now play a full ~3 s different move (3014's raised-sword attack) or the twitch? Does 30002 show
   anything (kneel, stiff pose, nothing)? Restore and hash-check.
