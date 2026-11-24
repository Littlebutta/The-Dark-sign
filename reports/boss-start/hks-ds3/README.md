# Behaviour-script transitions by function (2026-11-24, read-only)

Source: every DS3 /action/script/c*.hks (206 files, extracted with DarkSignPorter extract DS3), plus the mod's ER
c7112.hks for comparison. All of them are compiled HKS bytecode with no decompiler on the PC, so this is a
**string-order heuristic**: a function's constants (W_..., EventCommonFunction ...) come in the file right before
its own name string, so each W_ string is given to the next EventNNNNN_onXxx name.
- In DS3 c5110 this lines up cleanly: one constant block, then each name.
- For the ER file the layout may be offset by one function, so treat the c7112 rows as unverified. The earlier
  c7112-hks-event-strings.txt paired them the other way round.

- all-functions.tsv: script, function, every W_/EventCommonFunction string in that function.
- non-idle-transitions.tsv: only the W_ strings other than W_Idle (2,429 rows).

**DS3 c5110 (Gundyr):** Event20000_onUpdate -> W_Event20002; Event20001_onUpdate -> W_Event20002;
**Event20003_onUpdate -> W_Event20002**; Event20002 and Event20004-20019 -> W_Idle; Event30000-30005 have no W_ string.
So in DS3, after 20003 (the sword pull) the script goes on to 20002 (stand up).
The raw file is at raw/hks/ds3-c5110.hks.
