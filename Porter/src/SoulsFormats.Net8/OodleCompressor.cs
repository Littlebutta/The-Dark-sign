// Replaces SoulsFormatsNEXT's Utilities/Compression/Oodle/OodleCompressor.cs (GPLv3), which is excluded from this
// build. Elden Ring ships oo2core_6 (Oodle 2.6), which lacks OodleCore_Plugins_SetPrintf and
// OodleLZ_CompressOptions_GetDefault; upstream requires both, so loading it failed with "no Oodle found". Here
// those exports are optional: 2.6's OodlePlugins_SetPrintf is used instead, and default options are a null pointer.
using System;
using System.Runtime.InteropServices;
using static SoulsFormats.Oodle;
using InteropNativeLibrary = System.Runtime.InteropServices.NativeLibrary;

namespace SoulsFormats
{
    internal sealed unsafe class OodleCompressor : IOodleCompressor
    {
        private IntPtr handle;
        private readonly bool isOodle26;

        private readonly delegate* unmanaged<OodleLZ_Compressor, byte*, long, byte*, OodleLZ_CompressionLevel, IntPtr, IntPtr, IntPtr, IntPtr, long, long> compress;
        private readonly IntPtr compressOptionsGetDefault;
        private readonly delegate* unmanaged<byte*, long, byte*, long, OodleLZ_FuzzSafe, OodleLZ_CheckCRC, OodleLZ_Verbosity, IntPtr, long, IntPtr, IntPtr, IntPtr, long, OodleLZ_Decode_ThreadPhase, long> decompress;

        private readonly IntPtr getCompressedBufferSizeNeeded;
        private readonly IntPtr getDecodeBufferSize;

        public OodleCompressor(IntPtr handle, uint version)
        {
            this.handle = handle;
            isOodle26 = Oodle2Version(version) == 6;
            compress = (delegate* unmanaged<OodleLZ_Compressor, byte*, long, byte*, OodleLZ_CompressionLevel, IntPtr, IntPtr, IntPtr, IntPtr, long, long>)
                InteropNativeLibrary.GetExport(handle, "OodleLZ_Compress");
            decompress = (delegate* unmanaged<byte*, long, byte*, long, OodleLZ_FuzzSafe, OodleLZ_CheckCRC, OodleLZ_Verbosity, IntPtr, long, IntPtr, IntPtr, IntPtr, long, OodleLZ_Decode_ThreadPhase, long>)
                InteropNativeLibrary.GetExport(handle, "OodleLZ_Decompress");
            compressOptionsGetDefault = TryGetExport(handle, "OodleLZ_CompressOptions_GetDefault");
            getCompressedBufferSizeNeeded = TryGetExport(handle, "OodleLZ_GetCompressedBufferSizeNeeded");
            getDecodeBufferSize = TryGetExport(handle, "OodleLZ_GetDecodeBufferSize");
        }

        public static OodleCompressor Load(string path)
        {
            IntPtr handle = InteropNativeLibrary.Load(path, typeof(OodleCompressor).Assembly, null);

            try
            {
                return new OodleCompressor(handle, QueryVersion(handle));
            }
            catch
            {
                InteropNativeLibrary.Free(handle);
                throw;
            }
        }

        public static uint QueryVersion(IntPtr handle)
        {
            IntPtr checkVersion = InteropNativeLibrary.GetExport(handle, "Oodle_CheckVersion");
            IntPtr setPrintf = TryGetExport(handle, "OodleCore_Plugins_SetPrintf");
            if (setPrintf == IntPtr.Zero)
                setPrintf = TryGetExport(handle, "OodlePlugins_SetPrintf");

            // Oodle prints a warning here, so logging is turned off while the version is queried
            IntPtr printf = setPrintf == IntPtr.Zero ? IntPtr.Zero : ((delegate* unmanaged<IntPtr, IntPtr>)setPrintf)(IntPtr.Zero);
            uint libVersion = 0;
            ((delegate* unmanaged<uint, uint*, int>)checkVersion)(0, &libVersion);
            if (setPrintf != IntPtr.Zero)
                ((delegate* unmanaged<IntPtr, IntPtr>)setPrintf)(printf);

            return libVersion;
        }

