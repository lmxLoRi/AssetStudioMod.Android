using System;
using System.IO;
using System.Text;
using AssetStudio;

namespace AssetStudioMobile.Ui
{
    /// <summary>
    /// Turns an AudioClip into a file on disk so MediaPlayer can play it.
    ///
    /// MediaPlayer only takes a path or a file descriptor, and the asset is compressed audio inside
    /// a bundle, so it has to be converted and written before anything can play it. The result is
    /// cached under the app's cache directory, keyed by path id, because converting does real work
    /// and previewing the same clip twice should not do it twice.
    ///
    /// Three converters, tried in order of how much they cover:
    ///   - legacy clips are a header plus the raw bytes and need no decoder at all;
    ///   - FMOD, when the native library is there, which is what the desktop build uses;
    ///   - Fmod5Sharp, a managed FSB5 reader, which is what makes this work on Android at all.
    /// </summary>
    internal static class AudioCache
    {
        /// <summary>A wav header alone is 44 bytes; anything at or below that is a truncated file.</summary>
        private const int MinimumBytes = 45;

        /// <summary>
        /// Returns the path to a playable file -- .wav or .ogg depending on what the clip holds --
        /// or null with <paramref name="error"/> set. Safe to call from a background thread.
        /// </summary>
        public static string FileFor(AudioClip clip, string cacheDirectory, out string error)
        {
            error = null;

            if (clip.m_AudioData == null)
            {
                error = "这个 AudioClip 的音频数据被 strip 掉了";
                return null;
            }

            // The stem is the same for either outcome so that looking for a cached conversion does
            // not have to know which converter produced it.
            var stem = Path.Combine(cacheDirectory, "audio", $"{clip.m_PathID}_{Safe(clip.m_Name)}");
            foreach (var extension in CachedExtensions)
            {
                var cached = stem + extension;
                if (File.Exists(cached) && new FileInfo(cached).Length >= MinimumBytes) return cached;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(stem));

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
                    var legacyLog = string.Empty;
                    var raw = converter.RawAudioClipToWav(ref legacyLog);
                    if (raw != null && raw.Length >= MinimumBytes) return Save(stem, ".wav", raw);
                }
                else if (AudioClipConverter.UnavailableReason == null)
                {
                    // FMOD covers every format the tool has ever supported, so it goes first when it
                    // is present and the desktop behaviour is unchanged.
                    var debug = string.Empty;
                    var wav = converter.ConvertToWav(data, ref debug);
                    if (wav != null && wav.Length >= MinimumBytes) return Save(stem, ".wav", wav);
                }

                // No FMOD: read the FSB5 container in managed code. This is the Android path.
                var managed = FsbAudio.Write(data, length, stem, out var managedError);
                if (managed != null) return managed;

                error = AudioClipConverter.UnavailableReason != null
                    ? managedError
                    : "转换失败";
                return null;
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(data);
            }
        }

        private static readonly string[] CachedExtensions = { ".wav", ".ogg" };

        private static string Save(string stem, string extension, byte[] payload)
        {
            var path = stem + extension;
            File.WriteAllBytes(path, payload);
            return path;
        }

        /// <summary>Clip names come from the game and become file names; keep them to one path segment.</summary>
        private static string Safe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "clip";

            var builder = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
            }

            var result = builder.ToString().Trim('.');
            if (result.Length == 0) result = "clip";
            return result.Length > 64 ? result.Substring(0, 64) : result;
        }
    }
}
