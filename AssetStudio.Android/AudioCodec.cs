using System;
using AssetStudio;

namespace AssetStudioMobile
{
    /// <summary>
    /// Turns an AudioClip into bytes a player can read, and says what extension they want.
    ///
    /// Three converters, tried in order of how much each covers: legacy clips are a header plus the
    /// raw bytes and need none at all; FMOD when the native library is there, which is what the
    /// desktop build uses; and Fmod5Sharp, a managed FSB5 reader, which is what makes this work on
    /// Android. Shared by the export and by the preview's cache so both produce the same file.
    /// </summary>
    internal static class AudioCodec
    {
        /// <summary>
        /// The playable bytes for a clip, or null with <paramref name="error"/> set. Safe to call
        /// from a background thread.
        /// </summary>
        public static byte[] Convert(AudioClip clip, out string extension, out string error)
        {
            extension = null;
            error = null;

            if (clip?.m_AudioData == null)
            {
                error = "这个 AudioClip 的音频数据被 strip 掉了";
                return null;
            }

            var data = BigArrayPool<byte>.Shared.Rent(clip.m_AudioData.Size);
            try
            {
                var length = clip.m_AudioData.GetData(data);
                if (length <= 0)
                {
                    error = "读不到音频数据";
                    return null;
                }

                var converter = new AudioClipConverter(clip);

                // Legacy here means the reader holds the whole clip rather than a resource pointer.
                // It is written straight to a wav header with no decoder involved, so it works even
                // where FMOD cannot load.
                if (converter.IsLegacy)
                {
                    var log = string.Empty;
                    var raw = converter.RawAudioClipToWav(ref log);
                    if (raw != null && raw.Length > 44)
                    {
                        extension = ".wav";
                        return raw;
                    }
                }
                else if (AudioClipConverter.UnavailableReason == null)
                {
                    var debug = string.Empty;
                    var wav = converter.ConvertToWav(data, ref debug);
                    if (wav != null && wav.Length > 44)
                    {
                        extension = ".wav";
                        return wav;
                    }
                }

                // No FMOD: read the FSB5 container in managed code. This is the Android path.
                var managed = Ui.FsbAudio.Write(data, length, out var managedExtension, out var managedError);
                if (managed != null)
                {
                    extension = managedExtension;
                    return managed;
                }

                error = managedError ?? "转换失败";
                return null;
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(data);
            }
        }
    }
}
