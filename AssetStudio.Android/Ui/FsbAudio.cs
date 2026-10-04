using System;
using System.IO;
using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;

namespace AssetStudioMobile.Ui
{
    /// <summary>
    /// Reads Unity's audio out of its FSB5 container with a managed reader, so nothing native is
    /// needed to play it.
    ///
    /// Unity wraps its clips in an FSB5 bank, and AudioClipConverter opens that with FMOD, which is
    /// the piece this build does not have on Android. Fmod5Sharp does the same job in C#. For Vorbis
    /// -- which is what these games use -- it rebuilds a real Ogg rather than decoding to PCM, and
    /// that is what makes this cheap: Android's MediaPlayer plays Ogg Vorbis itself, so nothing here
    /// has to implement a codec.
    /// </summary>
    internal static class FsbAudio
    {
        /// <summary>
        /// Writes a playable file next to <paramref name="destinationStem"/> and returns its path
        /// with whatever extension the contained format calls for, or null with
        /// <paramref name="error"/> set.
        /// </summary>
        public static string Write(byte[] data, int length, string destinationStem, out string error)
        {
            error = null;
            try
            {
                // The pooled buffer is usually larger than the clip, and the reader validates its
                // own length, so hand it exactly the bytes that are the bank.
                var bytes = new byte[length];
                Buffer.BlockCopy(data, 0, bytes, 0, length);

                if (!FsbLoader.TryLoadFsbFromByteArray(bytes, out var bank))
                {
                    error = "不是有效的 FSB5 音频包（可能已被 strip，或不是 FSB5）";
                    return null;
                }

                if (bank.Samples == null || bank.Samples.Count == 0)
                {
                    error = "FSB5 包里没有采样";
                    return null;
                }

                var sample = bank.Samples[0];
                if (!sample.RebuildAsStandardFileFormat(out var payload, out var extension)
                    || payload == null || string.IsNullOrEmpty(extension))
                {
                    error = $"托管解码器还不支持这个编码（{bank.Header.AudioType}）";
                    return null;
                }

                var path = destinationStem + "." + extension;
                File.WriteAllBytes(path, payload);
                return path;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }
    }
}
