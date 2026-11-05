# How Elden Ring mods add unique weapon sounds (2026-11-04)

What released mods and tools do to give a weapon its own sounds, compared with The Dark Sign's sound port.
Nexus mod archives need a login to download, so the comparison uses the open-source mods and tools that do the
same job and publish their code or notes. Credits are in CREDITS.md.

## What they do

| Step | Community way | Source |
|---|---|---|
| Where the sounds live | New events in the always-loaded `sd/enus/cs_main.bnk` (or a custom bank), media embedded in its DATA or loose at `sd/wem/NN/<id>.wem` | Yonder docs, Spellforge format notes |
| Loader | me3 (or vswarte's ModEngine2 fork). Official ModEngine2 2.1.0 doesn't hook Wwise's file reads | Spellforge, er_soundbank_helper README, RogueRing |
| Bank objects | `Play_<letter><9 digits>` event -> Play action naming cs_main -> a **random container of its own** -> Sound(s). The container hangs under an existing actor-mixer, which lists it (children sorted by ID); every child comes before its parent in HIRC. Breaking this silences the whole of cs_main | Yonder "Simple Sounds", er_soundbank_helper, Spellforge `copyEvents` |
| Audio | Wwise Vorbis made by WwiseConsole (Yonder), or plain 16-bit PCM wems (codec 0x00010001), which RogueRing builds by hand and plays in game | Yonder wem.py, RogueRing bnk_pcm.py |
| Loudness | Set on the new container's `Volume` (about -15 loud to -3 quiet); pitch and filters come from the sound's own branch | er_soundbank_helper README |
| TAE | `Wwise_PlaySound_BySlot` (129): sound type, ID, dummy poly, slot; StateInfo cleared; SoundIDOffsetType 2 adds the weapon's `wepSeIdOffset`, -1 none | Smithbox ER TAE defs, Spellforge |
| Fallback | DOOM RING skips Wwise and plays WAVs from its DLL on its own audio stream | DOOM RING audio.rs |

## Gaps found in our port, and what changed

1. **Our new Sound sat inside Elden Ring's own swing container** and inherited its tuning. The scythe R1 whoosh
   (`Play_c105060900`) is a random container at -14 dB and **-2,200 cents** (about two octaves down) with low- and
   high-pass filters, all tuned for a thin whoosh sample. A DS3 sample under it comes out as a low muffled rumble,
   which is easy to hear as silence. Now each ported sound gets **its own random container**: a copy of the
   template's (volume and playback kept, **pitch and both filters left out**), hung under the template's actor-mixer
   (so bus, attenuation and 3D settings stay the weapon sounds'), listed by it, and inserted children first. The
   vanilla container is no longer edited, so vanilla swings can't pick up DS3 samples either.
2. **TAE 129 fields copied from the Elden Ring swing**: StateInfo, the override sound (+20 type, +24 ID) and the
   offset type (+28) belonged to the whoosh. Now StateInfo is 0, the override points at the ported sound itself, and
   the offset type is -1 (no `wepSeIdOffset`).
3. **No check of the hierarchy rules**: `validate-mod` now reports a ported sound whose parents come before it or
   don't list it, and `port-weapons` prints how many of cs_main's nodes the new reader reads exactly (any type it
   can't read falls back to the old layout). `sound-selftest` (CI) checks the layout on a small bank.

Unchanged and already matching the community: me3 as loader, events named `Play_c9xxxxxxxx`, Play actions naming
cs_main (cloned from a cs_main action), embedded PCM wems, `wepSeIdOffset` 0 for ported weapons.

`DARKSIGN_SOUND_CONTAINERS=0` brings back the old layout for comparison.

## If it is still silent in game

Run `sound-probe` (ported events -> Elden Ring's whoosh) under me3. Whoosh heard: the bank and events work and the
audio is at fault, so try Vorbis via WwiseConsole (Yonder's path, needs the free Wwise install). Still silent: the
TAE events aren't reaching the bank; check `mod-check 81190000`. DOOM RING's own-audio-stream DLL is the last resort
(no 3D position, but always audible).

## Second round (2026-11-05): what the first round missed

On 2026-11-01 the ported audio played on Devin's PC when a swing used an existing Elden Ring sound number, and stayed
silent under any new number. The community tools copy events under their **original** numbers (er_soundbank_helper,
Spellforge), so that test fits what they do and doesn't tell why ours fail. Two sources that add brand-new numbers do:

| Finding | Source | What changed |
|---|---|---|
| The game keeps its own index of which events each bank provides, `/sound/soundbanksinfo.mobnkinfo` (FromSoftware's replacement for Wwise's SoundbanksInfo). Every shipped event is listed; ours never were | Yonder `game/mo.py`, Smithbox `SBI.bt` (TKGP), Spellforge format notes, Nightreign Ultimate Voices | `SoundIndex` lists every ported event under its bank and writes `Mods\ER\sound\soundbanksinfo.mobnkinfo` (appended patch: the original bytes stay put). `validate-mod` checks each posted event is listed |
| Nightreign Ultimate Voices' new `Play_c9xxxxxxxx` events stayed silent even after registering them, until the TAE event was the game's own **one-shot 129** (DummyPoly/Slot -1, PlaybackBehaviorType 0, offset type -1). PlaybackBehaviorType 1 cuts the sound off when the event ends, and DS3's sound events last a frame or two | NUV docs/retimed-callout-trigger.md, Smithbox ER enums | Ported sound events are always 129 (a move without its own 129 swing takes Elden Ring's most common one, never 128), with slot -1 and playback 0. The dummy poly stays the template swing's, so the sound still comes from the blade |
| Wwise's PrepareEvent loading failed with the media only embedded and worked with a loose copy | NUV verification.md | Each embedded sample also gets a byte-identical `sd\wem\NN\<id>.wem` |
| Yonder recommends a bank of your own over editing the 75 MB cs_main, kept loaded through Torrent's NpcParam rows (`SoundAddBankId`) | Yonder Custom Soundbanks guide, er_soundbank_helper's empty bank | New fallback **E**: the sounds go into `sd\cs_c9990.bnk` (header copied from cs_main with a new ID and the SFX language, the new Sounds and containers, unchanged copies of their actor-mixers listing only them, Play actions naming the new bank); cs_main isn't touched; Torrent's rows 8000-8005 that name no bank load it; the index lists it |

`eventinfo.eib`, Nightreign's second index, isn't in Elden Ring's archive lists (BinderKeys), so only the SBI is
edited; the porter logs it if a game version has one. Fallback **F** is A as it was before this round
(`DARKSIGN_SOUND_INDEX=0`, `DARKSIGN_SOUND_ONESHOT=0`, `DARKSIGN_SOUND_MIRROR=0`), for when the new A breaks something.

Order to try if swings are still silent: A, then E (no cs_main edit at all), then B, then D (our DLL; always audible).
