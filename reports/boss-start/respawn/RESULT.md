# Respawn at DS3 bonfires, release 1.0.628 (with #283), 2026-11-24

## Port log (porter-port-maps-20261124-090712.log)
```
bonfire respawn: 15 maps send a death to their nearest bonfire until the player rests (105 events; flag 1039290101, cleared by every warp in); 0 maps without a bonfire use their start point ()
179 warps into ported maps clear the respawn flag 1039290101 first
```

## Test 1 (die after warping in, without resting)
Gundyr save; F1 > DS3 run > Firelink Shrine / Cemetery of Ash > **Warp here**, which loads m45_00 at its start point.
To get killed without steering blind, the m45_00 emevd had probe 22's helpers: a short warp to the sword 4 s after
load, with 45000802/45000800 cleared. I pulled the sword with god mode off, and Gundyr killed the player
("YOU DIED", frame 7 of 1-fight-death-respawn.png).
**Respawn: not the Chapel of Anticipation.** After the loading screen the player was in m45_00 again, at a rocky
cliff-side spot (1-respawn-spot.png, sheet frame 9), not ER's chapel as in probe 22. A few seconds later my test warp
moved the player back to the sword (frames 10-22), so the exact bonfire isn't confirmed from the frames. It's in
Cemetery of Ash / m45_00.

## Tests 2 and 3 (rest at another bonfire, reload, die; travel, die): not done
They need the bonfire rest and travel menus, and a walk to a second bonfire, which I can't steer to reliably with
scripted input. They need a hand check.

Restored: m45_00 emevd 897fb8e7... (the 1.0.628 build's); the save is back.
