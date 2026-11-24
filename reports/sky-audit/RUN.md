# Sky audit: in-game screenshots (2026-11-24)

- Content build: **1.0.628** (`TheDarkSign\Mods\ER\darksign_build.txt`), built 2026-11-24 09:52 by the launcher's rebuild.
- Port log: `TheDarkSign\Work\logs\porter-port-maps-20261124-090712.log` (report `port-maps-20261124-095218.txt`).
- Test-menu DLL: `TheDarkSign\Tools\Dlls\dksign_testmenu.dll` (2026-11-22). No mod or emevd file was changed.
- Launch: me3 with `TheDarkSign\Tools\me3\darksign-er.me3`, the same as `Work\scratch\probe-live-rtest.bat`, plus `DKSIGN_GOD=1` and `DKSIGN_HIDE=1`.
  The Rtest save (`Work\scratch\saves-overnight`) was backed up to `Work\scratch\skyaudit-save-backup`, overwritten with `saves-gundyr-r5`, and restored afterwards (hashes match).
- God mode was on from launch (log: `god mode: on at launch`). **`DKSIGN_HIDE` does nothing in this DLL build**: the string isn't in the DLL and there is no Ctrl+Shift+F9. Enemies could see me (boss bars came up in the Archives and Dreg Heap).
- Game closed with WM_CLOSE (like Alt+F4) and exited within 15 s.

## How the shots were taken
- **I couldn't warp to bonfires.** The F1 > DS3 run tab only has one "Warp here" per area, which goes to the map's start point, plus boss "Fight" buttons. So every map below is its start point, except map 1 (see its row).
- Each set has 4 views turned about 90 degrees apart (`-1` to `-4`, 27 mouse steps of 80 px each) and one view tilted up steeply (`-up`). Pitch was set from the camera's lowest limit. Mouse acceleration made the tilt vary: some sets show mostly sky, others are nearly level.
- Files are full native 1920x1080 captures of the game's client area. The **Windows taskbar covers the bottom ~50 px** of every shot, because the window sits at 8,31 on a 1080-high screen. Files named `*-extra-1280.png` are 1280x720 copies from the probe screenshots.
- The sky dome itself is always visible in the `-up` view where the start is outdoors. Indoor starts give little or no sky.
- The game didn't show an in-game time anywhere.

## Per map
| # | DS3 map (ER slot) | Spot used | Warp | Sky notes |
|---|---|---|---|---|
| 1 | m40 Firelink / Cemetery (m45_00) | **Cemetery of Ash**, by the grace where I respawned after falling out of the world in no-clip (`01-m40-cemetery-of-ash-*`). The map start is inside Firelink Shrine: one indoor frame only (`01-...-firelink-shrine-start-extra-1280`). I didn't reach the Firelink courtyard. | ok | Pale overcast sky with a soft sun disc, seen through dead trees, plus a warm haze band at the horizon. Nothing obviously broken, but trees block most of it. Red Japanese text "仮カシー" (looks like a placeholder label) at the bottom-left of view 4. |
| 2 | m30 High Wall (m45_01) | Start = High Wall of Lothric bonfire (outdoors) | ok | Yellow cloudy dome, broadly right. **Hard horizontal edge** where the cloud dome meets a flat paler strip at the horizon. Beyond the walls, a **flat blue band** stands in for the distant mountains. **Dark grey disc / half-sphere** straight overhead in `-up`. |
| 3 | m31 Undead Settlement (m45_02) | Start = path below the settlement (outdoors, dusk) | ok | Same yellow cloud dome. **The same dark disc overhead** (also in `03-...-dark-disc-extra-1280`). Views 3 and 4 are mostly dark walls. |
| 4 | m33 Road of Sacrifices / Farron Keep (m45_04) | Start = dark ruin interior (Halfway Fortress area) | ok | Indoor. A **saturated blue sky** shows through an arch in view 2 and at the left of view 1. It looks clean, but bluer than I'd expect for DS3: compare with the reference. |
| 5 | m35 Cathedral of the Deep (m45_05) | Start = inside the cathedral | ok | Indoor, no sky. Small blown-out white window shapes. A giant enemy is nearby (HUD up). |
| 6 | m38 Catacombs / Smouldering Lake (m38_10) | Start = Catacombs bonfire (underground) | ok | Indoor, no sky. I couldn't reach Smouldering Lake from the menu. |
| 7 | m37 Irithyll (m38_11) | Start = dark hall at the Irithyll bonfire | ok | Indoor. Bright pale windows only. No moon view. |
| 8 | m39 Irithyll Dungeon (m38_12) | Start = dungeon bonfire | ok | Indoor, no sky. I couldn't reach Profaned Capital. |
| 9 | m30_01 Lothric Castle (m45_03) | Start = courtyard by the castle gate (outdoors, between walls) | ok | Pale yellow sky. In the menu shot taken there, the **edge of a huge circle shows across the sky** (the same dark disc as maps 2 and 3, seen from the side). |
| 10 | m34_01 Grand Archives (m38_13) | Start = **Twin Princes arena**: the warp started the boss (Lorian's bar up, Lothric's dialogue subtitled) | ok | The 5 shots caught the boss intro: a **big tilted terrain plane** (brown far landscape at about 15 degrees) under a pale yellow sky. Afterwards it's the dark arena (`10-...-arena-extra-1280`), lit by a beam from above. |
| 11 | m32 Archdragon Peak (m38_14) | Start = ruin by the Archdragon Peak bonfire (sky overhead) | ok | **Bright blue sky with white cumulus clouds** in views 1 and up. That looks like a clear daytime / Elden Ring style sky rather than DS3's grey storm: compare with the reference. |
| 12 | m41 Kiln (m38_15) | Start = Flameless Shrine area, inside the ring of ruins | ok | Black sky overhead. A **large flat grey triangle** with hard edges hangs in view `-up` and in the menu shot: an untextured polygon. |
| 13 | m45 Painted World (m19_70) | Start = big snowy chapel interior, Corvian enemies right beside me | ok | Indoor, no sky. Flying up with no-clip didn't move me (W did nothing while held), so I left it. |
| 14 | m50 Dreg Heap (m11_70) | Start = **Demon Prince arena**: the warp started the boss (Demon in Pain / Demon from Below bars) | ok | Near-black sky with ember particles. No obvious blocks. |
| 15 | m51 Ringed City (m11_72) | Start = cliff ledge above the city, standing inside a winged angel-like figure that blocks the camera | ok | **Clearest defects**: in views 4 and up, **flat-colour hard-edged rectangles** (saturated blue and pink panels) cover parts of the sky. Behind them is a bright blue sky with white Elden Ring-like clouds rather than DS3's dusky haze. View 2 shows dark rock cliffs against a purple-pink sky. |

