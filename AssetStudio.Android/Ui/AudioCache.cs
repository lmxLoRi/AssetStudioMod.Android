using System;
using System.IO;
using System.Text;
using AssetStudio;

namespace AssetStudioMobile.Ui
{
    /// <summary>
    /// Turns an AudioClip into a wav on disk so MediaPlayer can play it.
    ///
    /// MediaPlayer only takes a path or a file descriptor, and the asset is compressed audio inside
    /// a bundle, so it has to be converted and written before anything can play it. The result is
    /// cached under the app's cache directory, keyed by path id, because converting is FMOD doing
    /// real work and previewing the same clip twice should not do it twice.
    /// </summary>
    internal static class AudioCache
    {
        /// <summary>A wav header is 44 bytes; anything at or below that is a truncated file.</summary>
        private const int MinimumWavBytes = 45;

        /// <summary>
        /// Returns the path to a playable wav, or null with <paramref name="error"/> set. Safe to
        /// call from a background thread.
        /// </summary>
        public static string WavFor(AudioClip clip, string cacheDirectory, out string error)
        {
            error = null;

            if (clip.m_AudioData == null)
            {
                error = "这个 AudioClip 的音频数据被 strip 掉了";
                return null;
            }

            var path = Path.Combine(cacheDirectory, "audio", $"{clip.m_PathID}_{Safe(clip.m_Name)}.wav");
            if (File.Exists(path) && new FileInfo(path).Length >= MinimumWavBytes) return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path));

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

                // Legacy clips are written straight to a wav header with no decoder involved, so
                // they work even where FMOD cannot load. Everything else needs it.
                if (!converter.IsLegacy && AudioClipConverter.UnavailableReason != null)
                {
                    error = "这个构建没有音频解码后端（FMOD），Android 上还没有可用的原生库";
                    return null;
                }

                if (!converter.IsSupport && !converter.IsLegacy)
                {
                    error = $"暂不支持这个压缩格式（{clip.m_CompressionFormat}）";
                    return null;
                }

                var debug = string.Empty;

                // Legacy here means the reader holds the whole clip rather than a resource pointer,
                // and it takes a different conversion entry point.
                var wav = converter.IsLegacy
                    ? converter.RawAudioClipToWav(ref debug)
                    : converter.ConvertToWav(data, ref debug);

                if (wav == null || wav.Length < MinimumWavBytes)
                {
                    // The library is only discovered to be missing once something tries to use it, so
                    // the reason is known here rather than before the attempt.
                    error = AudioClipConverter.UnavailableReason != null
                        ? "这个构建没有音频解码后端（FMOD），Android 上还没有可用的原生库"
                        : string.IsNullOrWhiteSpace(debug) ? "转换失败" : debug.Trim();
                    return null;
                }

                File.WriteAllBytes(path, wav);
                return path;
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(data);
            }
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
