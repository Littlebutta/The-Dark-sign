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
| Fextralife DS3 wiki and community weapon guides | Correct weapon types for DS3 weapons (e.g. Friede's Great Scythe is a reaper) |
