Files copied from [Smithbox](https://github.com/vawser/Smithbox) (MIT, see LICENSE) at the commit in COMMIT:
`src/Havok/HKX2` (without `Builders`, which needs a native BVH library) and `src/Havok/Havok.Shared/Interfaces.cs`
(as `HavokShared.cs`). They read Dark Souls III's Havok 2014 packfiles, including map collision
(`fsnpCustomParamCompressedMeshShape`) and navmesh (`hkaiNavMesh`) classes; the map porter upgrades them to Elden
Ring's Havok 2018 classes (`Porter/src/DarkSign.Porter/Maps/CollisionPort.cs`).
