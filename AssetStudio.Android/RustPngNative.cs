using System;
using System.Runtime.InteropServices;
using AssetStudio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AssetStudioMobile
{
    /// <summary>
    /// The Rust PNG encoder from dev/rust-png-native, called across a C ABI.
    ///
    /// Not here for the language: it is here for the png crate's Compression::Fast, which routes to
    /// fdeflate, a deflate written specifically for fast PNG encoding. The C encoder next to it uses
    /// zlib level 1, which is general purpose. On desktop x86_64 that difference measured 6-7.6x on
    /// identical pixels; this exists to find out what it is on the phone.
    /// </summary>
    internal static class RustPngNative
    {
        private const string Lib = "RustPng";

        [DllImport(Lib, EntryPoint = "rust_png_write_bgra", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int rust_png_write_bgra([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                             byte* bgra, int width, int height,
                                                             int flipVertical, int assumeOpaque);

        private static bool _probed;
        private static bool _available;

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

        /// <summary>
        /// Writes <paramref name="image"/> as an 8-bit RGBA PNG. False on any failure.
        ///
        /// <paramref name="flipVertical"/> turns the image over while the rows are copied, which is
        /// where the caller's flip went: doing it here is the same bytes moved, and doing it in
        /// managed code first measured 60.2s of CPU for 8487 textures.
        /// </summary>
        public static unsafe bool TryWrite(string path, Image<Bgra32> image, bool flipVertical, bool assumeOpaque)
        {
            var length = image.Width * 4 * image.Height;
            var bytes = BigArrayPool<byte>.Shared.Rent(length);
            try
            {
                image.CopyPixelDataTo(bytes.AsSpan(0, length));
                fixed (byte* pixels = bytes)
                {
                    return rust_png_write_bgra(path, pixels, image.Width, image.Height,
                                               flipVertical ? 1 : 0, assumeOpaque ? 1 : 0) == 0;
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
