# Probe 11: does the game read clip data from our c7112_div00 at all? (2026-11-24)

Probe 10 proved the clip Gundyr plays for 3012 is vanilla c3060's (5.1 s), not the DS3 one in our
c7112_div00.anibnd.dcx (2.03 s), and that the TAE's ID/sib don't decide it. me3 shows the game opens our c7112.anibnd,
_div00 and _div01. The .blf files in c7112.anibnd are generic regex rules (no character named), so not the link either.
Question: is the clip data in our div binder used at all?

Same tool as probe 10 (reports/boss-start/probe10/taeid, rebuilt from this commit: it has a `swap` mode).
1. Back up `Mods\ER\chr\c7112_div00.anibnd.dcx`.
2. `taeid swap "<...>\Mods\ER\chr\c7112_div00.anibnd.dcx" 3012 3014` (only a000_003012.hkx's bytes change: they become
   our DS3 3014 clip, 3.13 s). Optional check: `taeid clips <same file> 3012 3014` shows both 3.133 s.
3. Gundyr (Rtest save), animprobe `W40,A3012:0,W10,A3012:0`.
4. Report: is 3012 still the 5 s swing and lunge (then our div's clip data is never used and the vanilla clip comes
   from somewhere else), or a different ~3 s raised-sword move (then our file is read)? Restore and hash-check.
