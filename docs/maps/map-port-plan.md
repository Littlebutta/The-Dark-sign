# Porting Dark Souls III maps into Elden Ring

Goal (Devin, 2026-11-03): port the DS3 maps into Elden Ring with everything working, and add an F1 menu tab that
starts a DS3 run inside Elden Ring.

## How it works

`DarkSignPorter port-maps` converts every DS3 map that has an Elden Ring slot in one run. The launcher runs it right
after `port-weapons`. If it fails, the weapons still load and the game starts; the log is in
`TheDarkSign\Work\logs\port-maps-*.txt`.

| Piece | DS3 | Elden Ring | Code |
|---|---|---|---|
| Layout | `map/mapstudio/m40_00_00_00.msb.dcx` (MSB3) | `map/mapstudio/m45_00_00_00.msb.dcx` (MSBE); each part is a copy of a vanilla part of the same kind with DS3's name, model, transform and entity ID | `Maps/MsbPort.cs` |
| Map pieces | `map/m40_00_00_00/m40_00_00_00_NNNNNN.mapbnd.dcx` | `map/m45/m45_00_00_00/...`; materials become cloned Elden Ring matbins (`DKS_40_00_00_00_NNNN`) in `allmaterial`, copied from a plain one-layer material of a legacy dungeon's map pieces (the colosseums are built from assets, and most legacy dungeon pieces use terrain blend materials), or from JortPob's albedo-only asset material `AEG006_030_ID001`; samplers name the DS3 texture by bare name, as JortPob's do | `Maps/MapModelPort.cs` |
| Textures | `map/m40/m40_000N.tpfbhd/.tpfbdt` | a new `map/m45/common/m45_0000.tpfbhd/.tpfbdt` (Elden Ring has no map area texture binders, but loads this one: JortPob's approach), one uncompressed TPF per texture plus its `_l` copy, plus a flat metallic map | `Maps/MapTextures.cs` |
| Collision | `h40_00_00_00.hkxbhd` (Havok 2014 packfile) | `h45_00_00_00.hkxbhd` (Havok 2018 XML tagfile, same compressed-mesh tree); every triangle is checked after conversion | `Maps/CollisionPort.cs`, `Maps/MapCollisions.cs` |
| Navmesh | `map/m40_00_00_00/m40_00_00_00.nvmhktbnd.dcx` (Havok AI 2014, one file per navmesh in its own space) and `m40_00_00_00.nva.dcx` (the navmesh list: placement, connections, gate nodes) | `map/m45/m45_00_00_00/m45_00_00_00.nvmhktbnd.dcx` (Havok AI 2018 tagfiles, an `n` and an `o` file per navmesh, in map space, face tree in a query mediator) and `m45_00_00_00.nva.dcx` (Elden Ring's list: NameIDs by position, both sets, connections renumbered, gate nodes). See "Navigation" below. Fallbacks `map-navmesh`. | `Maps/NavmeshPort.cs`, `Maps/NavFaceTree.cs`, `Maps/NavmeshList.cs`, `Maps/MapNavmesh.cs` |
| Map events | `event/m40_00_00_00.emevd.dcx` | `event/m45_00_00_00.emevd.dcx`, converted instruction by instruction (see "Event scripts" below); the slot's own Elden Ring events no longer run | `Maps/EventPort.cs`, `Maps/MapEvents.cs` |
| Common events | `event/common_func.emevd.dcx` (only the events the ported maps start, and the ones those start) | added to `event/common_func.emevd.dcx` under IDs 103x291000-103x291999 | `Maps/MapEvents.cs`, `Maps/FlagMap.cs` |
| Enemies and bosses | MSB3 enemy parts | MSBE enemy parts at DS3's spot, on DS3's collision, with DS3's entity ID moved to the slot. The character is the ported DS3 one when `Mods\ER\darksign_chr_map.tsv` lists it, otherwise an Elden Ring stand-in of the same kind copied from a vanilla legacy-dungeon placement; DS3 bosses (boss health bar in DS3's events) get Elden Ring bosses. DS3's people get Elden Ring people (see "People" below). Fallbacks `map-enemies`, `map-people`. | `Maps/EnemyPort.cs` |
| Objects | MSB3 object parts, `obj/oNNNNNN.objbnd.dcx` | MSBE asset parts at DS3's spot with DS3's entity ID; each DS3 object model becomes an asset `asset/aeg/aeg92x/aeg92x_NNN.geombnd.dcx` (+ `_l.geomhkxbnd.dcx` collision for objects that don't break) with a row in AssetEnvironmentGeometryParam, built the way JortPob builds assets. Doors, lifts and levers move: animation binder, moving collision, ObjAct events and ObjActParam rows. Fallbacks `map-objects`, `map-doors`. See "Objects, regions and map-file events" and "Doors, lifts and levers". | `Maps/ObjectPort.cs`, `Maps/ObjectAnims.cs` |
| Regions and map-file events | MSB3 regions (event areas, warp, spawn and patrol points), PatrolInfo, PlatoonInfo, Generator | MSBE "Other" regions, spawn points and patrol route points with DS3's shape and entity ID; patrol routes, platoons and generators naming the ported enemies and points. Fallbacks `map-regions`. | `Maps/RegionPort.cs` |
| Map connections | MSB3 connect collisions | MSBE connect collisions copied from a legacy dungeon's, pointing at the neighbouring map's slot (ones to unported maps are left out) | `Maps/MsbPort.cs` |
| Run start/leave | | two events in `common.emevd` wait for flags the F1 menu sets, then use the game's WarpPlayer | `Maps/RunEvents.cs`, `TestMenu/src/overlay.cpp` |

### Map slots

Elden Ring may not load map IDs it doesn't ship (JortPob, the Morrowind port, only reuses existing ones), so the
DS3 maps take over the colosseum arenas, which single-player never visits:

| DS3 | Elden Ring slot |
|---|---|
| m40_00 Firelink Shrine / Cemetery of Ash | m45_00 |
| m30_00 High Wall of Lothric | m45_01 |
| m31_00 Undead Settlement | m45_02 |
| m45_00 Painted World of Ariandel | m19_70 (template m45_00) |

The Painted World takes m19_70, a small map Elden Ring ships (layout, map pieces, collision, navmesh) that the
single-player game never visits (area m19 is otherwise only the final boss arena). Its formats, template parts and
per-map side files come from m45_00, the colosseum every slot is proven to load from. Fallback `map-painted-world` B
puts it in m45_02 instead (the Undead Settlement is then left out), C leaves it out.

The other DS3 maps need more slots and are listed in `MapSlots.Ds3Maps`.
Entity IDs move with the map: 4000xxxx becomes 4500xxxx. The run starts at the DS3 player point with entity
x990-x999, which gets entity 45000990.

### F1 menu, "DS3 run" tab

- **Start DS3 run at the Cemetery of Ash** sets flag 1039292100. An event in common.emevd clears it, sets
  1039290100 (run active, saved) and warps to m45_00_00_00 entity 45000990.
- **Leave DS3 run** sets 1039292101. The event clears the run flag and warps to the first Roundtable Hold warp target
  found in Elden Ring's own scripts.
- The tab lists the ported maps from `Mods\ER\darksign_maps.tsv`.
- **Boss fights** lists every DS3 boss fight of the ported maps with a **Fight** button (`Mods\ER\darksign_bosses.tsv`,
  `Maps/BossArenas.cs`). Every DS3 boss fight is started by common event 20005800, called with the defeated flag, fog,
  arena area, entry flag, boss (entity or group) and first-encounter flag; the port reads those calls. The button
  sets flag 1039292400+i; an event in common.emevd turns the defeated, entry and first-encounter flags and the fight's
  own event flags off (so a fight already won runs again through every phase), warps to a new player point in the
  arena (entity x9970+; a DS3 player point inside the arena area if there is one, else a DS3 warp point inside it,
  else the area's centre 0.5 m above the boss's floor, else 1.5 m above the area's bottom: DS3's areas start below
  the floor, which put the player inside Oceiros' floor in 1.0.383), waits until the player is in the map and then turns the first-encounter flag and the entry flag on,
  which begins the fight without the fog wall, talk or cutscene. Fights whose boss isn't in the layout (the Champion
  Gundyr state of m40) get no button. The port-maps log lists each arena's landing point and flags.
  A first encounter that waits on a flag only an NPC's dialogue sets (Friede: 74500300, set by Father Ariandel's
  talk) is opened by a common.emevd event when the player comes within 8 m of the talk prompt's character
  (`BossArenas.TalkGate`, events 1039291500+).
- **Boss kits** (`Maps/BossKits.cs`, 2026-11-12): DS3 keeps part of each boss in the map script (Pontiff's
  half-health clone, 13705880; Friede's stage swaps; Iudex Gundyr's sword pull), so a boss outside its DS3 map froze
  at its phase change. Each fight's own events that only wait on its characters (no regions, objects, people or
  warps; arena-only commands such as cutscenes, map effects, cameras and item lots are blanked) are added to
  common.emevd for characters with fixed entity IDs, 1,190,000,000 + the DS3 entity, with the fight's flags in the
  run block's reset range (1039292500+) and events 1039291600+. A kit's start waits until every kit character is
  loaded, hides the companions a kit event brings in later, turns entry and first encounter on and then lets the
  kit's events run. A boss placed in its home map keeps its map's own IDs and events, so kits never touch it.
  `Mods\ER\darksign_boss_kits.tsv` tells the F1 spawner which entity a spawned boss and its companions get; the
  `events:` log line counts kits and spawnable kits. The boss randomizer can place kit characters the same way.

