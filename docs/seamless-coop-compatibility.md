# Seamless Co-op compatibility

Checked 2026-11-04 against Seamless Co-op by LukeYui (Elden Ring: `ersc.dll`, Dark Souls III: `ds3sc.dll`).
Seamless is closed source, so this is based on its published docs, its config files, me3's source, and how The Dark Sign itself works.

## Short answer

The Dark Sign plays inside **Elden Ring**, so co-op for the overhaul means **Elden Ring Seamless Co-op**.
Dark Souls III is only read as source data. Its Seamless mod only needs to keep working on its own, and this PR makes sure it does.

| Area | Status | Notes |
|---|---|---|
| Loader (me3 vs `ersc_launcher.exe`) | OK | me3 has supported Seamless since v0.6.0 (we pin v0.13.0). It loads `ersc.dll` as a `[[natives]]` entry through its ModEngine2 shim (`modengine_ext_init`). Seamless's own launcher is not used. |
| Offline block in our save-redirect DLL | **Fixed here** | It blocked every non-loopback socket. Seamless reaches the other players through Steam networking inside the game process, so co-op could never connect. Co-op mode (`DKSIGN_COOP=1`) now leaves sockets open. FromSoftware's servers stay off through me3's offline start (`start_online` stays false) and through Seamless itself. |
| Save files (`.co2`) | OK, isolated | Seamless renames the save to `ER0000.co2` (its `[SAVE] save_file_extension`). Our redirect moves the whole save folder, so a co-op save lands in `TheDarkSign\Saves\<profile>\ER\<steamid>\ER0000.co2`. The player's real Seamless save in AppData is never touched. A unit test covers this. |
| Dark Souls III Seamless run by hand | **Fixed here** | If our `dinput8.dll` is in the DS3 folder, a normal `ds3sc_launcher.exe` run would have been redirected and blocked from going online. The DLL now steps aside (dinput8 only) when it was not started by our launcher and Seamless is installed next to the exe. |
| `regulation.bin` must match | **Added a check** | Seamless requires every player to have the same `regulation.bin`. Each player builds the mod from their own files, so the launcher now shows a short **mod code** (first 8 hex digits of the regulation's SHA-256). Friends compare codes before playing. The same game version plus the same launcher release should give the same code. The parallel build is meant to be deterministic, and comparing codes is how we confirm it. |
| Password and settings | Player's own | Seamless reads `ersc_settings.ini` next to `ersc.dll`. The launcher only reads it, to warn when `cooppassword` is empty. The host's ini decides scaling, invasions and spirit summons. |
| Distribution | OK | We never bundle Seamless. The player ticks "Seamless Co-op" and picks the `ersc.dll` from their own Nexus download. |
| Uninstall | OK | Nothing of Seamless is copied into `TheDarkSign`, so Uninstall never touches it. |

## What still needs an in-game test (two PCs, both on the same mod code)

1. **Boot and connect.** Launch with Seamless ticked on both PCs. `dksign_saveredirect.log` should say `co-op mode, sockets left open`. Use the Seamless item to host and join.
2. **Ported weapons.** Equip a DS3 weapon, for example Friede's Great Scythe. The partner should see the model, the DS3 moveset and the sounds. Seamless only syncs IDs, and both players have the same files.
3. **`start_online`.** If Seamless refuses to connect while me3 starts the game in offline mode, the launcher should write `start_online = true` in co-op mode only. The me3 docs' own Seamless example sets it. Our winsock block is already off in co-op mode, so this would be a one-line change.
4. **DS3 maps (colosseum slots m45_00–02, m50 probe).** These are vanilla multiplayer arena slots. Test whether a client can follow the host there with the F1 "DS3 run" warp, and whether Seamless's player-location sync is fine with the new map IDs.
5. **Event flags.** Seamless shares the host's world. Our run flags (`1039290100`, the `10392921xx/22xx` requests) and the converted DS3 flags (`1030290000`–`1038290000`) are set by the host's scripts. Test whether a client's own save picks up DS3 boss kills the way it does vanilla boss kills.
6. **F1 test menu hooks.** Both mods hook game code. Watch for a "Could not find signature" or a crash on load. If one appears, try Seamless last in the natives list.

## Rules for future work (so co-op keeps working)

- **Deterministic builds.** Never put timestamps, random numbers or hash-set ordering into anything that lands in `regulation.bin`, `.emevd`, `.msb` or the TAEs. Same inputs must give the same bytes.
- **Param and flag ranges.** Seamless adds its own items at runtime. Keep our rows in our own high blocks (weapons 8xxxxxxx, ActionButton 74000000+, flags 10392xxxxx) and never reuse vanilla multiplayer items, such as the Furlcalling Finger or the effigy goods.
- **Never touch the game folder.** Seamless lives there in the player's own install.
- **Randomizer (next thread).** Seamless's guide notes that randomizers can fail to merge with map-editing mods. The randomizer must write into our own build, in one deterministic pass seeded by a shared seed, so both players get the same mod code. Done as co-op codes: everyone keeps their own seed, and friends paste the host's code to load the host's world for that session; the mod code then includes the world (see `docs/randomizer/randomizer-plan.md`, "Playing together").

## Sources

- Seamless Co-op modding guide: https://ersc-docs.github.io/seamless-modding/ (external_dlls load, one regulation.bin, `save_file_extension`, `cooppassword`, all players need the same mods)
- Seamless Co-op FAQ: https://ersc-docs.github.io/faq (.co2 saves, same regulation.bin, Steam networking)
- me3 source, garyttierney/me3 (MIT/Apache-2.0): NEWS.md v0.6.0 "Compatibility with Seamless Coop"; `crates/mod-host/src/host.rs` ModEngine2 shim; `schemas/mod-profile.md` `start_online`, `load_early`
- me3 discussion #118: https://github.com/garyttierney/me3/discussions/118
