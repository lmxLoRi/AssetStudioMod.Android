using System;
using System.Diagnostics;
using System.Threading;

namespace AssetStudioMobile
{
    /// <summary>
    /// Counters for the phases that between them are the whole reason an export takes as long as
    /// it does. Every number here is measured inside the process, on the real work, rather than
    /// inferred from log timestamps or from how fast the same library is on a desktop -- which is
    /// how a wrong "LZ4 should be 1 GB/s" figure survived several rounds of guessing.
    ///
    /// Timestamps are Stopwatch ticks; the arithmetic is done once, in <see cref="Report"/>.
    /// </summary>
    internal static class Stats
    {
        private static long _lz4Ticks, _lz4Bytes;
        private static long _texTicks, _texCount, _texPixels;
        private static long _encodeTicks, _encodeCount;

        public static void AddLz4(long ticks, int bytes)
        {
            Interlocked.Add(ref _lz4Ticks, ticks);
            Interlocked.Add(ref _lz4Bytes, bytes);
        }

        public static void AddTexture(long ticks, long pixels)
        {
            Interlocked.Add(ref _texTicks, ticks);
            Interlocked.Add(ref _texCount, 1);
            Interlocked.Add(ref _texPixels, pixels);
        }

        public static void AddEncode(long ticks)
        {
            Interlocked.Add(ref _encodeTicks, ticks);
            Interlocked.Add(ref _encodeCount, 1);
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public static string Report()
        {
            var ms = Ms(Volatile.Read(ref _lz4Ticks));
            var bytes = Volatile.Read(ref _lz4Bytes);
            var tex = Ms(Volatile.Read(ref _texTicks));
            var texN = Volatile.Read(ref _texCount);
            var px = Volatile.Read(ref _texPixels);
            var enc = Ms(Volatile.Read(ref _encodeTicks));
            var encN = Volatile.Read(ref _encodeCount);

            var lz4 = bytes > 0
                ? $"    LZ4 decode      {ms / 1000,8:F1}s  {bytes / 1048576.0 / (ms / 1000),7:F0} MB/s  ({bytes / 1048576} MB in)\n"
                : "    LZ4 decode      (native decoder not installed)\n";
            var texture = texN > 0
                ? $"    texture decode  {tex / 1000,8:F1}s  {texN,8} images  {texN / (tex / 1000),6:F0}/s  {px / 1048576.0 / (tex / 1000),6:F0} MPix/s\n"
                : "    texture decode  (none)\n";
            var encode = encN > 0
                ? $"    PNG encode+write{enc / 1000,8:F1}s  {encN,8} files   {encN / (enc / 1000),6:F0}/s\n"
                : "    PNG encode+write(none)\n";

            return "\n--- measured in-process ---\n" + lz4 + texture + encode;
        }
    }
}