Flags are in the block of the empty sea tile m60_39_29 (1039290000+), the way JortPob allocates its flags.

## Checks

- `DarkSignPorter map-selftest` runs offline (no game files) and in CI. It checks collision conversion (XML and
  binary), template settings, layout conversion and write/read, start-point choice, group masks, run event
  instruction layouts against DarkScript3's definitions, and the return-point search.
- `DarkSignPorter map-survey` reports both games' map files for the slots on a PC with the games installed: paths,
  binder entry names, the DS3 start point, the host collision template, map materials and texture folders, and the
  warp targets. Run it first on the PC; it answers the open questions below.
- Every `port-maps` run ends with a **map coverage** block (and `Work\reports\map-coverage.tsv`): each ported map
  side by side, with its map pieces written of DS3's, pieces left out because their model had nothing to draw,
  models and meshes left out, faded triangles cut, collisions written (and DS3 files that are empty), objects,
  enemies, boss warps and the steps that FAILED. Its `WARN` lines (also in `darksign_maps_check.txt` as
  `coverage:`) name a map that loses 5% or more of its pieces, or has failures the other maps don't. Added after the
  1.0.383 playtest (2026-11-12): every fix had been checked on map 1, while that build's piece reports showed
  Undead Settlement missing 714 pieces and the Painted World 103 (one-layer vertex alpha read as a fade, fixed for
  every map in #171). Check this block for every map, not only the one a fix was aimed at.
- DS3 collision files with no root object (28 of the Painted World's `h45_00` files and one `l45_00` file; none in
  m30, m31 or m40) are reported as "empty in DS3", not as failed conversions. Their collision parts are left out:
  there is nothing to collide with.

### Why map pieces draw (fixed after the 2026-11-09 playtest)

In 1.0.24x the maps loaded and were walkable, but every map piece was invisible. Comparing the port with vanilla
files found four causes, all checked by `map-selftest` and by `port-maps`' own validator now:

1. **Draw flags.** The colosseum's layout has one map piece, `m009999_1000`, a proxy drawn only in reflections
   (`DrawOnlyReflectCam`, no `IsShadowDest`). Every DS3 piece was a copy of it. Ported pieces now get an ordinary
   piece's flags (`MsbPort.DrawNormally`, values from Stormveil's majority), whatever the template has.
2. **Vertex layout.** An Elden Ring material only draws meshes in the vertex layout its own vanilla meshes use
   (JortPob ships these per material as `MaterialInfo.xml`). Each ported mesh is rewritten into the layout of the
   vanilla mesh its template material came from (`MapModelPort.Conform`): one UV channel as Short2, one tangent,
   one colour. The layouts used are written to `Mods\ER\darksign_map_layouts.txt`.
3. **Culling box.** Elden Ring's per-mesh box is half extents, rotation, centre; DS3's is min, max
   (`MapModelPort.SetBoundingBox`).
4. **Layered materials.** DS3's ground and wall materials (`M[ARSN]_2m/3m/4m`) name their textures
   `..._AlbedoMap_0`, not `g_DiffuseTexture`, and store UVs in short units. The first layer is used.

The 2026-11-10 playtest of that build (1.0.253+) still showed nothing, with every check above passing and the game
loading every binder. A second comparison with vanilla found what else no vanilla map piece shares with the port's:

5. **Draw settings row.** Every vanilla map piece names a PartsDrawParam row (`isUsePartsDrawParamID`, 342 of
   Stormveil's 343): draw distance and LOD distances. The colosseum's proxy names none, and so did its copies. The
   port adds row 9400 the way JortPob makes its rows (a copy of row 1001, unlimited draw distance, no LOD
   switching) and every piece names it (`Maps/MapDrawParams.cs`). If the regulation can't be written the pieces
   are pointed at vanilla row 2046.
6. **Group masks.** Pieces had every draw and display group on with `Condition2` 0, a state no vanilla piece is in
   (masks come with `Condition2` 1). Pieces now carry no masks, like the 483 assets the colosseum draws.
7. **Binder file ID.** The model sits in its `.mapbnd` under file ID 200 in vanilla; the port wrote 0.
8. **Vertex colour, node name, material numbers.** Vanilla vertices hold (1, 0, 0, alpha) in the colour channel
   (the port wrote white), the model's one node is named after the model, and materials are numbered in order.

The 2026-11-10 playtest of that build (1.0.259) no longer loaded at all: the loading bar filled and stopped. The
game's file log shows it opened the layout, the collision and all 325 map piece binders of the Cemetery and then
nothing more, where the earlier, walkable build went on to the enemies' files. The likeliest reading is that fix 7
made the game read the converted models for the first time (which would also explain why the earlier builds drew
nothing), and that it then waits for something they name. Compared again with vanilla and with JortPob, the
converted models, materials and texture binder match in structure, which leaves three things nothing offline can
test. The next build changes the first two and measures the third:

9. **Where the materials are.** The port added its MATBINs to `allmaterial_dlc01.matbinbnd.dcx`, which is empty in
   the game's own files, so nothing shows the game looks map materials up there. They now go into
   `allmaterial.matbinbnd.dcx`, where JortPob puts the materials of its map pieces.
10. **Textures that don't exist.** A few materials named textures DS3's map binders don't hold (object and
    character textures, system textures: 8 in the Cemetery). Elden Ring's own map materials only name textures that
    exist, so whether the game gives up on a missing one or waits is unknown. Each now gets a flat grey stand-in
    under its name (`MapTextures.Write`).
