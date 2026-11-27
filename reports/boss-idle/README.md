# Boss idle data pull (read-only, mod build 1.0.706)

Files only; the game, launcher, Mods\ER and saves were not touched.

- hks/: mod-c7134/c7032/c7108/c7112.hks (Mods\ER\action\script), er-vanilla-c4311/c3970/c3061.hks
  (extracted from the ER archives). Vanilla ER has no c4310.hks ("not found in the ER archives").
  *.diag.txt = `DarkSignPorter enemy-diag hks <file>` output for each.
- params/: one field per line, name=value. Byte arrays print as System.Byte[] (padding fields).
  - mod-*: from Mods\ER\regulation.bin (NpcThinkParam 71349000/70329000/71089000/71129000,
    NpcParam 71340000/70320000/71080001/71120000).
  - The mod think rows are converted from the DS3 rows, not copied from ER templates
    (Work\reports\enemy-ai.tsv: 602000->71349000, 132000->70329000, 502000->71089000, 511000->71129000),
    so ds3-vanilla-* holds those DS3 source rows (DS3 NpcParam 602000/132000/502001/511000 too).
    DS3's NpcThinkParam layout differs from ER's, so field lists do not line up 1:1.
  - er-vanilla-*: ER rows of the template chrs. ER NpcParam has no think-id field, so no row
    "points at" a think row; picked: think 43110000, 43113900, 43100000, 39700000, 30610000, 30600000;
    NpcParam 43113906 (Soldier of Godrick boss), 43110000, 39701942 (Armored Beastman boss),
    39700000, 30610000, 30600000. (NpcParam 43100000 does not exist.)
- ai/: Mods\ER\script 990057 (Friede), 990042 (Demon), 132000 (Sage), 990044 (Gundyr) _battle.luabnd.dcx.

Mod think-row fields that differ between the three idle bosses and Gundyr (71129000) include:
battleGoalID (Sage keeps DS3 id 132000, not renumbered like the others), Lv1/Lv2 forget time,
eye_dist/BattleStartDist, nose_dist (Friede 0), back-home distances (9999 vs Gundyr 999),
nonBattleActLife, goalAction_ToSearchLv2 / changeStateAction_ToNormal, enableNaviFlg_Door
(only Gundyr 1). searchEye_dist is 0 on Friede's row.
