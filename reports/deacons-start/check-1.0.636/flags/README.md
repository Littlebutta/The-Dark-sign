# Flag blocks (build 1.0.636)

- `darksign_flag_blocks.txt`: raw copy of `Mods\ER\darksign_flag_blocks.txt` as installed (37 probe lines).
- `flagcheck-lines.txt`: every `flagcheck` line from each distinct `dksign_testmenu*.log` written on 2026-11-24 (Default profile, Overnight test save and its backups, the boss-start playtest copies, and my two Deacons sessions). Each file starts with a `===` header naming its source log.
- **Block 1030297 (probe 1030297999) is reported as kept in every session**: "ok, the game keeps flags here", 17 sessions. All 37 listed blocks are ok in every session; the only MISSING lines are from the diagnostic run below.
- **Flags 1030297714, 1030297598, 1030297625 and 1030297749 appear nowhere** in the test menu or its logs. The DLL only logs flags it writes itself: probes, request flags and hotkeys.

## Diagnostic run (23:56 session, last block in flagcheck-lines.txt)
For this one launch I appended 10 probe lines for m45_05 (Cathedral of the Deep): 45050999 to 45059999 (`darksign_flag_blocks.with-diagnostic-m45_05.txt`). The result:
`flagcheck: block 45050..45059 (m45_05_00_00 map flags): MISSING, the game has no such block` and `37 blocks ok, 10 missing`.
So Elden Ring has no flag storage for m45_05's own flag range. Writes to 45055805 (the Deacons entry flag from PR #286), 45050800 (defeated), and any other 4505xxxx flag are dropped. That matches the fight never starting. The m45_00 to m45_02 blocks are ok because those are Elden Ring's own colosseum maps. The port log says "the map registry decides their flag blocks" for m45_05, but these flags don't stick.
I restored the original file afterwards (sha256 matches) and restored the test save from backup.