11. **Test maps** (`MapPorter.PieceTest`). The diagnostic piece is gone (it sat in a map that couldn't load). Instead
    the three slots hold three nested layers, so one playtest tells which layer stalls: the first slot is the real
    port; in the second (`vanilla-model`) every map piece is the colosseum's own model, byte for byte; in the third
    (`vanilla-material`) DS3's geometry is drawn with the colosseum model's own vanilla material. Second loads:
    binders, layout and draw settings are fine. Third loads too: so is the converted geometry, and what is left is
    the ported materials and textures. Fallback `map-draw` C builds all three normally.
12. **1.0.271 playtest: both tests loaded, the real port still hung.** So the wait is on the ported MATBINs or the
    textures they name. Compared again with JortPob, the texture binder differed in two ways: its entries
    (`name.tpf.dcx`) are DCX (Kraken) compressed TPFs, where the port wrote bare TPFs under those `.dcx` names, and
    its low detail entry holds a texture named `name_l`, where the port's held `name`. `MapTextures.BuildBinder` now
    writes both the way JortPob does. The second slot's test is now `ported-material` (the port's MATBINs keeping
    their template's vanilla textures): if the first map still hangs and the second loads, the textures are still
    at fault; if the second hangs too, the added MATBINs are.
13. **1.0.279 playtest: the first map loads and draws with DS3's textures.** The texture binder packing was the
    cause. All three slots are the real port again; the tests remain behind `--piece-test <er map>=<test>` only.

Materials are sorted into kinds, each with its own vanilla template found in the legacy dungeons: opaque
(`M[AMSN_V]`), alpha tested (`_Edge`), blended (`_Alpha`) and unlit (`_NoLight`, DS3's sky domes). A kind with no
template found falls back to opaque. Meshes with no stand-in yet (water, additive glows, decals, fog cards, unlit
cloud layers) are left out and counted in the log ("meshes left out"). A DS3 material with no normal map gets a
flat one (`DKS_flat_n`). Fallbacks: `map-materials` B (albedo-only template) and `map-groups` C (no draw groups).

**Sky and lighting.** DS3's sky is ordinary map pieces (009xxx, a 9 km dome), so it is drawn with the map. The
sun, sky colours, fog and exposure come from the slot's gparam, which is still the colosseum's. Three things make
that sun and its ambient light reach a DS3 map (`Maps/MapLighting.cs`; up to 1.0.284 the maps were lit only by the
host's weak straight-down fill light, side faces black):

- Unlit DS3 materials get `g_GX_bDisableShadowCasting` in their MATBIN. Elden Ring's map pieces cast shadows
  whatever their layout flags say (332 of Stormveil's 343 pieces have IsShadowSrc off), so the dome around the map
  kept the sun off all of it.
- The layout carries the host's first EnvironmentMapEffectBox and EnvironmentMapPoint, the box grown to hold the
  played area and at least Stormveil's catch-all size (4500 x 4500 x 3500 m). The ported layout is built from
  scratch and had none; the colosseum's own box is 246 x 316 x 350 m around the arena.
- DS3's map lights (`mXX_XX_00_00_000N.btl`, version 6) are written as the slot's light file in the host's version
  (18): what a light is stays DS3's, and the fields that hold one value in every Stormveil and colosseum light take
  that value. DS3's directional light is left out. These are fill lights (m40 has 45, named "dynamic", nearly all
  inside Firelink Shrine); the light of candles, torches and bonfires comes from the flame effects of DS3's
  objects, which arrive with the object port.

Fallback `map-lighting` B (`--host-lighting`) turns all three off. Still the colosseum's: sun direction and
colour, fog, exposure (DS3's LightSet group has the same field names as Elden Ring's and each DS3 part names a
light set, so those can be carried over next), and what the environment map shows in reflections.

**Helper pieces.** DS3 keeps map pieces that are never drawn as scenery, and the port draws every piece it writes
as ordinary geometry, so up to 1.0.299 they stood in the maps (2026-11-11 playtest: blank slabs with leaf-shaped
holes in the High Wall, blurry doubles of rocks and ground in the Undead Settlement). `MsbPort.HelperKind` leaves
out reflection proxies (DrawOnlyReflectCam: low detail doubles of whole areas for water reflections; m40's m200000
and m200010), shadow casters (IsShadowOnly) and pieces off the map's base layer (MapStudioLayer bit 0, when nearly
every piece has it). Fallback `map-helper-pieces` B (`--helper-pieces`) keeps them. `Work\reports\map-pieces-<slot>.tsv`
lists every DS3 piece (position, flags, draw groups, size, each mesh's material type, kind and albedo) to name what
a screenshot shows. Not handled: DS3's draw groups (which piece is drawn from which collision; Elden Ring's test
looks like piece DrawGroups against collision DisplayGroups, each collision owning one bit in its second mask
struct, which is not what `--ds3-groups` writes), and far scenery (m800000 mountain tiles 1 km below the map, the
m9xxxxx views of other maps), which is unlit and gets no distance fog.

**Low-detail stand-ins.** DS3 shows a far area (a hall, a castle wall, a tower) as one blocky piece with a stretched
texture named `..._low_...`, drawn only from the collisions where the detailed pieces it stands for are not. With
every piece drawn from everywhere, map 2 showed those slabs and steps over its detailed scenery (2026-11-11 playtest
of 1.0.307). `MapStandIns` leaves out a piece that is mostly low texture when at least 3 detailed pieces stand inside
its box, none of them drawn from a collision that draws it, holding a quarter of its vertices. Measured on the real
layouts: m30_00's m900xxx stand-ins have 400 to 1,700 pieces they are never drawn with, while m30_00's m980xxx
(Lothric Castle's outside) and every low piece of m40_00 share a collision with all other pieces, so they stay.
The port log lists each judged piece; fallback `map-stand-ins` B (`--stand-ins`) keeps them all.

**Vertex alpha: overlays and two-layer ground.** DS3 lays paving and rubble over its ground as separate one-layer
meshes (M[ARSN]_l) whose vertex colour alpha fades them into what lies under them (m40's m000079 brick: 37% of its
vertices under 0.1, mean 0.28; solid cliffs and rocks have alpha 1 everywhere). Its two-layer materials (M[ARSN]_m,
_l_m, _p_m: `GXFlver_ColDifSpcBumpMulMask`) use the same alpha as the first layer's weight over the second
(`g_DiffuseTexture2` on UV 2): m000080's ash over brick has alpha 0 on every vertex, so DS3 shows only its ash. Up to
1.0.340 the port wrote every vertex solid and showed the first layer, so dark brick shards lay across the ash around
Firelink Shrine. `MapOverlays` keeps an overlay's alpha (Elden Ring's map pieces fade edges the same way), leaves out
its triangles whose three corners are all under 0.5, and splits a two-layer mesh into one mesh per layer, each
triangle going to the layer its corners mostly show (Elden Ring has no two-layer map shader in use to blend them:
one mesh in all of Stormveil names `[Mb2]`). Terrain blends (2m/3m/4m), grass, snow and water keep their alpha
untouched. The port log's `vertex alpha:` line counts both; fallback `map-overlays` B (`--solid-overlays`) restores
the old solid drawing (see below: the one-layer fade was later found not to be one and is off by default).

**Faded triangles are cut only over ground.** Cutting every faded triangle (1.0.353 to 1.0.37x) opened holes to the
sky wherever an overlay fades out over nothing: measured on m40 with every map piece placed by its MSB part, Firelink
Shrine's floor (m004600 ash tiling) has nothing under its centre, the m000053 cliffs nothing under their outer slopes,
and the m001200 tree trunk above Iudex Gundyr nothing behind its faded bark; 679 m² of holes around the shrine and
the circle. `MapOverlayBacking` cuts a faded triangle only when an opaque surface that doesn't fade itself lies within
0.5 m behind it (along its normal, 5 cm in front still counts) at its centre and near each corner, in every placement
of its model. The port collects faded triangles during its first read of each model, defers the 59 models that have
any, checks every placed model's surfaces against them in a second read, then converts the deferred models (about one
extra read of the map: 44 s for m40 on 4 cores). On m40, 158,502 of 431,313 faded triangles are cut and the holes
around the shrine and circle fall to 11 m² (mostly faded edges of the m001110 walls, not yet looked at).
The port log's `overlay backing:` line counts them; fallback `map-overlays` D (`--cut-faded-overlays`) cuts them all.

**One-layer vertex alpha is not a fade (1.0.383 playtest, now the default).** Whole meshes DS3 plainly draws have
alpha 0 on every vertex: Firelink Shrine's m004001 (a 32,705 vertex wall_103 mesh beside a 5,170 vertex mesh of the
same wall at alpha 1) and m004520 (3,016 vertices, wall_103), Undead Settlement's wooden houses (m000120, every triangle
cut in 1.0.383) and the Painted World's Stone_Standard walls (103 pieces lost). DS3's one-layer materials are opaque
(MTD BlendMode 0), so nothing there can hide them; the brick shards of 1.0.340 were terrain blends (the
`--tint-materials` test showed them blue), not overlays. So one-layer meshes are drawn whole and solid by default;
two-layer meshes are still split by layer. Fallback `map-overlays` C (`--fade-overlays`) restores the fade with the
backing test above.

**A shadow-only FLVER no longer takes its binder with it.** Many DS3 binders (Undead Settlement's m002050 trees, 354
placements, m002610/m002620 hanging corpses, m003200 iron cages) hold a second FLVER with only an `M[Shadow]` caster.
That FLVER converts to nothing, and up to 1.0.383 the "only effect, decal, water or shadow meshes" error dropped the
whole binder, drawable FLVER included. Now such a FLVER is left out on its own (port log `extra FLVERs:`), and a binder
is left out only when none of its FLVERs can be drawn.

**Terrain blends: one layer per triangle.** Most of DS3's ground and many walls use `M[ARSN]_2m/_3m/_4m`
(MultiBlend2/3/4_et1): two to four textures (`GSBlendMap_AlbedoMap_0..3`, each with its own tiling on UV 1) painted
by per-vertex weights in the third UV member, four normalized bytes. A mesh with N layers uses the first N - 1 bytes
(m40's 3m wall m900285 keeps the third at 0, every mesh keeps the fourth at 0, and they don't sum to one), and layer k
is painted over the layers below it with weight k - 1, sharpened by the edge texture. Up to 1.0.353 every such mesh
showed only layer 0, which around Firelink Shrine is the dark brick under the ash: a `--tint-materials` build coloured
every shard of map 1 as a terrain blend. `MapTerrainBlends` gives each triangle the highest layer whose weight averages
0.5 or more over its corners, and splits the mesh into one mesh per layer with that layer's textures and tiling.
The port log's `terrain blends:` line counts triangles per layer; fallback `map-blend-layers` B
(`--first-blend-layer`) shows the first layer everywhere again. Unlit or scrolling "2m" materials are not ground:
DS3's sky domes (`m009000` of m31 and m40, `M[A]_2m_NoLight_scroll`) cross-fade two cloud layers with the same bytes,
and splitting one drew blue sky shards over map 3 in 1.0.366, so they stay whole on their first layer.

**Atmosphere.** A slot used to keep its colosseum's gparam: Caelid's red volumetric fog lit at 1.6 on map 2,
Leyndell's golden colour grading (LUT 101) and fog out to 2 km on map 1. `MapAtmosphere` writes the slot's
`/param/drawparam/mAA_BB_0000.gparam.dcx` from the host's: the sun angle and the colours of directional lights 0 and
1 from DS3's light set 0 (the set nearly every DS3 piece names), with Elden Ring's strengths; the volumetric fog's
colours from DS3's fog 0 and its amounts from Stormveil (m10_00, first time of day), with the height fog starting
where one in twenty of the map's enemies and player points stand lower; colour grading off. Fallback
`map-atmosphere` B (`--host-atmosphere`) keeps the host's file.

