# Cross-game randomizer: Dark Souls III + Elden Ring

Goal (Devin, 2026-11-04): once the DS3 maps work inside Elden Ring, randomize fog walls, items, enemies and bosses
across both games, in a way that hasn't been done before. Two rules from Devin:

1. **Progression and finishing the game must always stay possible.** Every seed is proven finishable by a solver
   before it is used.
2. **Randomization is per save, not one global rebuild.** Each launcher profile (each with its own save folder) has
   its own seed and options, so each character gets its own world.

## What is new here

Existing randomizers (thefifthmatt's DS3 and Elden Ring item/enemy randomizers and fog gate randomizers) each work
inside one game. Because The Dark Sign runs DS3's maps inside Elden Ring, this one treats both games as **one world**:

- **Fog walls cross games.** Walking through Vordt's fog in the High Wall can bring you out at the door of Stormfoot
  Catacombs, and an Elden Ring dungeon door can open into Undead Settlement. Every pairing is two-way. How often a
  fog crosses games is a setting (default 50%).
- **Key items cross games.** The Dectus Medallion can be in the High Wall; the Small Lothric Banner can be in
  Limgrave. The solver knows both games' locks.
- **Shared shards.** Elden Ring's Great Rune bearers and DS3's Lords of Cinder count for both games' gates: Leyndell's
  door opens after any two (Godrick and Abyss Watchers will do), and the Firelink thrones after any four.
- **Bosses cross games.** Elden Ring bosses appear in DS3 arenas and, once the enemy port has brought DS3 bosses into
  Elden Ring, DS3 bosses appear in Elden Ring arenas. Each is scaled to the arena's place in the run.
- **One goal across both games.** By default, beat both the Elden Beast and the Soul of Cinder.

## How it is built

| Piece | Where | What it does |
|---|---|---|
| World data, DS3 | `Randomizer/data/ds3/` | FogMod's `fog.txt` and `locations.txt` (Apache-2.0, unchanged) plus `ds3-overrides.yaml` (bosses, shard areas, pinned keys, item names) |
| World data, Elden Ring | `Randomizer/data/er/er-world.yaml` | Our own: 59 areas plus 48 minor dungeons, with tiers, routes and their locks, 16 boss fog walls, 7 key items, 26 main bosses and 48 dungeon bosses |
| Logic | `Randomizer/src/DarkSign.Randomizer/Logic` | Conditions (`AND`, `OR`, `2 OF ...`), the world model, the solver, the seeded RNG |
| Generation | `.../Generate` | World assembly, fog wall pairing, item placement, boss and enemy shuffles, plan and spoiler log |
| Porter commands | `Porter/src/DarkSign.Porter/Rando` | `randomize` (one profile's world) and `rando-survey` (Elden Ring data the item scanner needs) |
| Launcher | `Randomizer…` button, `RandomizerWindow.cs`, `DarkSign.Core/RandomizerSettings.cs` | Per-profile settings and "New randomized character" |
| Tests | `Randomizer/tests` | Hundreds of seeds over 9 setups, each re-verified from its plan alone; determinism; verifier catches a broken plan |

### The progression guarantee

1. **Conservative logic.** When unsure whether a route needs a key or a boss, the data requires it. Stricter logic
   can only place keys earlier than needed; looser logic could make a seed unbeatable. (Example: Liurnia requires
   beating Godrick, though a skip exists.)
2. **Assumed fill.** Each key item goes somewhere reachable while assuming the player owns every key not yet placed.
   No key can end up behind itself.
3. **The solver.** A sweep from the start follows every route whose condition holds and picks up every key it
   reaches, until nothing changes. Whatever is reached stays reachable: Elden Ring warps to any reached grace, and in
   the DS3 maps (whose bonfires aren't warp targets yet) the F1 menu's "Leave DS3 run" always returns to Roundtable
   Hold, from where the same route can be walked again (conditions only get easier). So the sweep is what a player
   can do, and no one-way drop can trap them. A seed is accepted only if the sweep reaches the goal,
   every key item, and (by default) every area and item location.
4. **Re-verification.** The plan is checked again from scratch (`Generator.Verify`) before it is written, and the
   tests do the same for every seed. A failing seed is rerolled with the next sub-seed, so it stays deterministic.
5. **Bosses never affect logic.** An arena keeps its own defeat flag, drops and fog walls, so beating whoever stands
   in it counts as beating the arena's boss.

### Fog wall pairing

Sides are paired outward from the start: each step joins a side the player can reach to a side in a part of the world
nobody can reach yet. Inner sides (boss arenas and minor dungeons, which are dead ends) only pair with outer sides,
dead ends are joined last, and a repair step re-routes an existing link when the open sides run out. With today's
three ported DS3 maps a seed needs on average fewer than one reroll; with all of DS3, almost never.

### Per-profile worlds

- The launcher stores each profile's settings in `launcher_settings.json` and writes
  `TheDarkSign\Randomizer\<profile>\options.json`; the porter's `randomize` writes `plan.json` and `spoiler.txt` there.
- It takes seconds and reads no game files. It runs again after a content rebuild (the world depends on which DS3
  maps are ported) and when the settings change, never when switching profiles.
- The apply stage will put each profile's changed game files in `TheDarkSign\Mods\ER-Rando\<profile>`. The launcher
  adds that folder to the me3 profile as a second package with `load_after = [{ id = "darksign" }]`; me3 scans
  packages in load order and a later package's file replaces an earlier one's (`mod-host-assets/src/mapping.rs`),
  so the profile's files win over the shared content mod.
- Limit: the 10 character slots inside one save file share one world, because the game loads these files at boot,
  before a character is chosen. "New randomized character" therefore makes a new profile. A later DLL stage could
  pick the world when a character loads, so characters in the same save could differ too.

### Playing together (Seamless Co-op)

Every player keeps their own seed. Both games read the world from files at boot and Seamless Co-op needs the same
files on every PC in a session, so playing together means everyone loads the host's world for that launch:

- **Co-op code.** The randomizer dialog shows the profile's code (`RandomizerCoop.Encode`: version, toggles, goal,
  cross-game ratios, the 64-bit seed and a checksum byte, as 23 Crockford base32 characters such as
  `0K3F-9QXA-…`). Lower case, spaces, O for 0 and I/L for 1 are accepted.
- **Joining.** A friend pastes the code under "Friend's co-op code". The profile's `JoinCode` is set, and
  `RandomizerCoop.ActiveWorld` makes that profile load the world `Co-op <code>` (its own folder under
  `TheDarkSign\Randomizer`, made once, reused next time) instead of its own. The friend's own seed, character and
  saves are untouched; "Back to my world" clears the code.
- **Same code, same world.** The randomizer is deterministic across processes and cultures (string sorts are
  ordinal, collections are enumerated in insertion order, JSON line endings are normalized before hashing). The
  porter seals each plan with a `WorldId` (8 hex digits of the plan's SHA-256), and the launcher folds it into the
  **mod code** (`RandomizerCoop.CheckCode`). Everyone in the same world on the same build sees the same mod code; a
  different launcher version (different ported maps) or a different world shows a different one.
- Tests: `SameWorldOnAnyPcWhateverItsLanguage`, `FriendsCoopCodeMakesTheHostsWorld`, `RandomizerCoopTests`.
- Later: a DLL could exchange the world code over the Seamless session and swap worlds without pasting a code. When
  the apply stage writes `Mods\ER-Rando\<world>`, the mod code must also hash that folder's `regulation.bin`.

## Stages

1. **Done (this PR): logic, data, solver, preview.** The launcher's Randomizer button makes a checked plan and a
   spoiler log per profile. Nothing in the game changes yet. Elden Ring item locations are stand-ins (one per area)
   until the scanner exists.
2. **Item scanner.** Run `DarkSignPorter rando-survey` on the PC; from its output, build the scanner that turns Elden
   Ring's map item lots into locations, placing each by its nearest Site of Grace's region (grace-menu tab) or by the
   grace names listed in `er-world.yaml`. Unknown names are reported and left vanilla.
3. **Apply items (Elden Ring only).** Move item lot contents (keeping each location's pickup flag) in the profile's
   regulation.bin. DS3 locations join once the map port places DS3 treasures; DS3 key items join once they exist as
   Elden Ring goods and the converted events check the new IDs.
4. **Apply fog walls.** Needs the map port's fog wall entities and working warps: each randomized side becomes a warp
   event (FogMod's approach), with the arrival point at the partner side. Rule for every apply stage: anything not
   applied yet is planned as vanilla (e.g. items applied before fog walls means the logic uses vanilla fog walls), so
   a plan never relies on a change that isn't in the game.
5. **Apply bosses and enemies.** Elden Ring bosses into DS3 arenas first (they already exist in Elden Ring), with
   SpEffect scaling by tier; DS3 bosses once the enemy port lists them (`ds3-overrides.yaml` gets their models).
6. **Per-character worlds in one save (optional).** A DLL that picks the overlay when a character loads.

## Open questions

- Ported DS3 bonfires are registered but not warp targets yet (map port, 2026-11-04). The guarantee relies on the
  F1 "Leave DS3 run" warp instead; if that warp is ever removed, DS3 one-way drops need return routes in the logic.
- Elden Ring region tab names for the grace menu (checked by `rando-survey`).
- Which Elden Ring fog walls can safely warp (some boss fogs are tied to arena scripts).

## Credits

- DS3 world data and the fog gate approach: thefifthmatt's [FogMod](https://github.com/thefifthmatt/FogMod)
  (Apache-2.0), see `Randomizer/data/ds3/NOTICE.md`.
- Item placement ideas (assumed fill, silos): thefifthmatt's randomizer documentation. SoulsRandomizers' code and data
  are source-available but not licensed for reuse in distributed tools, so none of it is copied; ours is written fresh.