## Common findings
1. A **dark grey disc** overhead on High Wall and Undead Settlement, and its edge on Lothric Castle. It looks like a sky piece drawn opaque and unlit (maybe a cloud or sun layer whose texture or alpha didn't carry over).
2. **Hard-edged bands at the horizon** on High Wall: the cloud dome meets a flat paler strip, and a flat blue mountain band sits below it.
3. **Flat-colour blocks** on Ringed City (blue and pink rectangles) and an **untextured grey triangle** in the Kiln.
4. **Blue daytime skies** on Archdragon Peak and Ringed City, and through the arch on Road of Sacrifices. *Update after the references:* DS3's Archdragon Peak really is bright blue with white clouds, so that one looks right. The Ringed City should be a yellow-white cloud sea under a hazy sun, so blue is wrong there.
5. Most map start points are indoors (Cathedral, Catacombs, Irithyll, Dungeon, Painted World, Firelink). A bonfire warp, or a working no-clip climb, is needed to audit those skies.

## Pass 2 (same build, same launch setup, 2026-11-24 evening)
Goal: get usable sky for the starts that had none in pass 1.
- **Grace Travel only lists bonfires already lit on this save.** I lit Firelink Shrine's bonfire ("Touch grace", then "Rest") and its Travel list offered only "Cemetery of Ash". The rest menu shows the Elden Ring time of day: **"Late Night"**. Pass 1 probably ran at night too, so check whether time of day changes the ported skies.
- I fell back to the F1 boss "Fight" warps for arenas that might be outdoors:

| # | DS3 map | Spot | Result | Sky notes |
|---|---|---|---|---|
| 13 | m45 Painted World | Sister Friede "Fight" warp (`13-m45-spot2-friede-warp-*`) | warp ok, lands inside Ariandel Chapel | Indoor. Snow on the nave floor, no sky. |
| 13 | m45 Painted World | Champion's Gravetender "Fight" warp (`13-m45-spot2-gravetender-arena-*`, plus `...-white-quads-extra-1280`) | warp ok, **outdoor** snowy canyon arena | Dark night sky with stars, plus **flat pale grey-white rectangles with hard edges hanging in the sky**: the same kind of untextured quad as the Kiln triangle. The DS3 references (13-*) show a white, foggy, snowy sky. |
| 8 | m39 Irithyll Dungeon / Profaned Capital | Yhorm "Fight" warp (`08-m39-spot2-yhorm-arena-*`) | warp ok | Indoor (dark hall). No sky. |

- I couldn't reach the rest in this pass. Cathedral has only Deacons, which is indoors. Catacombs / Smouldering Lake has no boss warp. Irithyll's Pontiff and Aldrich arenas are indoors. Grand Archives has no boss warp; the area warp lands in the Twin Princes arena. The Firelink courtyard would mean walking out of the shrine blind. All of these need either lit bonfires on the test save or a per-bonfire warp in the F1 menu.
- **Taskbar:** I moved the window to the upper monitor, but Windows draws a taskbar there too, and the test script's mouse moves can't reach negative screen coordinates, so I moved it back. Instead, `ingame\cropped\` has a copy of every full-size shot (90) with the bottom 50 px removed. The originals are untouched.
- The game closed cleanly and the Rtest save was restored again (hashes match).

## Other outputs
- `..\portlog-sky-lines.txt`: 113 lines from the port log matching "sky domes:", "see-through sky layers:", "sky:", "far cards", "nolight" or "atmosphere".
- `..\sky-pieces.tsv`: 158 map-piece rows whose material list contains NoLight, with a `slot` column added. The map-pieces TSVs have no separate mtd column, so the match is on the `meshes` column, which lists each mesh's material.