**Environment light.** Elden Ring lights map pieces with light baked per environment map point: a light volume
(GIIV, four BC7 volume textures W/X/Y/Z) and a reflection cube (GILM, BC6H) per time of day and detail level, in
`map/mAA/mAA_BB_00_00/mAA_BB_00_00_envmap_NN_{high,middle,low}_00.tpfbnd.dcx`. The ambient box `MapLighting` copies from
the host spread the colosseum's over the whole DS3 map (measured with `map-diag envmap`: Caelid's volume averages
252 136 132, Leyndell's 252 227 171, Limgrave's 228 248 251), while characters light from the separate irradiance
volume (ivinfo): red map surfaces on map 2 under an untinted player. `MapEnvironment` writes the slot's binders as the
host's with every light volume one even grey (its brightest channel's mean) and every reflection cube one even grey of
its mean brightness. Map 2's DS3 textures were measured neutral (`map-diag colours`: vertex-weighted mean albedo
93 89 80). Fallback `map-env-light` B (`--host-env-light`) keeps the host's files.

**Baked light (DS3's shade).** DS3 bakes shade into per-piece lightmaps (`g_DOLTexture1/2`) and into light probes
(`.btpb`, read with SoulsFormatsNEXT's `BTPB`: first-order spherical harmonics in RGB plus a sun mask per probe,
14k to 434k probes 1.5 to 2 m apart). Elden Ring ships the DS3-style lightmap matbins but none of their shaders
(`map-diag lightvol`, 2026-11-12), so the lightmaps cannot be drawn. `MapBakedLight` spreads the probes over a light
volume of the map's own (environment point and box 900, `GIIV0900`, in every envmap binder, plus an ivinfo entry per
time of day). Measured on vanilla files: a light volume spans the environment box of its number (colosseum 120 x 172 x
156 texels over 246 x 350 x 316 m), texture rows go up, and each of W/X/Y/Z holds one coefficient (order 0, then the
x/y/z terms of order 1) with its colour as 128 + 127 * c / |c| and its size in alpha (W's colour never drops below
127; Y is 0.87 of W in open arenas, the ratio of the up term to order 0 for light from the sky). DS3's open sky is
scaled to the host volume's open-sky alpha. Unproven until seen in game: column and slice directions (fallback
`map-baked-light` C/D mirror them), that the smaller box with the higher UnkT34 wins over the catch-all box, and that
alpha is linear. Fallback `map-baked-light` B (`--flat-env-light`) leaves the volume out.

**Light areas (DS3's rooms).** One volume was not enough (Devin, 2026-11-13: still flat, lit by "the world base
lighting"). Elden Ring draws no sunlight indoors: rooms get light only from a baked volume, a reflection cube, SSAO
and lamps (M. A. Moniem, "Behind the Pretty Frames: Elden Ring"). Stormveil has 129 environment boxes, one per room or
courtyard, each with its own point, volume and cube; its rooms' cubes are 15 to 150 times darker than outdoors and
nested rooms carry a larger UnkT34 (0 catch-all, 11 to 34 rooms). DS3's layouts mark out the same rooms (m40 31 boxes,
m30 38, m31 28; UnkT09 = order, UnkT0A = shared point), in layout space like the parts. `MapLightAreas` turns every DS3
box into an Elden Ring box of the same number, size and turn, with a volume over the box (its own axes) baked from the
probes in it and a cube as bright as their median against open sky (m40: 0.02 in the undercroft to 0.9 in the
courtyards). The map-wide volume 900 drops to UnkT34 5, under the rooms. Unproven until seen in game: box-local volume
axes, that the larger UnkT34 wins, world-direction first-order terms. Fallback `map-light-areas` B (`--no-light-areas`).

### Sky

Elden Ring keeps a second layout beside every legacy map, index 99 (`m45_02_00_99.msb` for `m45_02_00_00`): the
map's distant view. It holds the sky (assets `AEG096_xxx`), the Erdtree (`AEG094_000` to `_010`; the burning one,
`AEG094_1xx`, is switched in by `LegacyDistantViewPartsReplaceParam` rows whose `TargetMapId` is the index-99 ID)
and far scenery, and the game loads it with the map although nothing in the map's own layout names it. That is why
the DS3 maps, whose layouts are built from scratch, still stood under Elden Ring's sky with the Erdtree behind
their houses (1.0.299 playtest). The colosseums have no `WorldMapLegacyConvParam` row, so they are not placed in
the open world and its distant view (`m60_00_00_99`) is not involved.

`MapDistantView` writes an empty layout in place of the slot's index-99 one; the sky that remains is DS3's own
dome (map piece `m009000`). The build report's `sky:` line says what the host's distant view drew. Fallback
`map-sky` B (`--host-sky`) leaves the host's distant view alone. Found by reading how JortPob edits
`m60_00_00_99` (it keeps only the `AEG096` assets there).

### What a map costs the game to hold (hitching)

2026-11-11 playtest of 1.0.299: large hitches on the High Wall (worst walking into Vordt's room), the Undead
Settlement close to unplayable, the Cemetery of Ash fine. Not measured in game (the PC was offline), so what
follows is read from the converter and from JortPob, and the first fix is the one deviation from both that scales
with a map's size:

- The texture binder's low detail entry (`name_l`) was the full texture a second time. JortPob's is half size
  (`Common/DDS.cs` Scale), and the PartsDrawParam row asked for full textures within 256 m, which is most of a DS3
  map. So every texture of a map sat in graphics memory twice at full size. `MapTextures.BuildBinder` now writes
  the low copy without its largest mip level (`DdsMips`: DS3's own bytes, nothing re-encoded), and the row asks
  for full textures within 128 m (`MapDrawParams.TextureDistance`).
- Fallback `map-texture-detail`: B halves the full copies too (`--half-textures`), C is 1.0.299's behaviour
  (`--full-low-textures`).

The port log now says what each map holds, so the next look at performance starts from numbers:
`texture memory mXX: N textures, X MB full detail + Y MB low detail`, `load: N models, meshes, vertices,
triangles`, and the collision and navmesh lines end with the megabytes the game has to read.

Not changed, and the next suspects if a map still hitches:

- Collision and navmesh are XML tagfiles, some 55 times the size of DS3's binary (a 4.7 MB file is 260 MB of XML
  the game parses). JortPob ships XML and calls binary "worse in engine" without saying how; fallback
  `map-collision` B wrote binary and was untested in game (binary is the default since 1.0.31x, see below).
- Every map piece draws from everywhere (no draw groups, unlimited draw distance). DS3 culls by draw group; DS3's
  groups written into Elden Ring's real group fields (`Maps/MapDrawGroups.cs`) were the default in 1.0.414-1.0.417
  and hid the wrong things in game, so they are fallback `map-groups` B now. Instead, pieces whose models carry DS3's
  lower detail triangle lists (LodLevel1/2 face sets; on map 3 about 60% and 20% of the full triangles) switch to them
  at vanilla Elden Ring's 30 / 60 m (`Maps/MapLod.cs`, a twin of each piece row 20 up, 9420 and on, fallback `map-lod`; the port log's
  `lower detail` lines).
- Enemies that can't find a path (the navmesh's links are a separate problem) ask again every few seconds.

