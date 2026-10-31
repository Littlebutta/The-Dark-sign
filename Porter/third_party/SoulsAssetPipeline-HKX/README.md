Files copied from [SoulsAssetPipeline](https://github.com/Meowmaritus/SoulsAssetPipeline) (GPL-3.0, see LICENSE)
at the commit in COMMIT: `Animation/HKX/HKX.cs`, `Animation.cs`, `SplineCompressedAnimation.cs` and
`Animation/NewBlendableTransform.cs`. They read Dark Souls III's Havok 2014 packfiles and decompress spline animations.

Changed: `SplineCompressedAnimation.cs` drops an unused `using static SoulsAssetPipeline.AnimationImporting.ImportedAnimation;`.
The two helpers the files use from the rest of that project are in `Porter/src/DarkSign.Porter/Havok/SapCompat.cs`.
