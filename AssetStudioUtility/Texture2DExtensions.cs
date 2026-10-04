using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AssetStudio
{
    public static class Texture2DExtensions
    {
        /// <summary>
        /// Optional per-phase timing hook: (phase name, elapsed Stopwatch ticks). Null costs nothing.
        ///
        /// ConvertToImage is one opaque number in the export stats, and the format breakdown showed
        /// a suspiciously flat ~20-25 MPix/s across everything from ASTC to a plain RGBA32 copy --
        /// which says the cost is in the code around the codec, not the codec. This is what tells
        /// the three apart instead of assuming which one it is.
        /// </summary>
        public static Action<string, long> PhaseTiming;

        private static void Phase(string name, long startTicks)
            => PhaseTiming?.Invoke(name, System.Diagnostics.Stopwatch.GetTimestamp() - startTicks);

        /// <summary>
        /// Decodes straight to a rented BGRA buffer, without building the ImageSharp image the export
        /// does not need -- the PNG encoder wants the raw pixels, and LoadPixelData was 39.4s of CPU
        /// for 8487 textures just allocating and copying into an intermediate.
        ///
        /// Returns null when the texture needs the swizzle path, which still has to go through
        /// ConvertToImage because that is where the unswizzle and crop live; Switch textures do not
        /// occur in the Android games this is for, so the fallback costs nothing in practice.
        ///
        /// The caller owns the buffer and must return it with <see cref="ReturnDecodedBuffer"/>.
        /// </summary>
        public static byte[] DecodeToBgraBuffer(this Texture2D m_Texture2D, out int width, out int height)
        {
            width = m_Texture2D.m_Width;
            height = m_Texture2D.m_Height;

            var converter = new Texture2DConverter(m_Texture2D);
            if (converter.UsesSwitchSwizzle) return null;

            var buff = BigArrayPool<byte>.Shared.Rent(converter.OutputDataSize);
            if (!converter.DecodeTexture2D(buff))
            {
                BigArrayPool<byte>.Shared.Return(buff, clearArray: true);
                return null;
            }
            return buff;
        }

        public static void ReturnDecodedBuffer(byte[] buffer)
        {
            if (buffer != null) BigArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        public static Image<Bgra32> ConvertToImage(this Texture2D m_Texture2D, bool flip)
        {
            var converter = new Texture2DConverter(m_Texture2D);
            var mark = System.Diagnostics.Stopwatch.GetTimestamp();
            var buff = BigArrayPool<byte>.Shared.Rent(converter.OutputDataSize);
            Phase("租借缓冲", mark);

            var spanBuff = buff.AsSpan(0, converter.OutputDataSize);
            try
            {
                mark = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!converter.DecodeTexture2D(buff))
                    return null;
                Phase("编解码", mark);

                mark = System.Diagnostics.Stopwatch.GetTimestamp();
                Image<Bgra32> image;
                if (converter.UsesSwitchSwizzle)
                {
                    var uncroppedSize = converter.GetUncroppedSize();
                    image = Image.LoadPixelData<Bgra32>(spanBuff, uncroppedSize.Width, uncroppedSize.Height);
                    image.Mutate(x => x.Crop(m_Texture2D.m_Width, m_Texture2D.m_Height));
                }
                else
                {
                    image = Image.LoadPixelData<Bgra32>(spanBuff, m_Texture2D.m_Width, m_Texture2D.m_Height);
                }
                Phase("拷贝进图", mark);

                if (flip)
                {
                    mark = System.Diagnostics.Stopwatch.GetTimestamp();
                    image.Mutate(x => x.Flip(FlipMode.Vertical));
                    Phase("翻转", mark);
                }
                return image;
            }
            finally
            {
                mark = System.Diagnostics.Stopwatch.GetTimestamp();
                BigArrayPool<byte>.Shared.Return(buff, clearArray: true);
                Phase("归还+清零", mark);
            }
        }

        public static MemoryStream ConvertToStream(this Texture2D m_Texture2D, ImageFormat imageFormat, bool flip)
        {
            var image = ConvertToImage(m_Texture2D, flip);
            if (image != null)
            {
                using (image)
                {
                    return image.ConvertToStream(imageFormat);
                }
            }
            return null;
        }
    }
}
