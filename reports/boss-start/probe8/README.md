# Probe 8: is our behaviour graph used at all? (2026-11-24)

Probe 7 (renumbered selectors) changed nothing. No state the port added to an enemy graph has ever been seen working
in game (phase changes #253, hollow poses #210, added attacks are all untested). Before any more graph theories:
does the game read the c9997.hkx in our behbnd?

1. `graphprobe Mods\ER\chr\c7112.behbnd.dcx` (only Attack3012's a000 clip changes: it names 3014's clip, 0.4x speed).
2. Gundyr, animprobe `W40,A3012:0,W10,A3012:0` (3012 twice; it played in probe 6 as a 5 s swing and lunge).
3. Report: does 3012 look the same as in probe 6, or does it play a different, slow motion (3014's, about 8 s long)?
Restore and hash-check.
