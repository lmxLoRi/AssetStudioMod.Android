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

        public static Image<Bgra32> ConvertToImage(this Texture2D m_Texture2D, bool flip)
        {
            var converter = new Texture2DConverter(m_Texture2D);
            var mark = System.Diagnostics.Stopwatch.GetTimestamp();
            var buff = BigArrayPool<byte>.Shared.Rent(converter.OutputDataSize);
            Phase("rent buffer", mark);

            var spanBuff = buff.AsSpan(0, converter.OutputDataSize);
            try
            {
                mark = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!converter.DecodeTexture2D(buff))
                    return null;
                Phase("codec decode", mark);

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
                Phase("LoadPixelData", mark);

                if (flip)
                {
                    mark = System.Diagnostics.Stopwatch.GetTimestamp();
                    image.Mutate(x => x.Flip(FlipMode.Vertical));
                    Phase("flip", mark);
                }
                return image;
            }
            finally
            {
                mark = System.Diagnostics.Stopwatch.GetTimestamp();
                BigArrayPool<byte>.Shared.Return(buff, clearArray: true);
                Phase("return+clear", mark);
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
