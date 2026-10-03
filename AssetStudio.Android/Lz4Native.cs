using System;
using System.Runtime.InteropServices;
using AssetStudio;

namespace AssetStudioMobile
{
    /// <summary>
    /// The native LZ4 block decompressor, built from the vendored lz4.c by Lz4Native/build.sh.
    ///
    /// Installed over the managed default at startup. Every bundle block goes through this, and on
    /// the 4.1 GB cache that is 11.31 GB of decompression per pass, twice per export.
    /// </summary>
    internal static class Lz4Native
    {
        private const string Lib = "Lz4Native";

        [DllImport(Lib, EntryPoint = "LZ4_decompress_safe", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int LZ4_decompress_safe(byte* source, byte* destination,
                                                             int compressedSize, int destinationCapacity);

        /// <summary>
        /// Wired into <see cref="BundleDecompressionHelper.Lz4Decoder"/>. LZ4_decompress_safe
        /// returns the byte count or a negative error, which is the contract that delegate wants.
        /// </summary>
        public static unsafe int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            fixed (byte* src = source)
            fixed (byte* dst = destination)
            {
                return LZ4_decompress_safe(src, dst, source.Length, destination.Length);
            }
        }

        public static void Install() => BundleDecompressionHelper.Lz4Decoder = Decode;
    }
}