2026-11-11, 1.0.304 playtest: the texture change did not fix it (the Undead Settlement still "really really bad",
occasional hitches on the other two). The port log's new numbers named the collision: 3.4 GB (Cemetery), 3.5 GB
(High Wall) and 7.5 GB (Undead Settlement) of XML tagfiles, largest files 252, 188 and 300 MB, against DS3's 28 MB
binder for the Cemetery. Textures (320 to 540 MB) and triangles (15 to 16 million) are about equal on all three
maps, and only the collision is twice as large on the map that runs worst. Since 1.0.31x collision and navmeshes
are binary tagfiles by default (the size of DS3's files, every triangle compared after the round trip); XML is
fallback `map-collision` B (`--xml-collision`). Whether Elden Ring takes binary tagfiles whose type information is
inline (its own keep it in the binder's `.compendium`) is what the next playtest answers: falling through the
floor on A means it doesn't.

### Event scripts

Both games run the same event system, and DarkScript3 documents every instruction of each (`ds3-common.emedf.json`,
`er-common.emedf.json`, downloaded at a pinned commit into the work cache), so the conversion is table-driven:

- Each DS3 instruction is matched to the Elden Ring one of the same name, or a known rename ("Object" became "Asset",
  "(De)activate Object" became "Change Asset Enable State", and so on). 261 of DS3's 360 instructions have one;
  `DarkSignPorter event-coverage <ds3.json> <er.json>` lists every pairing and what has none.
- Arguments are paired by name, then by what they hold (entity, flag, event), then by shared words; Elden Ring-only
  arguments keep DarkScript3's default.
- IDs move by what they are: the map's entities, events and flags become the slot's (14000500 -> 45000500), a warp or
  flag of another ported DS3 map goes to that map's slot, and DS3's global flags get new ones from the JortPob-style
  blocks (1030290000-1038290000, numbers 0-999). The assignments are saved in `Mods\ER\darksign_flags.tsv` and reused,
  so a save keeps its DS3 progress across rebuilds.
- Event parameters follow their argument, and values passed to an event move by what the event uses them for.
- A command with no Elden Ring form becomes a no-op on the same line (so skips and labels stay right). An event whose
  conditions can't convert is left out, with the lines that start it. A cutscene that warps plays and warps once the
  cutscene port converted it (see below); otherwise it becomes the warp alone, and one that doesn't warp is skipped.
- The report (`port-maps` log) counts events kept, instructions converted and the most common misses.
- Cutscenes (`Maps/CutscenePort.cs`, step "cutscenes" before "events"): every DS3 remo binder of a ported map
  (`/remo/sAA_BB_NNNN.remobnd.dcx`) becomes `/cutscene/sCC_DD_NNNN.cutscenebnd.dcx` under the slot's area and block
  (the Painted World's s45_00_0000 becomes s19_70_0000). Both games share the binder layout and MQB version; the
  Havok cut files go from 2014 packfiles to 2018 tagfiles with the spline data copied byte for byte (cameras' FOV
  float track included), the player retargeted to Elden Ring's cutscene skeleton, and ported parts renamed. MQB
  resources and events are kept only in the kinds and parameter shapes Elden Ring's own cutscenes use (lights,
  DS3 draw settings and some sound events are left out). Each gets a CutsceneMapIdParam row (PlayMapId = the slot).
  The scripts then play them: DS3's warping forms become "Play Cutscene to Player and Warp" (2002[11]), the others
  "Play Cutscene to Player"/"To All", playback flags 8 -> 16. A cutscene that didn't convert is left out as before.
  `cutscene-survey` and `cutscene-test` check the files on a PC; fallback map-cutscenes B turns it off.

- DS3's "IF Action Button in Area" already names an ActionButtonParam row, but a DS3 one (boss fog walls 4001800,
  Gundyr's sword 8900, Ariandel's talk prompt 9373); each gets an Elden Ring copy (74000000+) with DS3's fields where
  both games name them, from Elden Ring's fog row 10000 for DS3 fog prompts. DS3 warp destinations (Vordt's
  post-cutscene point 3002810) are regions, and Elden Ring warps land only on player parts, so each gets a player part
  with the region's ID (`RegionPort.AddWarpPoints`); before, Vordt's door sent the player to the map's start.
- DS3's "IF Action Button" conditions (doors, levers, chests, elevators, boss fog) become "IF Action Button in Area".
  Each distinct prompt gets an ActionButtonParam row (74000000+) in the mod's regulation, copied from row 6000 the way
  JortPob does, with DS3's dummy poly, reach and angle. The prompt text is Elden Ring's when the words match ("Open"),
  otherwise DS3's words are added to `menu_dlc02`'s ActionButtonText (74000000+). `Maps/ActionButtons.cs`.
- SpEffect IDs ("Set SpEffect", "Clear SpEffect", "IF Character Has SpEffect", written or passed to an event) go to
  the enemy port's rows, 700,000,000 + the DS3 ID (`ChrEffects.SpEffectId`), so scripted effect changes on ported
  enemies (Pontiff's sword auras 10640/10641, the Jailer's eye glow swap) find DS3's effect. The enemy params step
  first ports every SpEffect the ported maps' scripts and DS3's common events use (`MapEvents.ScriptSpEffects`), so
  the row exists even when no move casts it; an ID with no ported row keeps its DS3 ID. The `events:` log line of each
  map counts `SpEffects: N to ported rows, M kept (ids)`, and the enemy port notes `SpEffects the map scripts use ported`.

- DS3's online checks have single-player forms (2026-11-12): "Player Is Not In Own World" (IF/GOTO/END) is Elden Ring's
  "Player Is In World Type" (the values match), "IF Character Damaged By" is "IF Damage Type" with any damage type,
  "Number of Clients of Type" counts co-op clients for type 0 and treats invaders as none (the line becomes an
  unconditional skip/goto/end or a no-op), and "WAIT For Network Approval" is a no-op. Before this about half of each
  map's events were left out (m40 66 of 103, m45 52 of 112, DS3's common functions 119 of 200), among them every boss
  fight's start (common event 20005800), its phases and most doors; on the real scripts it is now m40 5, m30 0, m31 0,
  m45 1, m37 2 and common functions 1. Still left out: map ceremonies and "IF Object Backread". "IF Character
  DrawGroup State" counts as met when it shares a condition group with another check (Pontiff's 13705880).
- Bosses who talk (Sister Friede, Father Ariandel) carry a talk ID in DS3. Placements that share a boss's entity group
  and body are bosses too (`EnemyPort.BossEncounter`), so they are ported and placed as enemies; the same body
  elsewhere without the group (Friede in the chapel) stays a person and never takes the boss stages' ported rows.

Not converted yet: sound and BGM instructions, map ceremonies and world tendency. Doors, levers and lifts are ported
assets that move (see "Doors, lifts and levers"); their ObjActParam rows sit at 70,000,000 + the DS3 ID, and the
scripts' ObjAct arguments are moved there.

### Enemy placement and the enemy porter

The enemy porter (`Enemies/ChrPort.cs`) runs at the start of every full port-maps run, before any map places its
enemies, and writes `Mods\ER\darksign_chr_map.tsv`, tab-separated, one line per ported row:

```
npc <DS3 NpcParam ID> <ER model> <ER NpcParam ID> <ER ThinkParam ID> [name]   one DS3 NpcParam row
chr <DS3 model>       <ER model> <ER NpcParam ID> <ER ThinkParam ID> [name]   any row of a DS3 model
```

An `npc` line wins over a `chr` line. Lines starting with `#` are ignored. Until a character is listed it gets a stand-in.

What the enemy porter does, for every DS3 character placed in DS3's maps (one run, no list to keep up to date):

1. **Template.** The Elden Ring enemy (from the legacy-dungeon catalog) whose skeleton shares the most bone names with
   the DS3 model, with a small bonus for the same kind and boss-ness. Below 25% shared bones it uses method C.
2. **ID.** A new character ID from c7000 up, in DS3 ID order, skipping IDs Elden Ring has, so every PC gets the same.
3. **Files.** Every `/chr/` and `/sfx/` file of the template and its `action\script\cXXXX.hks` are copied under the new
   ID (without the script the behaviour graph's script loader asserts on a null buffer at spawn, 2026-11-08 evening), and so is the ID inside every
   Havok file's own strings (the behbnd's project data names its `Characters\cXXXX.hkx` and `Behaviors\cXXXX.hkx`,
   the character its skeleton and animations; the game resolves them by name when the behbnd loads, and with the
   template's ID left inside every spawn crashed right after the behbnd loaded, 2026-11-08). The FLVER becomes DS3's model
   (template header and GX list, skin indices into the node list, one tangent, vertex colours), each material a copy
   of the template's closest MATBIN pointed at DS3's textures (DLC02 material binder), DS3's textures go into the
   `_h` texbnd. Method A also gives the template skeleton DS3's bind pose and replaces each template animation that
   DS3 has (same aXXX_YYYYYY) with DS3's, retargeted by bone name, and merges the TAE: the template's behaviour
   windows and ID-based sounds/effects, scaled to DS3's length, plus DS3's events that fit Elden Ring's layouts.
4. **Params** (after port-weapons wrote regulation.bin): ChrModelParam for the new model, one NpcParam row per DS3
   row (new ID * 10000 and up, a copy of the template's named "[DS3] ..." with DS3's HP, souls, stamina and FP) and,
   for method A, DS3's BehaviorParam/AtkParam_Npc rows under a new variation.
5. **Report.** `Work\reports\enemy-port.tsv` (template, bone match, method, failures); the pre-flight check sums it up.
   Models that were planned and not built are a `FAIL` line in the build log and in `darksign_maps_check.txt`
   (builds 1.0.234-1.0.242 lost all 93 to an uncompressed behaviour script read as DCX and still read "0 FAIL").
   `darksign_spawn_list.tsv` carries a body column (`ds3`/`er`) so the F1 menu knows which entries wear their DS3
   model without guessing from the ID (Elden Ring ships its own c7000).

A character whose files fail falls back to C (the template's body with DS3's stats), never to nothing. The body
of a character on C is picked by what the enemy is (`ChrPort.PickBody`: same kind and boss-ness, else same kind, else
same boss-ness), not by bone names, and never from Elden Ring's wildlife or helper characters
(`EnemyKinds.IsBystander`: deer, eagles, dummies, bonfires, scarabs), which are left out of the enemy catalog. On
1.0.271 the Undead Settlement's Overgrown Skeletons were deer, the template with the most shared bone names. The written
files are listed in `darksign_chr_files.txt` and deleted at the start of the next run. Fallbacks `enemy-port`: A model
and animations, B model only, C Elden Ring bodies with DS3 stats, off. Offline checks: `DarkSignPorter enemy-selftest`.
Not yet ported: DS3's AI scripts (the template's ThinkParam/AI is used), sounds (the template's bank), boss events.

port-maps also writes `darksign_enemies.tsv` (every DS3 enemy it placed, with its slot part and entity) and
`darksign_er_enemies.tsv` (the Elden Ring enemies below) for the cross-game randomizer's enemy shuffle.

Stand-ins: port-maps reads every enemy placed in Elden Ring's legacy dungeons (Stranded Graveyard, Stormveil, Raya Lucaria,
Volcano Manor, Leyndell...) with its NpcParam name, sorts both games' names into kinds (knight, soldier, archer, mage,
skeleton, dog, rat, bird, crab, worm, serpent, lizard, tree, ghost, beast, giant, dragon) and takes the earliest
dungeon's enemy of the same kind, or the most common one when the kind has none. Bosses are spread over Elden Ring's
bosses (of the same kind when there is one). Stand-ins keep their Elden Ring stats; the player keeps Elden Ring's
jump, guard counter and the rest. Patrol routes and DS3's boss name text aren't ported yet.

