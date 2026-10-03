using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;
using K4os.Compression.LZ4;
using ZstdSharp;
using Texture2DDecoder;

namespace AssetStudioMobile
{
    /// <summary>
    /// On-device smoke test for the pieces that behave differently on Android/bionic:
    /// every bundle decompressor plus the NDK-built Texture2DDecoderNative.
    /// Runs without any input files, so a fresh install can prove the native stack works.
    /// </summary>
    public static class SelfTest
    {
        public static void AppendResults(Action<string> log)
        {
            int pass = 0, fail = 0;
            void Check(string name, Action a)
            {
                try { a(); log?.Invoke($"PASS  {name}"); pass++; }
                catch (Exception ex) { log?.Invoke($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}"); fail++; }
            }
            void Assert(bool c, string m) { if (!c) throw new Exception(m); }

            log?.Invoke("--- self-test ---");

            // 1. UnityVersion: Regex-driven, runs on every single bundle load.
            Check("UnityVersion.Parse", () =>
            {
                var v = new UnityVersion("2019.4.40f1");
                Assert(v.Major == 2019 && v.Minor == 4, $"got {v}");
                Assert(v >= new UnityVersion("5.6.0f1"), "comparison failed");
            });

            // 2. Endian readers: Span + BinaryPrimitives + byte-swapping, the hot parse path.
            Check("EndianBinaryReader", () =>
            {
                // 1.0f and -100.0f, little endian. -100.0f is 0x00 0x00 0xC8 0xC2.
                var bytes = new byte[] { 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0xC8, 0xC2 };
                using var ms = new MemoryStream(bytes);
                using var br = new EndianBinaryReader(ms, EndianType.LittleEndian);
                Assert(Math.Abs(br.ReadSingle() - 1.0f) < 1e-6, "LE float");
                Assert(Math.Abs(br.ReadSingle() + 100.0f) < 1e-6, "LE float 2");
            });

            // 3. Half-float (Mesh vertex data goes through this on every mesh).
            Check("half float (Mesh vertex data)", () =>
            {
                var bytes = new byte[] { 0x00, 0x3C, 0x42, 0xC8 }; // 1.0h, -100.0h
                var a = HalfHelper.ToHalf(bytes, 0);
                Assert(Math.Abs((float)a - 1.0f) < 1e-3, $"half = {a}");
            });

            // 4. Vendored Brotli decoder (240 KB static dictionary embedded in the assembly).
            //    Compress with the BCL encoder and decode with the vendored one, so the vendored
            //    decoder is checked against a reference implementation.
            Check("Brotli (vendored decoder)", () =>
            {
                var raw = Encoding.UTF8.GetBytes(new string('B', 4096));

                byte[] compressed;
                using (var tmp = new MemoryStream())
                {
                    using (var enc = new System.IO.Compression.BrotliStream(
                               tmp, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                        enc.Write(raw, 0, raw.Length);
                    compressed = tmp.ToArray();
                }
                Assert(compressed.Length > 0, "brotli compression produced nothing");

                using var outMs = new MemoryStream();
                using (var dec = new Org.Brotli.Dec.BrotliInputStream(new MemoryStream(compressed)))
                    dec.CopyTo(outMs);

                var back = outMs.ToArray();
                Assert(back.Length == raw.Length, $"size {back.Length} != {raw.Length}");
                Assert(back.SequenceEqual(raw), "content mismatch");
            });

            // 5. LZMA (vendored 7-Zip codec) round-trip through the same entry point
            //    BundleDecompressionHelper uses. DecompressStream expects a real .lzma
            //    container: 5 property bytes, then the uncompressed size as 8 LE bytes,
            //    then the raw LZMA stream. Omitting that header makes it read payload bytes
            //    as a bogus output size.
            Check("LZMA round-trip", () =>
            {
                var raw = Encoding.UTF8.GetBytes(new string('L', 8192));

                var packed = new MemoryStream();
                var encoder = new SevenZip.Compression.LZMA.Encoder();
                encoder.WriteCoderProperties(packed);
                long uncompressedSize = raw.Length;
                for (var i = 0; i < 8; i++) packed.WriteByte((byte)(uncompressedSize >> (8 * i)));
                encoder.Code(new MemoryStream(raw), packed, raw.Length, -1, null);

                var compressed = packed.ToArray();
                Assert(compressed.Length > 13, $"encoder produced only {compressed.Length} bytes");

                using var inflated = BundleCompression.Lzma.SevenZipLzma.DecompressStream(new MemoryStream(compressed));
                var back = inflated.ToArray();
                Assert(back.Length == raw.Length, $"size {back.Length} != {raw.Length}");
                Assert(back.SequenceEqual(raw), "content mismatch");
            });

            // 6. LZ4 (K4os, managed) -- the most common Unity bundle compression.
            //    The span-only Decode overloads have no compressed length, so the explicit
            //    offset/length overloads must be used.
            Check("LZ4 block", () =>
            {
                var raw = Encoding.UTF8.GetBytes(new string('Z', 4096));
                var target = new byte[LZ4Codec.MaximumOutputSize(raw.Length)];

                var compressed = LZ4Codec.Encode(raw, 0, raw.Length, target, 0, target.Length, LZ4Level.L00_FAST);
                Assert(compressed > 0, "encode failed");

                var back = new byte[raw.Length];
                var decoded = LZ4Codec.Decode(target, 0, compressed, back, 0, back.Length);
                Assert(decoded == raw.Length, $"decode size {decoded} != {raw.Length}");
                Assert(back.SequenceEqual(raw), "content mismatch");
            });

            // 7. Zstd (ZstdSharp.Port 0.8.x exposes only the streaming Wrap/Unwrap API, which is
            //    exactly what BundleDecompressionHelper calls).
            Check("Zstd Wrap/Unwrap", () =>
            {
                var raw = Encoding.UTF8.GetBytes(new string('S', 4096));

                var packed = new byte[ZstdSharp.Compressor.GetCompressBoundLong((ulong)raw.Length)];
                var n = new ZstdSharp.Compressor().Wrap(raw, packed);
                Assert(n > 0, "Wrap returned 0");

                var back = new byte[raw.Length];
                var m = new ZstdSharp.Decompressor().Unwrap(new ReadOnlySpan<byte>(packed, 0, (int)n), back);
                Assert(m == raw.Length, $"Unwrap returned {m}");
                Assert(back.SequenceEqual(raw), "content mismatch");
            });

            // 8. GZipStream / System.IO.Compression (ImportHelper path).
            Check("GZipStream", () =>
            {
                var ms = new MemoryStream();
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                    gz.Write(new byte[256], 0, 256);
                Assert(ms.Length > 0, "no output");
            });

            // 9. BigArrayPool: 256 MB x 5 buckets are rough on ART.
            Check("BigArrayPool<byte> 8 MB", () =>
            {
                var buf = BigArrayPool<byte>.Shared.Rent(8 * 1024 * 1024);
                Assert(buf.Length >= 8 * 1024 * 1024, $"got {buf.Length}");
                BigArrayPool<byte>.Shared.Return(buf, true);
            });

            // 10. NDK-built native library. This is the one that proves jniLibs shipped correctly.
            Check("native Texture2DDecoderNative (BC1)", () =>
            {
                // 4x4 BC1 block: all-zero colour endpoints, 16 zero indices -> opaque black.
                var block = new byte[8];
                var pixels = new byte[4 * 4 * 4];
                Assert(TextureDecoder.DecodeDXT1(block, 4, 4, pixels), "DecodeDXT1 returned false");
                Assert(pixels[3] == 255, $"alpha should be opaque, got {pixels[3]}");
            });

            Check("native Texture2DDecoderNative (ASTC)", () =>
            {
                // 16-byte ASTC block header of zeros = LDR, 4x4, single level.
                var block = new byte[16];
                var pixels = new byte[4 * 4 * 4];
                Assert(TextureDecoder.DecodeASTC(block, 4, 4, 4, 4, pixels), "DecodeASTC returned false");
            });

            // 11. System.Text.Json reflection (Object.DumpObject path).
            Check("System.Text.Json reflection", () =>
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new Probe { Name = "tex", Count = 7 });
                Assert(json.Contains("tex") && json.Contains("7"), json);
            });

            // 12. ImageSharp encoding (final PNG write).
            Check("ImageSharp PNG encode", () =>
            {
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32>(4, 4);
                using var ms = new MemoryStream();
                img.WriteToStream(ms, ImageFormat.Png);
                Assert(ms.Length > 0, "no png bytes");
                Assert(ms.ToArray()[1] == 'P', "missing PNG signature");
            });

            // 13. UnityFS header detection through the real FileReader.
            Check("FileReader UnityFS detect", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "ast-selftest");
                Directory.CreateDirectory(dir);
                var p = Path.Combine(dir, "synthetic.unity3d");
                File.WriteAllBytes(p, new byte[]
                {
                    // "UnityFS\0" -- case matters, FileReader switches on the exact signature.
                    0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00,
                    0x06, 0x00, 0x00, 0x00,
                    0x18, 0x2D, 0x18, 0x6F,
                });
                using var fr = new FileReader(p);
                Assert(fr.FileType == FileType.BundleFile, $"got {fr.FileType}");
                File.Delete(p);
            });

            log?.Invoke($"--- {pass} passed, {fail} failed ---");
        }

        private sealed class Probe
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }
    }
}
