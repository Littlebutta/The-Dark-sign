# Probe 17: which ActionButtonParam categories event scripts use (read-only, 2026-11-24)

1. **1-vanilla-3-24-uses.tsv**: every vanilla ER emevd scanned (all /event/*.emevd.dcx). 214 uses of 3[24] over 77 rows
   (plus 36 where the row comes from a parameter). Targets were resolved in that map's MSB: Asset, Enemy, Region,
   "param" (the entity comes from a parameter), or unresolved.
2. **2-vanilla-category-histogram.txt**: categories over all 400 vanilla rows, with examples. 2 = pillage/pick up
   (136), 4 = **Talk** (61, e.g. 6000), 5 = examine/lever/door/mist and other world actions (172), 14 = 13 rows, and
   singletons for messages, bloodstains, summon signs and riding.
3. **3-mod-74000000-74099999.tsv**: our 71 ported rows. **48 are category 4** (Elden Ring's Talk category) and 23 are
   category 5. 74000007 ("Pull out a sword from the Segunda grave", DS3 row 8900) is category 4.

**Finding:** every vanilla 3[24] row with a known category is **category 5** (see the category column of 1, summed
below). None uses 4. The port copies DS3's category numbers, and DS3's 4 isn't Elden Ring's 4. Our sword row, and
row 6000 in probe 16, are both category 4 (Talk), which Elden Ring only offers for talking to characters. Mapping DS3
categories to Elden Ring's (world actions -> 5) should bring the prompt back.
      1 -
      5 cat 2
      5 cat 4
     65 cat 5
      1 cat 8
