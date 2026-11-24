# Probe 12: does the game load vanilla c3060_div00 for Gundyr? (2026-11-24)

Probe 11: putting other bytes into our c7112_div00 a000_003012.hkx changed nothing; 3012 is still vanilla c3060's
5.1 s swing. So the clip comes from a binder that isn't ours, almost certainly vanilla c3060_div00. me3 logs only
overrides, so to see the open and to prove the source, override that vanilla binder with a marked copy.

Same tool (reports/boss-start/probe10/taeid, rebuilt from this commit: new `plant` mode).
1. `taeid plant "<TheDarkSign>\Work\extract\ER\chr\c3060_div00.anibnd" "<...>\Mods\ER\chr\c7112_div00.anibnd.dcx" "<...>\Mods\ER\chr\c3060_div00.anibnd.dcx" 3012 3014`
   (use the extract's actual file name, .dcx or not). It writes a NEW file Mods\ER\chr\c3060_div00.anibnd.dcx: vanilla
   c3060_div00 with its a000_003012.hkx holding our DS3 3014 clip (3.13 s), compressed like our div binder. Our c7112
   files are not touched. Make sure no c3060_div00 file was in Mods\ER\chr before (there shouldn't be).
2. Gundyr (Rtest save), animprobe `W40,A3012:0,W10,A3012:0`.
3. Report: (a) me3 log lines with c3060 (is Mods\...\c3060_div00.anibnd.dcx opened, and next to which c7112 lines /
   line numbers), (b) does 3012 now show a ~3 s raised-sword move instead of the 5 s swing and lunge?
4. Restore: delete Mods\ER\chr\c3060_div00.anibnd.dcx; hash-check the c7112 files and emevd as usual.
