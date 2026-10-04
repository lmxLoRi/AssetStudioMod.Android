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

            // The stem is the same for either outcome so that looking for a cached conversion does
            // not have to know which converter produced it.
            var stem = Path.Combine(cacheDirectory, "audio", $"{clip.m_PathID}_{Safe(clip.m_Name)}");
            foreach (var extension in CachedExtensions)
            {
                var cached = stem + extension;
                if (File.Exists(cached) && new FileInfo(cached).Length >= MinimumBytes) return cached;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(stem));

            var payload = AudioCodec.Convert(clip, out var realExtension, out error);
            if (payload == null) return null;

            var path = stem + realExtension;
            File.WriteAllBytes(path, payload);
            return path;
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