        public byte[] Compress(byte[] source, OodleLZ_Compressor compressor, OodleLZ_CompressionLevel level)
        {
            ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);

            IntPtr pOptions = IntPtr.Zero;
            try
            {
                if (compressOptionsGetDefault != IntPtr.Zero)
                {
                    var getDefault = (delegate* unmanaged<OodleLZ_Compressor, OodleLZ_CompressionLevel, IntPtr>)compressOptionsGetDefault;
                    OodleLZ_CompressOptions options = Marshal.PtrToStructure<OodleLZ_CompressOptions>(getDefault(compressor, level));
                    // Required for the game to not crash
                    options.seekChunkReset = true;
                    options.seekChunkLen = 0x40000;
                    pOptions = Marshal.AllocHGlobal(Marshal.SizeOf<OodleLZ_CompressOptions>());
                    Marshal.StructureToPtr(options, pOptions, false);
                }

                byte[] compBuf = new byte[CompressedBufferSizeNeeded(compressor, source.LongLength)];
                long compLen;
                fixed (byte* pSource = source)
                fixed (byte* pCompBuf = compBuf)
                {
                    compLen = compress(compressor, pSource, source.LongLength, pCompBuf, level, pOptions,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
                }
                Array.Resize(ref compBuf, (int)compLen);
                return compBuf;
            }
            finally
            {
                if (pOptions != IntPtr.Zero)
                    Marshal.FreeHGlobal(pOptions);
            }
        }

        public byte[] Decompress(byte[] source, long uncompressedSize)
        {
            ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);

            // corruptionPossible is a 4-byte BOOL
            long decodeBufferSize = getDecodeBufferSize == IntPtr.Zero
                ? uncompressedSize + 0x100000 // generous: Oodle may write up to 64 bytes past the end per block
                : isOodle26
                    ? ((delegate* unmanaged<long, int, long>)getDecodeBufferSize)(uncompressedSize, 1)
                    : ((delegate* unmanaged<OodleLZ_Compressor, long, int, long>)getDecodeBufferSize)(
                        OodleLZ_Compressor.OodleLZ_Compressor_Invalid, uncompressedSize, 1);
            byte[] rawBuf = new byte[decodeBufferSize];
            long rawLen;
            fixed (byte* pSource = source)
            fixed (byte* pRawBuf = rawBuf)
            {
                rawLen = decompress(pSource, source.LongLength, pRawBuf, uncompressedSize,
                    OodleLZ_FuzzSafe.OodleLZ_FuzzSafe_Yes, OodleLZ_CheckCRC.OodleLZ_CheckCRC_No,
                    OodleLZ_Verbosity.OodleLZ_Verbosity_None,
                    IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0,
                    OodleLZ_Decode_ThreadPhase.OodleLZ_Decode_Unthreaded);
            }
            if (rawLen != uncompressedSize)
                throw new InvalidOperationException($"Oodle decompressed {rawLen} bytes, expected {uncompressedSize}.");
            Array.Resize(ref rawBuf, (int)rawLen);
            return rawBuf;
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero)
                return;

            InteropNativeLibrary.Free(handle);
            handle = IntPtr.Zero;
        }

        private long CompressedBufferSizeNeeded(OodleLZ_Compressor compressor, long size)
        {
            if (getCompressedBufferSizeNeeded == IntPtr.Zero)
                return size + 274 * ((size + 0x3FFFF) / 0x40000) + 0x1000; // Oodle's documented worst case
            return isOodle26
                ? ((delegate* unmanaged<long, long>)getCompressedBufferSizeNeeded)(size)
                : ((delegate* unmanaged<OodleLZ_Compressor, long, long>)getCompressedBufferSizeNeeded)(compressor, size);
        }

        private static IntPtr TryGetExport(IntPtr handle, string name) =>
            InteropNativeLibrary.TryGetExport(handle, name, out IntPtr address) ? address : IntPtr.Zero;
    }
}
