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

Each launch passes the save location to the game process as environment variables
(`DKSIGN_SAVE_DIR`, `DKSIGN_SAVE_EXT`, `DKSIGN_PROFILE`, `DKSIGN_GAME`) for `dksign_saveredirect.dll` to read.
Until that DLL sits next to a game's exe, the launcher blocks the launch so the game cannot write to the vanilla
AppData saves. Set `"RequireSaveRedirect": false` in the settings file to override.

### Updates

On every start the launcher checks the repository's releases and replaces itself with a newer build.
CI makes a release (`launcher-v1.0.<build>`) from every push to `main`. While the repository is private, the
launcher needs a GitHub token to see them: create a fine-grained token with read-only **Contents** access to
this repository only and put it in `launcher_settings.json` as `"GitHubToken"`. `"AutoUpdate": false` turns the
check off.
