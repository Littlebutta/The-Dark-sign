# Probe 10: does the game play the template's own clips? (2026-11-24)

## Why
Every ported enemy's TAE keeps the template's identity. Gundyr's c7112.tae says ID **203060** and **c3060.sib**,
exactly like vanilla c3060.tae (the Giant Skeleton his files come from). The player moveset port, which works, gives
every new TAE its own ID (`tae.ID = 2000 + section`, MovesetPort.cs). If the game finds an enemy's animations
through the TAE's owner, Gundyr plays **vanilla c3060's clips**, not ours. That fits every probe:

| forced | our file (offline) | vanilla c3060 | seen in game |
|---|---|---|---|
| 3012 | DS3 swing, **2.03 s** | entry events run to 6.2 s | a **5 s swing and lunge** (probe 6) |
| 3014, 30001, 30002 (added) | DS3, 3.13 / 0.03 / 0.33 s | **no file** | twitch / nothing |
| 30003 | DS3 kneel (same bytes as 30002) | own file, 2.7 s entry | a stiff upright pose, not the kneel |
| 3012 state renamed to a000_003014 | | no such file | twitch (probes 8, 9) |

It also fits the older reports: "Jailer shows Imp moves", "moves look Elden Ring", DS3-only bones standing still.

## Steps (Rtest copy, save at Gundyr, restore and hash-check afterwards)
Build: `dotnet build -c Release reports\boss-start\probe10\taeid.csproj`, then `taeid` below = `dotnet <out>\taeid.dll`.
1. `taeid survey "<TheDarkSign>\Mods\ER\chr"`: every ported anibnd's TAE ID and sib (read-only). Save the output.
2. `taeid clips "<TheDarkSign>\Work\extract\ER\chr\c3060_div00.anibnd" 3012 3014` (and `c3060_div01.anibnd`
   `20003 30000 30002 30003`; use the .dcx name if the extract keeps it): vanilla clip lengths. Expect 3012 about 5-6 s.
3. Back up `Mods\ER\chr\c7112.anibnd.dcx`, then `taeid set "<...>\Mods\ER\chr\c7112.anibnd.dcx"` (ID -> 207112,
   sib -> c7112.sib; nothing else changes).
4. Gundyr, animprobe `W40,A3012:0,W10,A30002:1,W10,A3014:0`.
5. Report: does 3012 now play a short ~2 s swing (DS3's) instead of the 5 s swing and lunge? Does 30002 show the
   kneel? Does 3014 play a full ~3 s move? Or does Gundyr T-pose / nothing play at all? Restore and hash-check.
