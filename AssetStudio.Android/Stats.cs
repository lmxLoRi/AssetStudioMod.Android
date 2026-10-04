using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace AssetStudioMobile
{
    /// <summary>
    /// Counters for the phases that between them are the whole reason an export takes as long as it
    /// does. Every number here is measured inside the process, on the real work, rather than
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
        private static long _rawBytes, _rawCount;

        /// <summary>
        /// Texture decoding, split by the texture's own format.
        ///
        /// "Texture decoding is 200s" is not actionable on its own: DXT1, DXT5, ASTC, ETC2 and PVRTC
        /// are separate decoders in that library, and they are not equally good. This is what says
        /// which one to look at instead of guessing, which has been wrong often enough in this
        /// project to be worth the twenty lines.
        /// </summary>
        private sealed class FormatStat
        {
            public long Ticks;
            public long Count;
            public long Pixels;
        }

        private static readonly ConcurrentDictionary<string, FormatStat> ByFormat =
            new ConcurrentDictionary<string, FormatStat>(StringComparer.Ordinal);

        /// <summary>
        /// ConvertToImage split into its parts. Same reasoning as ByFormat: "decoding" is one number
        /// covering a codec, a copy and a flip, and they have very different fixes.
        /// </summary>
        private static readonly ConcurrentDictionary<string, long[]> ByPhase =
            new ConcurrentDictionary<string, long[]>(StringComparer.Ordinal);

        public static void AddPhase(string phase, long ticks)
        {
            var slot = ByPhase.GetOrAdd(phase ?? "?", _ => new long[2]);
            Interlocked.Add(ref slot[0], ticks);
            Interlocked.Add(ref slot[1], 1);
        }

        public static void AddLz4(long ticks, int bytes)
        {
            Interlocked.Add(ref _lz4Ticks, ticks);
            Interlocked.Add(ref _lz4Bytes, bytes);
        }

        public static void AddTexture(string format, long ticks, long pixels)
        {
            Interlocked.Add(ref _texTicks, ticks);
            Interlocked.Add(ref _texCount, 1);
            Interlocked.Add(ref _texPixels, pixels);

            var stat = ByFormat.GetOrAdd(format ?? "?", _ => new FormatStat());
            Interlocked.Add(ref stat.Ticks, ticks);
            Interlocked.Add(ref stat.Count, 1);
            Interlocked.Add(ref stat.Pixels, pixels);
        }

        public static void AddRaw(long bytes)
        {
            Interlocked.Add(ref _rawBytes, bytes);
            Interlocked.Add(ref _rawCount, 1);
        }

        public static void AddEncode(long ticks)
        {
            Interlocked.Add(ref _encodeTicks, ticks);
            Interlocked.Add(ref _encodeCount, 1);
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static double Hours(long ticks) => Ms(ticks) / 1000.0;

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("--- 进程内实测 ---");

            var lz4ms = Ms(Volatile.Read(ref _lz4Ticks));
            var lz4Bytes = Volatile.Read(ref _lz4Bytes);
            sb.AppendLine(lz4Bytes > 0
                ? $"    LZ4 解压          {lz4ms / 1000,8:F1}s  {lz4Bytes / 1048576.0 / (lz4ms / 1000),7:F0} MB/s  ({lz4Bytes / 1048576} MB in)"
                : "    LZ4 解压          （未装原生解码器）");

            var texMs = Ms(Volatile.Read(ref _texTicks));
            var texN = Volatile.Read(ref _texCount);
            var texPx = Volatile.Read(ref _texPixels);
            sb.AppendLine(texN > 0
                ? $"    贴图解码          {texMs / 1000,8:F1}s  {texN,8} images  {texN / (texMs / 1000),6:F0}/s  {texPx / 1048576.0 / (texMs / 1000),6:F0} MPix/s"
                : "    贴图解码          （无）");

            var encMs = Ms(Volatile.Read(ref _encodeTicks));
            var encN = Volatile.Read(ref _encodeCount);
            sb.AppendLine(encN > 0
                ? $"    PNG 编码+写出     {encMs / 1000,8:F1}s  {encN,8} files   {encN / (encMs / 1000),6:F0}/s"
                : "    PNG 编码+写出     （无）");

            var rawN = Volatile.Read(ref _rawCount);
            if (rawN > 0)
                sb.AppendLine($"    贴图原始字节      {Volatile.Read(ref _rawBytes) / 1048576,8} MB  {rawN,8} files");

            // Slowest format first, which is the point of keeping them apart.
            if (ByFormat.Count > 0)
            {
                sb.AppendLine("    按格式拆分贴图解码：");
                foreach (var kv in ByFormat.OrderByDescending(kv => Volatile.Read(ref kv.Value.Ticks)).Take(10))
                {
                    var s = kv.Value;
                    var ms = Ms(Volatile.Read(ref s.Ticks));
                    var n = Volatile.Read(ref s.Count);
                    var px = Volatile.Read(ref s.Pixels);
                    if (n == 0 || ms <= 0) continue;

                    sb.AppendLine($"      {kv.Key,-16} {ms / 1000,7:F1}s  {n,7} imgs  {ms / n,7:F1} ms each  {px / 1048576.0 / (ms / 1000),6:F0} MPix/s");
                }
            }

            if (ByPhase.Count > 0)
            {
                sb.AppendLine("    ConvertToImage 分阶段：");
                foreach (var kv in ByPhase.OrderByDescending(kv => Volatile.Read(ref kv.Value[0])))
                {
                    var ms = Ms(Volatile.Read(ref kv.Value[0]));
                    var n = Volatile.Read(ref kv.Value[1]);
                    if (n == 0 || ms <= 0) continue;
                    sb.AppendLine($"      {kv.Key,-16} {ms / 1000,7:F1}s  {n,7} calls  {ms / n,7:F2} ms each");
                }
            }

            return sb.ToString();
        }
    }
}
