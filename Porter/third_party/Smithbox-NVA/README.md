`NVA.cs` is copied from [Smithbox](https://github.com/vawser/Smithbox) (GPL-3.0, see LICENSE) at the commit in COMMIT:
`src/Andre/SoulsFormats/SoulsFormats/Formats/NVA.cs`, by vawser and the Smithbox contributors, after TKGP's SoulsFormats.
It reads and writes the navmesh list (.nva) of every FromSoftware game from Bloodborne to Nightreign, Elden Ring's
included, which the NVA class of the pinned SoulsFormatsNEXT does not.

Changed: the namespace (`SmithboxFormats`, so it sits beside `SoulsFormats.NVA`), the editor-only `[RotationRadians]`
attribute removed, and `AssertASCII` called with separate arguments.

The map port's navmesh stage (`Porter/src/DarkSign.Porter/Maps/NavmeshList.cs`, `NavFaceTree.cs`) also follows what
Smithbox's Navigation tool documents about the game's files; CREDITS.md lists each finding.
