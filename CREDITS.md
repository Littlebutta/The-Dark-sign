# Credits

The Dark Sign builds on the work of the FromSoftware modding community. Everything we copy, bundle, download
or take ideas from is listed here with its author and license. New entries go in with the change that uses them.

Dark Souls III and Elden Ring are © FromSoftware / Bandai Namco. The porter reads them only from the owner's own
install, and no game files are distributed.

## Code and data the porter uses

| Work | Author | License | How we use it |
|---|---|---|---|
| [SoulsFormatsNEXT](https://github.com/soulsmods/SoulsFormatsNEXT) | soulsmods (after TKGP's SoulsFormats) | GPL-3.0 | Reading and writing BND4, DCX, PARAM, TAE, FLVER, TPF and more (git submodule) |
| [HKLib](https://github.com/The12thAvenger/HKLib) | The12thAvenger | MIT | Reading and writing Elden Ring Havok 2018 animations and behaviour (git submodule) |
| [SoulsAssetPipeline](https://github.com/Meowmaritus/SoulsAssetPipeline) | Meowmaritus | GPL-3.0 | HKX reader and spline decompression for DS3 animations (files copied, Porter/third_party/SoulsAssetPipeline-HKX) |
| [@cccode/fxr](https://github.com/EvenTorset/fxr) | EvenTorset | Unlicense | Converting DS3 effects (FXR) to Elden Ring's format (Porter/third_party/fxr) |
| [Paramdex](https://github.com/soulsmods/Paramdex) | soulsmods contributors | see repo | Param definitions and row names, downloaded at a pinned commit |
| [UXM Selective Unpack](https://github.com/Nordgaren/UXM-Selective-Unpack) | Nordgaren | see repo | Archive keys and file-name lists, downloaded at a pinned commit |
| [NVorbis](https://github.com/NVorbis/NVorbis) | Andrew Ward and contributors | MIT | Decoding DS3's Vorbis sounds |
| [Jint](https://github.com/sebastienros/jint) | Sébastien Ros | BSD-2-Clause | Running fxr.js inside the porter |
| [ZstdNet](https://github.com/skbkontur/ZstdNet) | SKB Kontur | BSD-3-Clause | Zstandard (DCX) compression |
| [BouncyCastle](https://github.com/bcgit/bc-csharp) | Legion of the Bouncy Castle | MIT | Decrypting regulation files |
| [DrSwizzler](https://www.nuget.org/packages/DrSwizzler) | see package | see package | Texture swizzling (SoulsFormats dependency) |
| [Acornima](https://github.com/adams85/acornima) | Adam Simon | BSD-3-Clause | JavaScript parser Jint depends on |
| [Microsoft.Toolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) | .NET Foundation | MIT | Buffer helpers bundled with the porter |
| [HKX2](https://github.com/vawser/Smithbox) (Smithbox, after Katalash's HKX2) | vawser, Katalash and contributors | MIT | Reading and writing DS3 Havok 2014 collision packfiles for the map port (files copied at Smithbox commit e27134d, Porter/third_party/HKX2) |
| [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET) | Nominom | MIT/Unlicense | Encoding icon textures |

## Tools the launcher runs

| Work | Author | License | How we use it |
|---|---|---|---|
| [ModEngine2](https://github.com/soulsmods/ModEngine2) | soulsmods | MIT | Loads the mod folder into the game |
| [me3](https://github.com/garyttierney/me3) | Gary Tierney and contributors | MIT / Apache-2.0 | Mod loader that also loads modded Wwise sound banks (v0.13.0, being tested as ModEngine2's replacement, 2026-11-01); we follow its sd/ and wem/ path rules |

## Knowledge and inspiration

| Source | What we learned |
|---|---|
| [er_soundbank_helper](https://github.com/ndahn/er_soundbank_helper) (ndahn) and [rewwise](https://github.com/vswarte/rewwise) (vswarte) | Official ModEngine2 doesn't load modded sound banks; modded banks mirror the game's sd/ layout |
| [Nexus forum: Sound modding tools](https://forums.nexusmods.com/topic/13484154-sound-modding-tools) | Elden Ring sound-bank workflow and loader requirements |
| [me3 source](https://github.com/garyttierney/me3) (crates/mod-host-assets/src/wwise.rs) | How me3 maps sd:/ banks and .wem streams to a mod package |
| [Yonder docs](https://github.com/ndahn/yonder) (ndahn): events, soundbanks, adding sounds | Event names are Play_<type><9-digit ID>; new events in cs_main play from a TAE 129; which banks stay loaded |
| [DSAnimStudio](https://github.com/Meowmaritus/DSAnimStudio) TAE templates (Meowmaritus and contributors) | Event type names used by validate-mod (SpawnFFX family 95-123) and to find DS3's weapon model events (711-713) |
| [Paramdex](https://github.com/soulsmods/Paramdex) EquipParamWeapon / WepAbsorpPosParam defs | wepSeIdOffset (weapon sound ID offset), absorpParamId and the per-model dummy poly fields used to place split weapons' models |
| Fextralife DS3 wiki and community weapon guides | Correct weapon types for DS3 weapons (e.g. Friede's Great Scythe is a reaper) |
| [DS3PortingTool](https://github.com/GompDS/DS3PortingTool) (GompDS; thanks to TKGP, Meowmaritus, Katalash, The12thAvenger, Nordgaren) | The steps of a character port (FLVER materials to the target's, Havok version conversion, TAE event filtering and ID remapping, sound events renamed to the new character ID); our enemy port follows the same order in the other direction (DS3 to Elden Ring) |
| [Souls Modding Wiki tutorials](https://soulsmodding.com/doku.php?id=tutorial%3Amain): ElaDiDu (Elden Ring AI), Themyys (attacks, bullets and BehaviorParam for NPCs), Katalash (Anatomy of a Boss Fight), thefifthmatt (Elden Ring EMEVD), Vawser (Smithbox map editor) | How Elden Ring enemies tie model, behaviour, AI, params, boss events and map placement together, used to plan the Midir and Friede port |
| [Paramdex DS3 row names](https://github.com/soulsmods/Paramdex) | NpcParam rows that identify Sister Friede (6020xx), Father Ariandel (6010xx) and Darkeater Midir (6210xx) |
| [Themyys: Introduction to Moveset Modding](https://docs.google.com/document/d/1quFnkKMRAx5mApbB7LRDPByYZyz6J2-7dFtUwk-NQpM) | TAE section ranges, Special Motion Category (`spAtkcategory`) for custom movesets, SwordArtsParam and hks for arts, jump attacks bound to TimeActEditor_10 (docs/benchmarks/weapon-port-benchmark.md) |
| [Themyys and Challenger Andy: Soundbank Editing](https://docs.google.com/document/d/1lNov-a0DwnMY2yZywH3hFYzuoDfndofguvZmAnLDo-U) | Copying Wwise Event, Action, container, Sound and .wem objects into cs_main in parent order |
| [Soulsmodding wiki tutorial index](http://soulsmodding.wikidot.com/tutorial:main) | Index of moveset, behaviour, sound, FXR and porting tutorials (Rainer, Kmstr, Igor, F Wang, Challenger Andy, JeNoVaViRuS, Chocotavo, WrathfulMantis) |
| [ERClipGeneratorTool](https://github.com/The12thAvenger/ERClipGeneratorTool) (The12thAvenger, MIT) | Confirms one clip generator per new animation, internal ID max+1, under the selector for its animation ID |
| [EldenRingHKS](https://github.com/ividyon/EldenRingHKS) (ividyon) | Decompiled c0000.hks used by moveset and weapon-art mods |
| [Yonder](https://github.com/ndahn/yonder) (ndahn, GPL-3.0) | Successor to er_soundbank_helper; sound hierarchy transfer and wav/wem conversion |
| [Player Animation Repositories](https://www.nexusmods.com/eldenring/mods/3027) (Blubber; packed by Themyys, ol51609, HaVeNII7; Friede/Bloodborne sets via Mayhem) | DS3 player and Sister Friede animations retargeted to the ER player, a visual reference for our conversion |
| [Romano's DS3/Sekiro weapon moveset pack](https://www.nexusmods.com/eldenring/mods/9319) | New DS3 weapons with original movesets and sounds; credits ERSoundBankLoader and Blubber's repositories |
| Nexus articles [228](https://www.nexusmods.com/eldenring/articles/228) and [789](https://www.nexusmods.com/eldenring/articles/789) | Merging TAE mods; new TAE binder entries must be in ID order |
| [Weapon Catalysts](https://www.nexusmods.com/eldenring/mods/7222) (nikonikoneko) | Weapon behaviour changed in hks by special motion category |
| [Ringed Knight Set](https://www.nexusmods.com/eldenring/mods/1061) (KRDCD), [Armor of Thorns](https://www.nexusmods.com/eldenring/mods/3115) (Noctis) | Earlier DS3 armor/weapon ports, done by replacing vanilla parts |
| Nexus mods [6384](https://www.nexusmods.com/eldenring/mods/6384) and [6340](https://www.nexusmods.com/eldenring/mods/6340) | Ship vswarte's (chainfailure) ModEngine2 fork that loads sound banks |
| [JortPob](https://github.com/infernoplus/JortPob) (infernoplus) | The Morrowind-to-Elden Ring map port: ported maps reuse existing map IDs, collision written as XML tagfiles, global event flags allocated from the empty tiles along the bottom edge of the world (1030290000-1039290000; 0-999 saved, 2000-2999 temporary) |
| [ER_OBJ2HKX](https://github.com/Dropoff/ER_OBJ2HKX) (Dropoff) | Elden Ring map collision as an XML Havok tagfile (fsnpCustomParamCompressedMeshShape) that the game loads |
| [Smithbox](https://github.com/vawser/Smithbox) (vawser and contributors) | Map file paths for both games, HKLib/HKX2 collision decoding (triangle and quad layout of compressed mesh sections), and the template-part approach to new MSB parts |
| [DarkScript3](https://github.com/AinTunez/DarkScript3) (AinTunez, thefifthmatt) er-common.emedf.json | Elden Ring EMEVD instruction layouts (WaitForEventFlag, SetEventFlag, WarpPlayer, InitializeEvent, EndUnconditionally) for the DS3 run events |
| [DarkScript3](https://github.com/AinTunez/DarkScript3) (AinTunez, thefifthmatt) ds3-common.emedf.json and er-common.emedf.json | Both games' event instruction definitions (names, argument names, types and defaults), downloaded at a pinned commit and used to convert DS3 map and common event scripts into Elden Ring ones |
| [Erd-Tools](https://github.com/Nordgaren/Erd-Tools) (Nordgaren) | Byte patterns for Elden Ring's event flag manager, SetEventFlag and IsEventFlag, used by the F1 menu's DS3 run tab |
| [NavMeshStudio](https://github.com/Pear0533/NavMeshStudio) (Pear0533) and [PhiveConverter](https://github.com/dt-12345/PhiveConverter) | Navmesh generation for Elden Ring maps and the hkcdSimdTree node layout; planned for the map port's navmesh stage |
