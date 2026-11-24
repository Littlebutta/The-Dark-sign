# Probe 17: which ActionButtonParam categories event scripts use (read-only, 2026-11-24)

1. **1-vanilla-3-24-uses.tsv**: every vanilla ER emevd scanned (all /event/*.emevd.dcx). 214 uses of 3[24] over 77 rows
   (plus 36 where the row comes from a parameter). Targets were resolved in that map's MSB: Asset, Enemy, Region,
   "param" (the entity comes from a parameter), or unresolved.
2. **2-vanilla-category-histogram.txt**: categories over all 400 vanilla rows, with examples. 2 = pillage/pick up
   (136), 4 = **Talk** (61, e.g. 6000), 5 = examine/lever/door/mist and other world actions (172), 14 = 13 rows, and
   singletons for messages, bloodstains, summon signs and riding.
3. **3-mod-74000000-74099999.tsv**: our 71 ported rows. **48 are category 4** (Elden Ring's Talk category) and 23 are
   category 5. 74000007 ("Pull out a sword from the Segunda grave", DS3 row 8900) is category 4.

**Finding:** most rows that vanilla Elden Ring uses with 3[24] are category 5: 65 rows, against 5 in category 4,
5 in category 2 and 1 in category 8. The five category-4 rows (6000 Talk, 6321 Touch the Two Fingers, 6340 Summon
Latenna, 6519, 6590 Examine grave) are each used once, all with the entity passed in as a parameter, which usually
means an NPC. Our ported rows are mostly category 4 (48 of 71), because the port copies DS3's numbers, and so is
74000007. Probe 16 also failed with 6000 (category 4) on an asset, which fits category 4 being the talk-to-character
kind. Worth testing: 74000007 as category 5, the kind ER's world actions on assets use. Counts of the category column
in 1-vanilla-3-24-uses.tsv:

## In-game follow-up: 74000007 set to category 5 (release 1.0.620)
Only the category changed (4 -> 5), in the built regulation. Walked up beside Gundyr and the sword (cat5-near.png):
**no prompt**. Pressed E, then a 20 s burst (cat5-after-E-20s.png): **nothing**, and he stays kneeling. So the category alone
isn't it. The regulation was restored (26412e47...) and the save put back.
