# Probe 18: does any event-script prompt (3[24]) show under the mod? (2026-11-24)

Tool: `p18-Program.cs` (SoulsFormats only; builds like the probe 16 tools). Every edit is temporary: hash the files
first, restore them after, and check the hashes, as in probes 14-17.

Why: DS3 NPC talk prompts work in ported maps (they use ActionButtonParam 6000 from talk scripts), but no event
script prompt has ever shown on a ported target. Untested cells so far: a category 5 row on an asset, any 3[24] in a
vanilla Elden Ring map under the mod, and 3[24] on a friendly ported character.

## Part A: prompts (report for each test: prompt shown yes/no, screenshot next to the target, what E does)
1. **m45_00 (Gundyr's map), vanilla rows on ported targets.** Find a friendly ported talker first:
   `p18 near <mod m45_00_00_00.msb.dcx> <Fire Keeper or any Firelink NPC position> 40` (pick an Enemy row with
   TalkID != 0 and an entity ID; any ported Firelink NPC will do). Then:
   `p18 inject <mod m45_00_00_00.emevd.dcx> <out> 45005990 9000:45001952 9000:45000800 6000:<talker entity>`
   - 9000 = Elden Ring "Examine" (category 5, dummy poly -1, radius 1), on the sword asset 45001952 and on Gundyr.
   - 6000 = Elden Ring "Talk" (category 4) on the friendly talker.
   Walk up to the sword asset (beside Gundyr), then to the talker.
2. **Vanilla control: Roundtable Hold (m11_10_00_00) under the mod.** Pick an asset with an entity ID within a few
   metres of where the player stands after loading there: `p18 near <m11_10_00_00.msb.dcx the game loads> <x y z> 15`.
   Inject into the m11_10 emevd the game loads (the mod's if it ships one, else a copy of vanilla's placed in the mod
   folder, removed afterwards):
   `p18 inject <m11_10_00_00.emevd.dcx> <out> 11105990 9000:<asset entity> 74000007:<asset entity>`
   Walk up to that asset.

## Part B: the instant defeat once the boss bar shows (m45_00, plain load at Gundyr like probe 16 run B)
1. `p18 bypass <emevd> <out>`: probe 16 run B again, on a save where Gundyr was never fought if one exists (or the
   current one). One frame every 0.5 s from the pull to 10 s after the boss bar.
2. `p18 bypass <emevd> <out> nosp`: the same, with SpEffect 700005800 not applied. Does the bar still show empty and
   does he still vanish?
