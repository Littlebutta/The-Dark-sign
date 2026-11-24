# Probe 13: NpcParam normalChangeAnimChrId (2026-11-24)

Probe 12 proved Gundyr takes his clips from vanilla c3060_div00. ER's NpcParam has `normalChangeAnimChrId`, which
Paramdex describes as "replaces the character's animations with the anibnd of the given ID" (it is how vanilla c3061
uses c3060's animations). The port copies the template's NpcParam row for each ported enemy, so a template row that
borrows another character's anibnd passes that on, and the game plays that character's vanilla clips.

Tool: `dotnet build -c Release reports\boss-start\probe13\npcanim.csproj`; `npcanim` = `dotnet <out>\npcanim.dll`.
It downloads Paramdex defs to %TEMP%\darksign-probe13-paramdex on first use.
1. `npcanim survey "<TheDarkSign>\Mods\ER"` (read-only): every ported row's normalChangeAnimChrId,
   RetargetReferenceChrId, normalChangeModelId, normalChangeTexChrId, normalChangeResouceId. Save the output.
2. Back up `Mods\ER\regulation.bin`, then `npcanim set "<TheDarkSign>\Mods\ER" c7112` (those five fields -> -1 on
   Gundyr's rows only; it prints the old values and reads them back).
3. Gundyr (Rtest save), animprobe `W40,A3012:0,W10,A30002:1,W10,A3014:0`.
4. Report: is 3012 a short ~2 s DS3 swing (not the 5 s swing and lunge)? Does 30002 kneel? Does 3014 play a ~3 s
   raised-sword move? Restore regulation.bin and hash-check.
