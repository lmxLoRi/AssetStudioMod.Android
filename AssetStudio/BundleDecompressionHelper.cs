using BundleCompression.Lzma;
using BundleCompression.Oodle;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace AssetStudio
{
    /// <summary>
    /// Decompresses one LZ4 block. Returns the number of bytes written, or a negative value on
    /// failure -- the same contract as K4os.Compression.LZ4.LZ4Codec.Decode.
    ///
    /// A delegate rather than a direct call because the parameter types are spans, which cannot be
    /// generic arguments, and because the right implementation is platform specific: K4os is fine
    /// on x64 but has no ARM64 SIMD path.
    /// </summary>
    public delegate int Lz4BlockDecoder(ReadOnlySpan<byte> source, Span<byte> destination);

    public static class BundleDecompressionHelper
    {
        /// <summary>
        /// Optional platform override for LZ4 block decompression. Null means use K4os.
        ///
        /// The Android app installs a native liblz4 here: K4os falls back to scalar managed code on
        /// ARM64, which decompressed the 4.1 GB cache's 11.31 GB of blocks at ~207 MB/s.
        /// </summary>
        public static Lz4BlockDecoder Lz4Decoder;

        // Per thread, not shared: ZstdSharp's Decompressor keeps its working state in the
        // instance, and batches are now decompressed by several workers at once.
        [ThreadStatic] private static Decompressor _zstdDecompressor;

        private static Decompressor ZstdDecompressor => _zstdDecompressor ??= new Decompressor();
        private static readonly string MsgPattern = @"\. ";

        public static MemoryStream DecompressLzmaStream(MemoryStream inStream)
        {
            return SevenZipLzma.DecompressStream(inStream);
        }

        public static long DecompressLzmaStream(Stream compressedStream, Stream decompressedStream, long compressedSize, long decompressedSize, ref string errorMsg)
        {
            var numWrite = -1L;
            try
            {
                numWrite = SevenZipLzma.DecompressStream(compressedStream, decompressedStream, compressedSize, decompressedSize);
            }
            catch (Exception e)
            {
                Logger.Debug(e.ToString());
                errorMsg = $"({Regex.Split(e.Message, MsgPattern, RegexOptions.CultureInvariant)[0]})";
            }
            return numWrite;
        }

        public static int DecompressBlock(CompressionType type, ReadOnlySpan<byte> srcBuffer, Span<byte> dstBuffer, ref string errorMsg)
        {
            var numWrite = -1;
            try
            {
                switch (type)
                {
                    case CompressionType.Lz4:
                    case CompressionType.Lz4HC:
                        numWrite = Lz4Decoder != null
                            ? Lz4Decoder(srcBuffer, dstBuffer)
                            : LZ4Codec.Decode(srcBuffer, dstBuffer);
                        break;
                    case CompressionType.Zstd:
                        numWrite = ZstdDecompressor.Unwrap(srcBuffer, dstBuffer);
                        break;
                    case CompressionType.Oodle:
                        numWrite = OodleLZ.Decompress(srcBuffer, dstBuffer);
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }
            catch (Exception e)
            {
                Logger.Debug(e.ToString());
                errorMsg = $"({Regex.Split(e.Message, MsgPattern, RegexOptions.CultureInvariant)[0]})";
            }
            return numWrite;
        }
    }
}
