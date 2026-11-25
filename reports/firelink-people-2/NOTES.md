# Firelink (m40_00_00_00) people diagnostic, round 2 — 2026-11-25

- Installed mod build (Mods\ER\darksign_build.txt): **1.0.640**
- Newest port-maps log: `Work\logs\port-maps-20261125-133326.txt`
- Porter built from `origin/claude/project-thread-c3g7jv` @ 7e88e54 (Release, 0 errors), separate worktree.
- Read-only: game, launcher and me3 not started; nothing in Mods\ER or saves written; no content rebuilt.

## Command
```
DarkSignPorter.exe --root "D:\The Dark sign" --game "DS3=...\DarkSoulsIII.exe" --game "ER=...\eldenring.exe" map-diag people m40_00_00_00
```
Exit 0 on the first try: the Oodle error from round 1 no longer occurs (no DLL copied). people.txt is 4,447 lines.

## Files
- `people.txt`: the diag output, now including every event started with a DS3 person (DS3 and mod side).
- `people-log-lines.txt`: from the newest port-maps log, every line containing `people` (331), then lines
  containing `flag` (case-insensitive) and `m40` or `m45_00` (42, under the 300 cap).
- `flags-m40-people.txt`: header plus the 608 `flag` rows of `Mods\ER\darksign_flags.tsv` whose DS3 flag (column 2)
  is in 1110-1499, 1059481190, 1060320051, 74000330-74000900, 73500290, 90300-90900, 50006020 or 70000000-70009999.
  (No `event` rows fell in those ranges.)
