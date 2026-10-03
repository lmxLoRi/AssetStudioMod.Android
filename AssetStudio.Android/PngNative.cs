using System;
using System.Runtime.InteropServices;
using AssetStudio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AssetStudioMobile
{
    /// <summary>
    /// The native PNG writer from the vendored PngNative/png_write.c, over the platform zlib.
    ///
    /// ImageSharp's PNG encoder was 73% of the CPU in a texture export (882s for 8487 files,
    /// measured in-process), because its deflate is managed scalar code. Android ships zlib, which
    /// is what libpng uses, so this covers the same ground with a real optimised deflate.
    /// </summary>
    internal static class PngNative
    {
        private const string Lib = "PngNative";

        [DllImport(Lib, EntryPoint = "png_write_bgra8", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int png_write_bgra8([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                         byte* pixels, int width, int height, int level);

        private static bool _probed;
        private static bool _available;

        /// <summary>False when the library is not in the APK, so callers can fall back to ImageSharp.</summary>
        public static bool Available
        {
            get
            {
                if (!_probed)
                {
                    _probed = true;
                    try
                    {
                        NativeLibrary.Load(Lib);
                        _available = true;
                    }
                    catch
                    {
                        _available = false;
                    }
                }
                return _available;
            }
        }

        /// <summary>Writes <paramref name="image"/> as an 8-bit RGBA PNG. Returns false on failure.</summary>
        public static unsafe bool TryWrite(string path, Image<Bgra32> image, int level)
        {
            var length = image.Width * 4 * image.Height;
            var bytes = BigArrayPool<byte>.Shared.Rent(length);
            try
            {
                image.CopyPixelDataTo(bytes.AsSpan(0, length));
                fixed (byte* pixels = bytes)
                {
                    return png_write_bgra8(path, pixels, image.Width, image.Height, level) == 0;
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(bytes);
            }
        }
    }
}