### Objects, regions and map-file events

Stage 1 (2026-11-11, not yet playtested; written without game files, checked offline by `map-selftest`):

- **Regions** (`Maps/RegionPort.cs`). The converted event scripts wait on things only the map file holds: "is the
  player inside region 3002800", "warp to point 3002990", an enemy's patrol route. DS3's Event, ActivationArea,
  WarpPoint, Logic and Other regions become Elden Ring "Other" regions (JortPob's script region settings), spawn
  points stay spawn points, PatrolRoute and MovementPoint regions become patrol route points, all with DS3's shape,
  spot and entity ID moved to the slot. Left out and counted in the log: sound and effect emitters, wind, messages,
  muffling, invasion points and DS3's environment map points (their IDs name DS3 sounds, effects and text).
- **Map-file events.** PatrolInfo (enemies walk their DS3 routes), PlatoonInfo and Generator are carried, naming the
  ported enemy parts and regions. Treasure and ObjAct are not yet (stage 2).
- **Objects** (`Maps/ObjectPort.cs`). Every DS3 object model used by a map that has a slot gets a number in name
  order and becomes asset `AEG{first free group from 920}_{000-999}`, the same on every PC. The model is converted
  like a map piece (same cloned materials and vertex layouts); because an object's meshes hang off several nodes,
  each vertex is first moved into model space (a DS3 unskinned vertex is stored in the space of the node its NormalW
  names). Nodes and dummy points are kept. The objbnd's own textures go into the map texture binder. The asset
  binder, collision binder, asset part settings and AssetEnvironmentGeometryParam row (a copy of row 7077) follow
  JortPob (Bind.cs BindAsset, MakePart.cs Asset, Paramanager.cs GenerateAssetRows). Draw distance is by size
  (PartsDrawParam rows 9410-9415, JortPob's table with the distances doubled).
- **Collision** is ported only for objects that never move or break: no animations in the objbnd, no hit points in
  DS3's ObjectParam, not a ladder, not the target of an ObjAct. Doors, levers, lifts, crates and barrels are drawn
  but can be walked through until stage 2, so none of them can block the way. DS3's object collision is converted
  by the map collision converter; an object whose file holds no compressed mesh has none (the report says so).
- **Reports.** `Work\reports\object-port.tsv` has one line per object model: what its objbnd holds, what was
  converted, why it has or hasn't collision, whether it moves, breaks, is a ladder or an ObjAct target.
  `Mods\ER\darksign_objects.tsv` maps DS3 objects to assets. The port-maps log has "objects:", "regions:",
  "object port:" and "object params:" lines, and `darksign_maps_check.txt` checks every asset's file and row.
- **If a map breaks:** launcher Fallbacks `map-objects` B (no collision) or off, `map-regions` B.

Still missing after stage 1: object effects (candle and bonfire flames, torches), doors/levers/lifts (ObjAct events,
ObjActParam, object animations), ladders, bonfires as sites of grace, fog walls, breakable objects, treasure
(items lying in the map), what people say and sell (see below), sound regions.

### Doors, lifts and levers (objects that move)

2026-11-12, not yet playtested; checked offline by `map-selftest` ("doors: ...") and on DS3's o300310, o300320,
o300500, o300510 and o400340 against Elden Ring's door AEG219_000 in a cloud thread. Fallback `map-doors`.

- **Which objects.** Every DS3 object whose objbnd has an animation binder and that doesn't break or serve as a ladder
  (`ObjectPort.ConvertMoving`). They are converted one at a time after the still objects, because their animations go
  through HKLib under AnimPort's lock while the still objects' collision serializes lock-free.
- **Model.** The meshes stay on their nodes (`MapModelPort.KeepNodes` / `RestoreNodes`): every mesh becomes a plain
  one on node 0 with NormalW = the vertex's node, as Elden Ring's door AEG219_000 is built (one mesh, no bone list,
  the leaf's 5,600 vertices NormalW 1 = node "Model_AEG219_000_01"). DS3's root node ("o300310") is renamed to the
  asset name in the model, the skeleton and the collision alike; a model with no such node gets one added last.
- **Animations** (`Maps/ObjectAnims.cs`). Elden Ring's door binder layout: file 400 of the asset binder is an
  animation binder holding `hkx_compendium\skeleton.hkx` (1000000), `tae\AEGxxx_yyy.tae` (3000000), the compendium
  (7000000) and `hkx_compendium000_00000N.hkx` (1000000000 + ID). DS3's objbnd has the same pieces under the same IDs
  (Havok 2014). The skeleton is DS3's (bones, parents, rest pose) in Elden Ring's skeleton file; each animation goes
  through `AnimPort` onto that same skeleton (nothing to retarget); the TAE has one entry per animation in Elden
  Ring's format (SDT, bank 36). DS3's TAE events (the creak, dust) are left out: the objects move silently.
- **Collision.** Elden Ring's door collision has one body per part named after the bone it follows (the leaf is a box
  named "Model_AEG219_000_01"; the frame stays put). DS3's object collision is laid out the same way, so bodies
  named after a non-root bone take the leaf's settings (motion type 1, its motion properties, no motion ID:
  `ObjectAnims.MovingBodies`). DS3 convex hulls (door leaves, levers; 31 of the first build's still objects had only
  these and got no collision) now convert (`CollisionPort.ConvertConvex`). A body whose compressed mesh points past
  its vertices (the placeholder frames of o400340 and o300500) is left out instead of failing the file.
- **Hull arrays in the patch list (1.0.410 crash).** A convex hull keeps its vertices, planes, faces and indices in
  hkRelArrays. In a binary tagfile each one holds the number of the item with its elements, and the game resolves it
  into a count and a distance only if the file's patch list (PTCH) names the field; Elden Ring's door aeg219_000_l
  names all four. HKLib never listed them, so 1.0.410 crashed loading every DS3 map (eldenring.exe+0x188434b, the
  hull's set-up reading "vertices" on top of the shape). `HavokRelArrays` hooks HKLib's array writer once for the
  whole porter so every hkRelArray it writes is a patch, and `CollisionPort.Convert` refuses a file whose hull arrays
  the patch list misses (map-selftest "doors: a written convex hull lists ...").
- **Rows.** Moving assets get behaviorType 0 (Elden Ring's door and lift have collision and 0) and isMoveObj from
  DS3's ObjectParam (lifts: 304822, 342400, 404822...; Elden Ring's lift 219020 has it). Every DS3 ObjActParam row is
  copied at 70,000,000 + the DS3 ID (Elden Ring shares 49 of DS3's 118 IDs and has nothing in 70-75 million) as a
  copy of Elden Ring's door row 219000 with DS3's fields by name; item requirements (DS3's keys, not ported) are
  dropped, so locked doors open; the one-sided check (type 2, 4150) stays; prompt rows Elden Ring lacks become 7200
  (door) or 8301 (lever). The scripts' "Object Parameter ID" / "ObjAct Param ID" arguments move the same way
  (`EventPort.Kind.ObjAct`).
- **ObjAct events.** Placed for moving objects only, laid out like Stormveil's (no part/region/map of their own,
  UnkE0C 255, UnkS0C -1), naming the asset part, the ported row and DS3's state type (0 lever, 1 one-way door, 4
  two-way, 5 chest, the same values in both games). Their flag stays DS3's (marked "[DS3 flag] ") until the event step
  gives the scripts their flags; then `ObjectPort.RemapObjActFlags` moves it the same way, so "IF ObjAct Event Flag"
  and the ObjAct agree.
- **Lifts** need nothing more: DS3's scripts play their animations ("Force Animation Playback" on the lift's entity).
- **Unproven until a playtest:** that Elden Ring follows hierarchical FLVER nodes on an asset as DS3 did; that a convex
  polytope without a connectivity table collides; that the moving bodies follow the bones (a moving lift carries the
  player only if they do). Report columns in `object-port.tsv`: "moves in Elden Ring" (animations, bones, or why the
  object stands still), "lift"; the collision column counts moving bodies.

### People (stage 2)

DS3's people are enemy parts like any other: the human body `c0000` dressed by DS3's CharaInitParam, or a character
with a talk script (`TalkID`). Neither DS3's armour nor its dialogue is ported, so `EnemyPort.People` gives each one an
Elden Ring person, copied from a vanilla placement of `c0000` in the legacy dungeons (part, look, NpcParam, AI):

- Someone DS3 lets you talk to gets an Elden Ring person who has a talk script (so the game has them on your side),
  with the talk script taken off: they stand where DS3 put them and say nothing.
- A DS3 human without a talk script (invaders, hostile humans, summons) gets an Elden Ring human whose NpcParam team is
  one the ordinary enemies of the catalog are on. Summons and quest phantoms stay hidden as long as DS3's converted
  events hide them, as in DS3.
- A talker with its own body (Fire Keeper `c1400`, Ludleth) is placed only when the enemy porter ported that body.
- The pick is by DS3 NpcParam, so the same DS3 person is the same Elden Ring person everywhere.

The port log names every pairing (`enemies: people: Greirat -> ... (silent)`), and they are kept out of
`darksign_enemies.tsv` so the randomizer never swaps one for a monster. Known limits: they look like Elden Ring characters, they say and sell nothing, and killing one drops what
the Elden Ring person drops. Off switch: fallback `map-people` B (`port-maps --no-people`).

After the 1.0.326 playtest (no one in Firelink Shrine, Black Knights around it, no objects at all):

- **Seats.** DS3 parks most of Firelink Shrine's people at one spot about 40 m under the floor (273,-98,603) and
  its scripts warp each to their seat when the map loads (`Warp Character and Copy Floor`, mostly in parameterised
  events). `EnemyPort.EventWarps` reads every character warp in the map's script and common_func, arguments of
  parameterised events included; `EnemyPort.Seats` gives a seat to each person standing at a holding spot (three or
  more characters at one point) whose script warps them, on the collision of the nearest character within 60 m. Two
  versions of one person warped to the same seat: only the first stands. m40: 19 seats from 27 warps.
- **Own-body talkers** (Fire Keeper c1400, Ludleth c1450, c2160, c3190, c3200) are their ported body when the
  enemy porter made one, else an Elden Ring person. c1000 (DS3's invisible talk dummy at bonfires) gets nobody.
- **Map states.** DS3's m40 holds the Untended Graves at the same place as the Cemetery of Ash: its parts lack
  MapStudioLayer bit 0 (Black Knights c1300, Champion Gundyr, 40 enemies and 45 objects in all). Map pieces were
  already filtered that way (`MsbPort.HelperKind`); enemies and objects now are too (`MsbPort.OtherState`), m30 loses
  11 objects and m31 2 enemies and 1 object the same way. Fallback `map-layers` B (`--all-layers`) keeps them.
- **Objects lost in 1.0.326.** Every object was placed and then taken out again: Paramdex's def for
  AssetEnvironmentGeometryParam (ASSET_GEOMETORY_PARAM_ST) has the game's row size, 320, but data version 4, and
  SoulsFormats' careful apply also compares the version. `ParamStore` now applies a def whose type and row size
  match when only the version number differs, and says so in the log.
- **1.0.340 playtest.** The naked man at Firelink was Boc the Seamster standing in for the Fire Keeper: his
  CharaInitParam row (23420) has a hood and no body armour. People whose CharaInit row has no body armour (220 of
  3,273 rows) are now left out of the pools (`EnemyPort.PersonLooksWrong`, logged as `people: left out of the pools`).
  The "gold figures" were Tanith (orange dress, seated) and Knight Diallos (golden Hoslow armour) under the map's
  lighting, not a phantom look (no picked NpcParam row has a phantom shader). DS3's summons and invaders ("White
  Phantom", "Mad Phantom", "Dark Spirit") were placed as hostile Elden Ring invaders; on map 2 three of them became
  Inquisitor Ghiza, Maureen and Mary, who cast at the player at the start. They are no longer placed
  (`People.IsPhantom`), since DS3 only brings them in by summon sign or invasion.
- **1.0.353: map 2 still knocked the player down.** The start point itself was the cause: m30 has no x990 arrival
  point, so the first player part with an entity was used, 3000974, which DS3's scripts use for the Dancer fight and
  which stands half a metre from the Dancer of the Boreal Valley (c5270). `MsbPort.ChooseStart` now passes over any
  player part with an enemy of the starting state within 10 m (people and the c1000 dummy don't count). m30 now starts
  at the High Wall bonfire (3000970); m40 and m31 keep their starts. The port log's `start:` line names the point
  and the ones passed over.
- **No Elden Ring stand-ins for people (after 1.0.353).** Devin wants DS3's own people at Firelink or nobody. By
  default only people with a ported DS3 body (Fire Keeper, Ludleth...) are placed; DS3's human NPCs (c0000, built
  from DS3 armour) are left out until they can be ported. The Elden Ring stand-ins are fallback `map-people` B
  (`--er-people`), nobody at all is C (`--no-people`). DS3's logic characters (talk dummy c1000, invisible platoon
  leader c0100 "Patrol Leader - Collective Patrol", "... target dummy character") no longer get a visible Elden Ring
  enemy either (`People.IsLogic`): m30 and m31 had six Mausoleum Exile Soldiers and a crossbow skeleton that way.

## Open questions (answered by the first `map-survey` and in-game test)

1. Does WarpPlayer into m45_00 work offline, and does the arena load with our layout? Partly answered 2026-11-08:
   the warp happens (the save then holds m45_00 / m45_01 as the last map), and the load crashed. The map check of that
   build listed 6530 meshes in 2098 map piece models whose `mesh.NodeIndex` pointed past the FLVER's one node (DS3's
   exporter left Maya node indices there, up to 12; Dark Souls III ignores them). Ported map pieces are now laid out
   the way JortPob builds Elden Ring map pieces (one root node, every mesh on node 0, no per-mesh bone list, NormalW
   0; `MapModelPort.MakeStatic`). The same build also lost 15 Cemetery of Ash collisions to HKLib's serializers
   running on several threads at once (now under the shared Havok lock), which left the run start 745 m from the
   nearest collision. A save left inside a crashing map loads nothing else: the launcher's **Rescue save…** button
   starts the game once with the ported maps moved out of the mod folder (`SaveRescue`), so the vanilla colosseum
   loads under the same ID and Ctrl+Shift+F2 (or a death) gets the character back; the next PLAY restores the maps.
   Only the layouts and map folders move: the slots' empty event scripts stay, because with the vanilla arena script
   running the player was left in the colosseum lobby state (enemies ignored them and took no damage until a restart,
   1.0.221 test). A save in a new-ID slot (m50-m52) has no vanilla map to fall back to.
   The node fix did not stop the crash (same `eldenring.exe+0x18C038A` on 1.0.230). The two minidumps show the real
   cause: the game's hknpPhysicsSystem constructor looks every body's `m_motionPropertiesId` up in the system's
   `m_motionProperties` array unless it is 0xFFFF, without a bounds check. Our bodies took the id (0) from the vanilla
   host collision's body while DS3 static collisions ship no motion properties, so the game read a null array right
   after the h45/l45 binders loaded. `CollisionPort` now carries the host's motion properties over when the DS3 file
   has none and points every body id that still falls outside its array at nothing (0xFFFF for motion properties,
   material 0), with a self-test for each case.
