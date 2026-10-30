# The Dark Sign

Private, single-player cross-game overhaul for Dark Souls Remastered, Dark Souls III, Bloodborne and Elden Ring.
`project_manifest.json` tracks what is built.

## Folder layout

```
D:\The Dark sign\
  Launcher\                 the launcher (TheDarkSignLauncher.exe)
  <Game folder>\            one folder per game, each with its exe inside
  TheDarkSign\Saves\        isolated saves: <profile>\<DSR|DS3|BB|ER>\*.dksign
```

## Launcher (Phase 1)

Build and install into the project folder (needs the .NET 8 SDK on Windows):

```
dotnet publish Launcher/src/DarkSign.Launcher -c Release -o "D:\The Dark sign\Launcher"
```

The launcher treats the parent of its `Launcher` folder as the project root, finds each game's exe in the root's
subfolders, and refuses to launch anything outside the root. Settings live in `launcher_settings.json` next to the exe.

## Save isolation

- **Dark Souls Remastered, Dark Souls III, Elden Ring:** `SaveRedirect/` builds `dksign_saveredirect.dll`
  (`make -C SaveRedirect`, needs mingw-w64). The launcher carries it inside its exe and installs it next to each
  game exe as `dinput8.dll`, which those games load at start. It hooks the file APIs so the vanilla save folder
  (`%APPDATA%\DarkSoulsIII`, `%APPDATA%\EldenRing`, `Documents\NBGI\DARK SOULS REMASTERED`) is served from
  `DKSIGN_SAVE_DIR` with `.sl2` renamed to `.dksign`. Started by hand, the game still saves to
  `TheDarkSign\Saves\Default`. Each run writes `dksign_saveredirect.log` into the save folder.
- **Bloodborne (bbport):** bbport takes its saves folder from `%APPDATA%\bbport-launcher\settings.json`, which
  outranks environment variables. Each profile gets its own copy in `TheDarkSign\Config\<profile>\BB` (started from
  the player's own file, which is only read) with `user_dir` set to the profile's save folder and `online` off, and
  the game starts with `APPDATA` pointing there. Saves keep their PS4 file names, which the port needs.

## Offline only

The save-redirect DLL also hooks Winsock in the three Souls games: name lookups and connections to anything but
this PC fail, so they never reach FromSoftware's servers. Elden Ring additionally starts without Easy Anti-Cheat.
Bloodborne's profile settings have online play turned off.

The launcher refuses to start a game when its redirect can't be put in place (for example another mod already
uses `dinput8.dll`). Set `"RequireSaveRedirect": false` in the settings file to override.

### Updates

On every start the launcher checks the repository's releases and replaces itself with a newer build.
CI makes a release (`launcher-v1.0.<build>`) from every push to `main`. If the repository is ever made private
again, put a read-only token in `launcher_settings.json` as `"GitHubToken"`. `"AutoUpdate": false` turns the check
off.

## Porter (Phase 3/4: content from the other games in Elden Ring)

`Porter/` builds `DarkSignPorter.exe`, which reads Dark Souls III and Elden Ring straight from their packed
archives and will build the Elden Ring content mod: items, enemies and bosses from Dark Souls III first, then Dark
Souls Remastered and Bloodborne, with items Elden Ring already has skipped. It only reads the game folders; everything it writes goes to
`TheDarkSign\Work` (and later `TheDarkSign\Mods\ER`). It lives in `TheDarkSign\Tools\Porter` and finds the
project root from there, or takes `--root`.

```
git submodule update --init      # SoulsFormatsNEXT, pinned
dotnet publish Porter/src/DarkSign.Porter -c Release -r win-x64 --self-contained false -o "D:\The Dark sign\TheDarkSign\Tools\Porter"

DarkSignPorter scan                          # index every file in both games' archives
DarkSignPorter extract DS3 /msg/engus/*      # copy files out, decompressed
DarkSignPorter inventory                     # DS3 and Elden Ring weapons, armor, rings, spells, NPCs
```

Elden Ring's files are Oodle-compressed; the porter copies the game's `oo2core_6_win64.dll` into
`TheDarkSign\Work\cache\oodle` and loads it from there (`Porter/src/SoulsFormats.Net8/OodleCompressor.cs` makes
SoulsFormats accept Oodle 2.6). Building needs the .NET 10 SDK (SoulsFormatsNEXT uses C# 13); the exe still runs on the .NET 8 runtime.
On first run the porter downloads the archive keys and file-name lists from
[UXM Selective Unpack](https://github.com/Nordgaren/UXM-Selective-Unpack) and paramdefs from
[Paramdex](https://github.com/soulsmods/Paramdex), both at pinned commits, into `TheDarkSign\Work\cache`.
[SoulsFormatsNEXT](https://github.com/soulsmods/SoulsFormatsNEXT) is GPLv3, so a porter build shared with anyone
else must come with its source.
