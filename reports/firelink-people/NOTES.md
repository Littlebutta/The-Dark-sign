# Firelink (m40_00_00_00) people diagnostic — 2026-11-25

- Installed mod build (Mods\ER\darksign_build.txt): **1.0.636** (build id 40C61B7D…9CD4). Tools\Porter is also 1.0.636.
- Porter built from `origin/claude/project-thread-c3g7jv` @ 593ccab (Release, 0 errors).
- Read-only: game, launcher and me3 were not started; nothing in Mods\ER or saves was written.

## Commands
```
dotnet build Porter/src/DarkSign.Porter -c Release
DarkSignPorter.exe --root "D:\The Dark sign" --game "DS3=...\DarkSoulsIII.exe" --game "ER=...\eldenring.exe" map-diag people m40_00_00_00
```
(same --root / --game form the launcher uses; output `Work\reports\map-diag\people.txt`)

## Error on first run (bug in map-diag people)
```
error: Could not find a supported version of the Oodle compression library.
  DllNotFoundException: Unable to load DLL 'oo2core_9_win64.dll' / 'oo2core_8_win64.dll' / 'oo2core_6_win64.dll'
```
Cause: `MapPeopleDiag.cs` reads the mod's Elden Ring MSB/EMEVD (`ctx.ModPath(...)`, Oodle DCX) but only opens
DS3 archives. `PorterContext.AddOodle` is only called from `OpenArchives(EldenRing)`, so Oodle's search path is
never set. Workaround for this run: copied `Work\cache\oodle\oo2core_6_win64.dll` next to the scratch build's exe;
second run exited 0. Fix suggestion: register Oodle in the context up front (or open ER archives) for any command
that reads ER DCX from ModPath.

## Logs
`people-log-lines.txt`: lines matching `people|m40|enemies:` from `Work\logs\port-maps-20261124-231859.txt`
and `Work\logs\porter-port-maps-20261124-222422.log` (newest of each).
