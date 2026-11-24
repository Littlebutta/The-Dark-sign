# Sky audit: Elden Ring port 1.0.628 vs Dark Souls III (2026-11-24)

**Inputs:** the in-game shots in `RUN.md` (two passes, same build), the DS3 reference pictures listed in `SOURCES.md` (kept on the PC only), `portlog-sky-lines.txt` (113 sky lines from `porter-port-maps-20261124-090712.log`), `sky-pieces.tsv` (158 map-piece rows whose meshes use a NoLight material) and `TheDarkSign\Work\reports\map-pieces-<slot>.tsv` (all pieces, same build).

**Sheets:** the side-by-side sheets (port left, DS3 right) are in `Work\reports\sky-audit\sheets\` on the PC and are not in the repo, because the right half is third-party images. The repo has the in-game half only, as `contact/NN-*.jpg`. `make_sheets.py` (on the PC) rebuilds both. All in-game shots are cut at the top of the Windows taskbar (y = 1000 of 1080).

**Port-log line numbers used below** (`portlog-sky-lines.txt`; one block per map, in port order): L1 Firelink (m45_00), L8 High Wall (m45_01), L16 Undead Settlement (m45_02), L23 Painted World (m19_70), L31 Lothric Castle (m45_03), L39 Road of Sacrifices (m45_04), L46 Cathedral (m45_05), L54 Catacombs (m38_10), L60 Irithyll (m38_11), L68 Irithyll Dungeon (m38_12), L76 Grand Archives (m38_13), L83 Archdragon Peak (m38_14), L91 Kiln (m38_15), L98 Dreg Heap (m11_70), L106 Ringed City (m11_72).

How to read it: **Seen** = visible in the screenshots or written in the log/TSVs. **Guess** = my reading of the cause, still to be proven.

---

## 1. Shared causes (fix the porter rule, not one map)

### A. Every NoLight variant gets the same opaque Elden Ring template
- **Seen:** every map's log block starts `nolight=m10_00_003, nolight-blend=m10_00_003, nolight-edge=m10_00_003` (L1, L8, L16, ... L106). So DS3's blended `M[A]_al_NoLight` layers (cloud bands, haze, far mountain rings), its alpha-tested `M[A]_NoLight_e` cards (distant tree and scenery billboards) and its plain opaque `M[A]_NoLight` domes all go through **one** template.
- **Seen (symptoms):** hard-edged flat shapes in the sky on four maps:
  - Kiln: a grey triangle.
  - Painted World (Gravetender arena): pale textured rectangles.
  - Ringed City: flat blue rectangles and a stepped pink patch.
  - High Wall, Undead Settlement and Lothric Castle: a large dark disc overhead.
- **Guess:** if `m10_00_003` neither blends nor alpha-tests, each see-through layer draws as an opaque sheet of its RGB. A cloud or haze texture keeps its shape in the alpha channel, so its RGB alone often looks flat or dark. That matches every symptom above.
- **Fix area:** template choice in the material mapper. Give `nolight-blend` an unlit **alpha-blended** Elden Ring template and `nolight-edge` an unlit **alpha-tested** one, for every map at once. Check: in the built mod, dump the material params of one `nolight-blend` mesh (the mod-inspection tools can do this) and confirm its blend mode is not opaque.

### B. "Triangles under half visible are left out" turns soft fades into hard polygon edges
- **Seen:** the log drops whole triangles where DS3's vertex alpha is under 0.5:
  - Firelink: 3,558 of 9,859 triangles (L3).
  - High Wall: 1,152 of 2,432 (L11).
  - Kiln: 1,664 of 3,210 (L93).
  - Dreg Heap: 1,984 of 4,125 (L101).
  - On High Wall it removes two meshes completely (`m30_00_sky_02_a`, `m30_00_sky_05_a`), and one on Lothric Castle (L34).
- **Seen:** the Kiln triangle and the Ringed City's stepped pink patch have straight, triangle-shaped outlines.
- **Guess:** this rule exists because of A: with an opaque template, the faded parts would show as solid. Once A is fixed, the rule should go, so the layers can fade by vertex alpha as they do in DS3. Removing whole haze bands (the High Wall `sky_02`/`sky_05` cases) also takes away the layer that hides the dome's horizon seam (see D).

### C. Two DS3 sky material types have no Elden Ring material, so they are dropped
- **Seen:** `meshes left out (no Elden Ring material for them yet)`. `M[A]_al_m_NoLight_Opacity` is dropped on High Wall ×5, Lothric Castle ×5, Cathedral ×6, Irithyll ×6, Irithyll Dungeon ×3, Archdragon Peak ×19, Dreg Heap ×8, Ringed City ×12 and Painted World ×3. `M[A]_add` is dropped on High Wall and Lothric Castle (L9, L32, L47, L61, L69, L84, L99, L107).
- **Seen in sky-pieces.tsv:** the `_Opacity` meshes are the soft cloud cards (`*_kumo_30_a`, `*_kumo_50_a`, `m50_00_enkei_kumo_01_a`, `m51_00_kumo_30_a`). Many pieces are wholly `left out: no mesh the port can draw`. On Archdragon Peak that is 8 pieces, every one `m32_00_kumo_50_a`. The single `M[A]_add` mesh is `m30_00_sky_06_a`, a ~730-920 m round mesh in the High Wall and Lothric Castle sky piece `m009000`.
- **Guess:** this is why port skies look plainer than DS3's (fewer cloud masses, no drifting cloud banks below Archdragon Peak or the Ringed City). `m30_00_sky_06_a` is most likely DS3's **sun glow / sun disc** (additive), so High Wall and Lothric Castle have no visible sun.
- **Fix area:** map `_Opacity` to an unlit blended template, and `M[A]_add` to an additive one, in the same mapping table as A.

### D. Two-layer domes lose their second (cloud) layer on four maps
- **Seen:** the dome's second texture layer (DS3 UV 2) is not shown on Firelink (L2), Undead Settlement (L17), Irithyll (L62) and Kiln (L92): `1 two-layer meshes, 0 show their second layer`. It is shown on Road of Sacrifices, Cathedral, Painted World, Dreg Heap and Ringed City.
- **Seen:** the Undead Settlement sky in the port is an almost blank bright cream wash. DS3 shows heavy grey and white storm clouds with dark gaps (refs 07, 08, 10).
- **Guess:** the "show second layer" rule only fires for some dome material or UV layouts. The four failing domes need the same handling as the five that work. Look at why the detector returns 0 for `m31_00_sky_a` (`M[A]_2m_NoLight_scroll`, a scrolling dome) while it finds `m51_00_sky_70_a`.

### E. Maps with no DS3 dome show Elden Ring's own sky
- **Seen:** no sky dome and no see-through layers on Catacombs (L55-56), Irithyll Dungeon (L70-71), Grand Archives (L77-78) and **Archdragon Peak** (L85-86). sky-pieces.tsv has no dome row for m38_14 (only dropped `kumo_50` cards).
- **Seen:** the Archdragon Peak start still shows a bright blue sky with white cumulus. The Grand Archives boss intro shows a pale yellow cloudy sky.
- **Guess:** with nothing from DS3 there, what you see is Elden Ring's weather/time-of-day sky. On Archdragon Peak it happens to resemble DS3 (also blue). But it is not DS3's sky (no DS3 moon, and the clouds are Elden Ring's). In DS3, Archdragon Peak's and the Grand Archives' skies probably come from pieces outside the map we port: a shared sky map or another map loaded alongside, such as Lothric Castle for the Archives.
- **Fix area:** a porter rule that, when a DS3 map has no dome of its own, takes the dome from the DS3 map that supplies it in game, and always replaces Elden Ring's sky. It must not leave Elden Ring's sky visible.
- **Same family:** on the Painted World (Gravetender arena, pass 2 at "Late Night") the sky is **black with stars**, which DS3's Ariandel never has. On the Ringed City (pass 1) a **bright blue Elden Ring-looking sky** shows between the flat panels. Both point to Elden Ring's sky showing through where the DS3 dome or cloud layers are missing or drawn wrong. Open question: the log reports domes `m45_00_sky_01_a` and `m45_00_kumo_31_a` for the Painted World (L25), but neither name appears in `map-pieces-m19_70_00_00.tsv`. Check whether they were written at all.

### F. Far scenery and horizon haze
- **Seen:** High Wall's far mountains (`m30_00_mountain_60_a`, pieces m901000-m901005, up to 9.6 km wide) are listed as `written`. In game, beyond the walls, there is only a **flat blue band** under a **hard horizontal edge** in the cloud dome. DS3 shows misty snow mountains and layered fog banks there (Fextralife "screens-6").
- **Seen:** on Firelink, Kiln, Ringed City and Dreg Heap, DS3 draws a thick white or yellow cloud sea or haze at the horizon. The port shows little or none of it. Most of those layers are B and C meshes (`m40_00_mountain_04_382_a`, `m41_00_m40_00_mountain_04_382_a`, `m51_00_enkei_kumo_04_a`, `m50_00_m40_00_sky_05_392_a`).
- **Guess:**
  - The hard edge is the dome's lower seam. In DS3 the haze band `m30_00_sky_02_a`/`sky_05_a` hides it, but the porter removed that band (B).
  - The flat blue band is the `m30_00_water_50_a` sea plane, or Elden Ring fog, seen because the mountain ring is not drawn at that distance. Possible reasons: draw-distance or LOD culling of huge pieces, or the mountains sitting below the horizon after the map was moved. Check the mountain pieces' position and draw distance in the built MSB before changing anything.

### G. Overall tint and brightness
- **Seen:** High Wall, Undead Settlement, Lothric Castle and the Grand Archives intro all have the same even cream-yellow sky. DS3's pre-eclipse High Wall and Undead Settlement skies are grey and white with pink haze and strong contrast.
- **Guess:** this is partly D and C (fewer cloud layers, so less contrast) and partly Elden Ring's sky and post-process pipeline tinting an unlit dome. It cannot be judged until A-D are fixed and the time-of-day question is settled.

### Time of day
- **Seen:** the pass-2 rest menu said **"Late Night"**. Pass 1 had no time readout. The Painted World stars (pass 2) and the Archdragon Peak blue sky (pass 1) both look like Elden Ring sky at different times.
- **Guess:** wherever Elden Ring's sky leaks through (E), the result depends on the Elden Ring clock. Before blaming dome textures for colour, re-shoot High Wall, Archdragon Peak and the Gravetender arena at two Elden Ring times (rest > pass time to Morning and to Night). Whatever changes between the two shots is coming from Elden Ring, not from DS3.

---

## 2. Per map

Legend for "missing / lacking": tint, sun/moon, clouds, far scenery, horizon haze, flat quads, Elden Ring-looking sky.

### 01 Firelink Shrine / Cemetery of Ash (m45_00): Cemetery of Ash grace
| | DS3 | Port |
|---|---|---|
| Sky | Grey-white overcast, big cloud masses, soft sun with light shafts | Pale overcast, soft sun disc, seen through trees |
| Horizon | Snowy mountain range in white haze (Fextralife view) | Warm haze strip; mountains not seen from this spot |
- Missing / lacking: cloud detail (D: the dome's 2nd layer is off, L2); horizon haze/mountain ring thinned (B: 3,558 triangles cut, `m40_00_mountain_04_382_a`, `m40_00_kumo_40_a`, `m40_00_sky_50_a`, L3). No hard-edged shapes seen. Note: a red Japanese placeholder label "仮カシー" (not sky) is at the bottom-left of `-3`. RUN.md says view 4, but the shots show it in `-3`.

### 02 High Wall of Lothric (m45_01): map-start bonfire
| | DS3 (pre-eclipse) | Port |
|---|---|---|
| Sky | Grey-white and pink, dramatic clouds | Even cream-yellow clouds |
| Overhead | Clouds | **Dark grey disc** |
| Horizon | Misty snow mountains, fog banks | **Hard edge**, then a darker cloud strip and a **flat blue band** |
| Sun | Bright hazy sun area | None visible |
- Missing / lacking: horizon haze, far mountains, sun, tint; flat quad (the disc).
- Pieces:
  - Disc: likely the surviving `m30_00_sky_02_a` mesh in `m009000_9500` drawn opaque (A+B; one of its two meshes survives, L11).
  - Missing sun: `m30_00_sky_06_a` (`M[A]_add`, dropped, C).
  - Horizon seam: `sky_02`/`sky_05` haze removed (B).
  - Flat band: `m901000-m901005` mountains / `m30_00_water_50_a` (F).
  - Cloud cards: `m901030_9500` (`m30_00_kumo_50_a` ×5 dropped, C).

### 03 Undead Settlement (m45_02): map start below the settlement
| | DS3 (pre-eclipse) | Port |
|---|---|---|
| Sky | Dark grey storm clouds with white breaks | Nearly blank bright cream wash |
| Overhead | Clouds | **Same dark disc** |
- Missing / lacking: clouds (D: dome `m31_00_sky_a` in `m009000_9000`, 2nd layer off, L17); tint; flat quad (the disc).
- Pieces: disc likely `m31_00_cloud_03_a` (`m008501_9000`, 7 km blended cloud sheet, 195 of 904 triangles cut) drawn opaque (A+B, L18).

### 04 Road of Sacrifices / Farron Keep (m45_04): map start (ruin interior)
| | DS3 | Port |
|---|---|---|
| Sky | Dull grey-violet, hazy | Saturated clean blue, seen only through an arch |
- Missing / lacking: tint (too blue, too clean); horizon haze. Only a sliver was visible, so this is low confidence.
- Pieces: dome `m33_00_m37_00_kumo_30_a` (`m910010_2000`, scroll, its 2nd layer shown, L40). The blue may be the dome's UV2 layer at full strength, or Elden Ring sky behind (E / time of day).

### 05 Cathedral of the Deep (m45_05): no outdoor sky reached
- DS3 reference (05) is the post-eclipse orange sky. Only blown-out white windows in the port.
- Log: dome `m35_00_o359000_02_a` with its 2nd layer shown; 6 `_Opacity` cloud meshes dropped (C, L47).

### 06 Catacombs / Smouldering Lake (m38_10): no outdoor sky reached
- Log: no dome, no layers (L55). `m38_00_m37_00_sky_04_a` and `m38_00_UndergroundLake_Landscape_00_a` are `left out: far view`. Smouldering Lake's backdrop is likely to be missing when it is reached (E / far-view rule).

### 07 Irithyll of the Boreal Valley (m38_11): no outdoor sky reached
- DS3: night-blue sky with a **big moon**, dark clouds, icy haze.
- Log: dome `m37_00_sky_04_a` with its 2nd layer off (D, L62); 11 blended layers, **9 wholly removed** (B, `m37_00_kumo_01_a`, `m37_00_sky_04_a`, L63); 6 `_Opacity` meshes dropped (C, `m37_00_kumo_50_a`); distant tree cards `m37_00_enkei_70_a` go through the same `nolight-edge` template (A).
- Expect: moon and clouds missing, and possibly opaque tree-card rectangles. This needs a shot from the bridge or the Pontiff approach.

### 08 Irithyll Dungeon / Profaned Capital (m38_12): no outdoor sky reached (start + Yhorm warp, both indoors)
- Log: no dome (L70); `m39_m37_00_kumo_50_a` cards dropped (C); `m39_00_Cloud_01_a` written as plain alpha. DS3 reference shows only a dark sky over the Profaned Capital.

### 09 Lothric Castle (m45_03): map start (courtyard by the gate)
| | DS3 (07 pre-eclipse, dark; 06 post-eclipse, orange) | Port |
|---|---|---|
| Sky | Dusky / orange | Pale cream |
| Overhead | Clouds | **Edge of the same huge disc** visible in the `-up` view |
- Missing / lacking: tint, sun (`m30_00_sky_06_a` `M[A]_add` dropped, C); flat quad (disc).
- Pieces: likely the surviving `m30_00_sky_02_a` in `m009000_9000` (A+B, L34); `m902200_9000` `kumo_50` ×5 dropped (C).
- Not sky: the bright jagged-edged opening in the wall in `-up` and `-2` looks like missing castle geometry. Worth a separate check.

### 10 Grand Archives (m38_13): Twin Princes boss intro
| | DS3 (post-eclipse refs) | Port |
|---|---|---|
| Sky | Orange with eclipse (post-lords) | Pale yellow clouds, no eclipse (fits pre-eclipse) |
| Ground | Rooftops | A large tilted brown terrain plane at ~15° (intro camera) |
- Missing / lacking: Elden Ring-looking sky (E: the map has no dome or layers of its own, L77-78). The tilted terrain is probably the cutscene's own backdrop or camera roll. Check it against the cutscene port before calling it a sky bug.

### 11 Archdragon Peak (m38_14): map start (ruin by the bonfire)
| | DS3 | Port |
|---|---|---|
| Sky | Deep blue, white wisps, **moon** | Blue with white cumulus |
| Horizon | White cloud sea over snow peaks | Not seen from this spot |
- Missing / lacking: Elden Ring-looking sky (no DS3 dome at all, E); moon; cloud sea (8 `m32_00_kumo_50_a` pieces left out, C, L84).

### 12 Kiln of the First Flame (m38_15): map start (inside the ring of ruins)
| | DS3 | Port |
|---|---|---|
| Sky | Dark storm sky, **Darksign eclipse with a red beam**, white cloud sea | Black overhead |
| Shape | n/a | **Large flat grey triangle** with hard edges |
- Missing / lacking: eclipse/beam, horizon haze; flat quad.
- Pieces: triangle likely a remnant of `m902000_9000` (`m41_00_m40_00_kumo_10/11_a`, `mountain_04_382_a`) or the `m41_00_m40_00_sky_01_a` blended layer in `m900001_9000`, drawn opaque after 1,664 of 3,210 triangles were cut (A+B, L93). The dome's 2nd layer is off (D, L92). The eclipse is probably a layer in that set (`sky_05_392_a`) or an SFX. Note that Dreg Heap's `m50_00_Test_sun_000_a` (a flat 2 × 6 km card) is the same kind of eclipse card.

### 13 Painted World of Ariandel (m19_70): Champion's Gravetender "Fight" warp
| | DS3 | Port |
|---|---|---|
| Sky | White-grey snow fog, no stars | **Black night sky with stars** |
| Shapes | n/a | **Pale rectangles** with hard edges and snow-like texture |
- Missing / lacking: tint, fog/haze (the whole white-out look); Elden Ring-looking sky (stars); flat quads.
- Pieces:
  - Quads: likely the `m45_00_enkei_70_a` distant-scenery cards in `m002110_6000` (`nolight-edge`, so no alpha test, A).
  - Cloud cards dropped: `m091020-m091022_1000` (`m45_00_kumo_30_a`, C).
  - Dome: `m090000_1000` (`m45_00_kumo_30_a` scroll).
  - See also the open question under E (domes named in L25 but not in the TSV).

### 14 The Dreg Heap (m11_70): Demon Prince arena
| | DS3 | Port |
|---|---|---|
| Sky | Dark, eclipse low on the horizon, ash haze | Near-black, ember particles |
- Missing / lacking: eclipse; haze. No hard-edged shapes seen from this spot.
- Log: 7 layers, 1,984 of 4,125 triangles cut (`m50_00_Test_sun_000_a`, `m50_00_m40_00_sky_05_392_a`, `m50_00_sky_91_a`, L101); `enkei_kumo_01`/`kumo_30` ×8 dropped (C); dome `m50_00_sky_90_a` with its 2nd layer shown. The far-view piece `m040001_3000` (`m50_00_sky_69_a`) is left out.

### 15 The Ringed City (m11_72): map start (cliff ledge)
| | DS3 | Port |
|---|---|---|
| Sky | Yellow-white cloud sea under a hazy sun | **Flat saturated blue rectangles** and a **stepped pink patch**, with a bright blue sky and white clouds behind; purple-pink sky in view 1 |
- Missing / lacking: tint, sun, cloud sea (horizon haze); Elden Ring-looking sky; flat quads.
- Pieces:
  - Panels: likely `m51_00_enkei_kumo_04_a` in `m900011_9000` (blended + scroll), `m51_00_kumo_70_a` in `m000016_9000`, or `m51_00_TreeBackground_01_a` in `m080100_9000` (`nolight-edge`), all drawn through the opaque template (A, B; 33 of 372 triangles cut, L109).
  - Cloud sea `m51_00_kumo_30_a`: 12 meshes dropped, four pieces wholly left out (C).
  - Dome `m51_00_sky_70_a` (`m900001_9000`) has its 2nd layer shown.
  - Far-view piece `m040001_7000` (`m51_00_enkei_33_a`, `m51_00_Aurora_03_a`) is left out.

---

## 3. Suggested order
1. **A + C** (one material-mapping change): blended, alpha-tested, opacity and additive NoLight each get a matching unlit Elden Ring template. Then retire **B** (stop cutting triangles). Expected to fix the disc, the triangle, the quads and the panels, and to bring back clouds, haze and sun glow on every map at once.
2. **D**: the second-layer detector for scrolling/2m domes (Firelink, Undead Settlement, Irithyll, Kiln).
3. **E**: always replace Elden Ring's sky. For maps with no dome, take the dome from the DS3 map that supplies it.
4. Re-shoot with lit bonfires, or a per-bonfire warp, at two Elden Ring times. Then judge **F** (far mountains, horizon band) and **G** (tint).

Not audited because no outdoor sky was reached: Cathedral, Catacombs / Smouldering Lake, Irithyll, Irithyll Dungeon / Profaned Capital, the Firelink courtyard. Those need lit bonfires on the test save or a per-bonfire warp in the F1 menu.