2. ~~Do Elden Ring map textures live in `map/m45/m45_0000.tpfbhd` like DS3's?~~ No (2026-11-08 test): Elden Ring has none, its map textures are in `asset/aet`. Textures now go to `map/m45/common/m45_0000.tpfbhd` the way JortPob ships them.
3. Does the game accept collision as XML tagfiles inside the binder (JortPob says yes), or does it need binary?
4. Is the simd tree needed (`--simd` keeps DS3's)?
5. Do the cloned map materials draw the DS3 textures correctly (opaque, alpha, two-layer)?
6. ~~Does Elden Ring need anything besides the .nvmhktbnd for a navmesh (an MSB entry or an .nva file)?~~ Yes: the .nva list (2026-11-11, see "Navigation"). The colosseum's own list named none of DS3's navmeshes, so the game loaded none.
7. ~~Are the port's flag blocks allocated?~~ Answered 2026-11-08, in two steps. Elden Ring's map registry is
   `map/worldmsblist.worldloadlistlist.dcx` (+ `_dlc01`, `_dlc02`): plain text, one `map:/MapStudio/mAA_BB_CC_DD.msb `
   line per map the game will load (m45_00-02 included, m50 not; an unlisted ID sat on the loading screen for ever
   and the game asked for none of its files). The flag lists `event/eventflag/{legacymap,openmap}[_dlc02].eventflagalloclist.dcx`
   are plain text too (`index,mAA_BB_CC_DD,type`, indices 0-148, 150-186, 200-332, 350-358) but don't name the
   colosseums, whose flags the game keeps anyway, so the registry decides a map's flag blocks. The in-game check
   (dksign_testmenu.dll `flagcheck:` lines, shown by the pre-flight) found blocks 45000-45029 kept except 45006,
   45016, 45026, the m50 slot's none, and the JortPob-style sea-tile blocks kept. Registering m50 in that list
   (PR #95) then crashed the game at boot on every launch of 1.0.238 (eldenring.exe+0xAED000, a null per-map entry):
   the executable keeps its own table of map IDs, and the registry only selects among them. So port-maps never writes
   the registry, skips a slot whose ID the shipped registry doesn't name, and map-slots B uses registered IDs the
   single-player game never visits (m11_70, m11_72, m19_70; each ships a small map of its own, read from the archive
   index on 2026-11-12, so they are not file-less as first thought). The template's other per-map files (`NewIdFiles.Copy`) and flag-list lines
   (`FlagAlloc.WriteForNewIds`) are still copied under such a slot. See `docs/maps/own-map-ids.md`.
8. The vanilla colosseum's other per-map files (`.mfr`, envmaps, ivinfo, `map/onav`, `breakgeom`) stay as they are
   and describe the colosseum, not the DS3 map. (`.btl` lights and the `.nva` navmesh list are the port's own now.)
   `map/onav` is the map's occlusion graph (Smithbox: read by MSPOcclusionGraph, empty in most maps); the port-maps
   log doesn't describe the colosseum's yet.

A full `port-maps` run first deletes everything it wrote last time (both slot methods) and builds from Elden Ring's own
common.emevd, common_func.emevd and menu binder with a fresh flag table, so every PC gets the same output; a `--only`
run is a partial rebuild that keeps the rest.

## Navigation (how enemies find their way)

2026-11-11, playtest of 1.0.299: "enemies don't seem to correctly pathfind on DS3 maps". Cause: Elden Ring loads a
navmesh only when the map's navmesh list (`.nva`) names it. The port wrote DS3's navmeshes into the binder and left
the list the host colosseum's, which names the colosseum's own models. No DS3 navmesh was ever loaded, so every
enemy ran straight at the player.

What the port does now, for every DS3 map (`MapNavmesh.Port`):

| | DS3 | Elden Ring, as written |
|---|---|---|
| List entry name | NameID = (AA × 10 + BB) × 1,000,000 + ModelID | NameID = AABBCC × 1000 + position in the list |
| Sets | one | n (first half) and o (second half, NameID + 2,000,000,000, large characters); the o set is a copy of the n set |
| Placement | each entry has a position, rotation (radians, Y × Z × X) and scale; the mesh is in its own space | entries at the identity; the mesh's vertices are moved into map space |
| File contents | hkaiNavMesh, a bare hkcdStaticAabbTree, a cluster graph, jump/drop link setups | hkaiNavMesh and an hkaiStaticTreeNavMeshQueryMediator whose face tree is built over the moved faces |
| Connections between navmeshes | list connectors: pairs of (face, edge) by index | the same pairs, with the entries' new NameIDs, once per set |
| Gate nodes | per navmesh, 16 walking-cost slots, navmeshes named by list position | carried over; the o set's point at the o half |
| Binder | `n40_00_00_00_NNNNNN.hkx` | `n45_00_00_00_NNNNNN.hkx` from ID 10000 and `o45_00_00_00_NNNNNN.hkx` from ID 20000, in the host's folder and format |

Sources: Smithbox's Navigation tool (the rules above, from its survey of every map of both games) and JortPob (the
smallest list and binder Elden Ring accepts); see CREDITS.md. The list reader and writer is Smithbox's `NVA.cs`
(`Porter/third_party/Smithbox-NVA`).

Decisions and what they cost:

- **Face data is cleared** (every face is plain ground). Elden Ring reads face bits as impassable (0x1), gated
  (0x10000, 0x100000), lava, ladder and more; what DS3's bits are is unknown, and a DS3 mark read as "impassable"
  would wall enemies in. The log lists DS3's values per map (`navmesh: DS3 face data ...`), and fallback
  `map-navmesh` B keeps them, to find out.
- **Node banks are left out**: the faces DS3's events close and open (doors, fog). Enemies may try to path through a
  closed door.
- **Jump and drop links are left out**: DS3 keeps them as link setups the game turns into edges on load; Elden Ring
  wants them as user edges in the mesh. Enemies won't drop off ledges after the player.
- **Joins to other maps are left out** (level connectors); each ported map is on its own.
- A DS3 map with a MapOffset event has list entries in world space and parts in map space. The port tries both and
  keeps the one that puts more of DS3's characters over a navmesh face (logged).

Evidence in the port-maps log (`Work\logs\port-maps-*.txt`), per map, under `navmesh:`: the summary (navmeshes, faces,
connections, gate nodes), two placement checks on the map's own data (gate nodes on their navmesh's edges; DS3's
characters over a navmesh face), DS3's face data values, and a description of the host's own binder and list and of
DS3's list (entry values, sections, root objects, face and edge data), so the next question about either game's
files can be answered from the log. `map-validate` reads the written pair back (entries named by position, every
entry's file present, face counts and face trees of sampled files).

Not proven in game yet (first build with it: the one after 1.0.299). If enemies still don't find their way:

1. Read the placement checks in the log. Low numbers mean the navmeshes aren't where the map is (rotation order or
   map offset), not that the game rejects them.
2. Compare the "host" lines with what the port writes (root objects, face/edge data striding, binder IDs and
   names, the list's sections). The port takes the host's values where it reads them, but nothing has been compared
   by eye, because the cloud threads can't open the game's files.
3. If the files are right and the game still doesn't use them, generate the navmeshes with the game's own Havok code
   instead (ERNavmeshGen, as JortPob does) from the ported collision, and keep this list code for the result.

## Map effects (candles, fog, light shafts)

DS3 places its ambient effects as SFX regions: an effect ID at a spot. Firelink Shrine's candle flames are five of
them (effect 840060, entities 4003500-4003504), next to fog (833002, 840040) and light shafts (840031); the High Wall
has 238 SFX regions naming 22 effects and the Undead Settlement 76 naming 15. Up to 1.0.366 RegionPort left them out,
so Firelink had its torches (objects) but no candle light.

`MapSfx` ports them in two steps. port-weapons adds every effect the maps' SFX regions name to the effects it
converts into Elden Ring's common effect binder (900,000,000 + DS3 ID) and writes which converted to
`Work\map-sfx.tsv`. port-maps, after waiting for port-weapons, adds an Elden Ring SFX region for each DS3 one whose
effect converted, with DS3's spot, shape, start state and moved entity ID. Unproven in game: that a map SFX region
plays an effect from the resident common binder. Fallback `map-effects` B (DARKSIGN_MAP_SFX=0) turns them off.
WindSFX regions (they need DS3's wind areas) are still left out.

## DS3 people in their DS3 armour

DS3's NPCs and hostile humans are the human body c0000 dressed by a CharaInitParam row: 23 at Firelink (Hawkwood,
Sirris, Irina, the Sword Master...), 4 on the High Wall, 11 in the Undead Settlement. `PeopleArmour` ports their
looks:

- Each armour model (parts/hd|bd|am|lg_m|f_NNNN and the _l copy) is converted on plain character materials
  (ChrModel), with its textures, into Elden Ring's binder layout (TPF 100, FLVER 200, read off bd_m_1010) under a new
  model number from 5000 (Elden Ring's end at 3230). Both the male and female file are written.
- Each piece gets an EquipParamProtector row 100,000,000 + its DS3 ID, a copy of the most common Elden Ring row of
  its slot (equipModelCategory 5 head, 2 body, 1 arms, 6 legs) naming the new model.
- Each person stands as the Elden Ring person who would have stood in for them (face, NpcParam, AI), with a
  CharaInitParam row 3,000,000 + the DS3 row: a copy of that person's own row wearing the DS3 pieces, with DS3's sex.

The rows are added after port-weapons; if they can't be written the people go back to their stand-in's own row.
Unproven in game: that a DS3 armour FLVER, skinned to DS3's c0000 bones, binds to Elden Ring's body. Fallback
`map-people` D (DARKSIGN_PEOPLE_ARMOUR=0) leaves DS3's humans out again. Weapons are still the stand-in's.

## Next stages

1. Navmesh: converted since PR #44, loaded by the game since the list is written too (see "Navigation"). If Elden Ring rejects converted navmeshes, generate them with the game's own Havok code (ERNavmeshGen, as JortPob does) or with Recast (NavMeshStudio, Smithbox). Then: DS3's jump and drop links as user edges, and the faces events close and open (node banks).
2. Enemies: placed, and ported by the enemy porter (see above). Next: DS3 AI scripts and sounds per character, objects, items and bonfires from the MSB.
3. DS3 map events: converted (see "Event scripts"). Next: action buttons (doors, levers, fog walls) as
   ActionButtonParam rows, map-to-map connect collisions, then cutscenes (remo files, camera and animations via Havok).
4. More slots for the remaining DS3 maps.
