using System;
using System.IO;
using System.IO.Compression;

namespace AssetStudioMobile.Scripting
{
    /// <summary>
    /// What the loader reads when a script is loaded: a directory of plain files.
    ///
    /// Everything goes to the script and the loader reads the result. A ZIP or an APK is opened and
    /// its entries are handed over one at a time, so the loader never needs to know that the input
    /// was a container.
    /// </summary>
    internal static class DecryptPass
    {
        public static string Run(string input, string root, LuaDecryptor script, Action<string> log)
        {
            var name = Path.GetFileName(input.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(name)) name = "input";

            var dest = Path.Combine(root, Safe(name));
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);

            if (File.Exists(input))
            {
                if (IsZip(input))
                {
                    Directory.CreateDirectory(dest);
                    Zip(input, dest, script);
                }
                else
                {
                    Write(File.ReadAllBytes(input), Path.Combine(dest, Safe(Path.GetFileName(input))), Path.GetFileName(input), script);
                }
            }
            else if (Directory.Exists(input))
            {
                foreach (var file in Directory.GetFiles(input, "*.*", SearchOption.AllDirectories))
                {
                    if (IsZip(file))
                    {
                        var sub = Path.Combine(dest, Safe(Path.GetFileName(file)));
                        Directory.CreateDirectory(sub);
                        Zip(file, sub, script);
                        continue;
                    }

                    var target = Path.Combine(dest, Safe(Path.GetRelativePath(input, file)));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    Write(File.ReadAllBytes(file), target, Path.GetFileName(file), script);
                }
            }

            log?.Invoke($"lua:{script.Name} → {dest}");
            return dest;
        }

        private static void Zip(string path, string dest, LuaDecryptor script)
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length == 0 || entry.FullName.EndsWith("/")) continue;

                using var memory = new MemoryStream();
                using (var stream = entry.Open()) stream.CopyTo(memory);

                var target = Path.Combine(dest, Safe(entry.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                Write(memory.ToArray(), target, Path.GetFileName(entry.FullName), script);
            }
        }

        private static void Write(byte[] data, string target, string name, LuaDecryptor script)
        {
            var output = data;
            try
            {
                if (script.TryTransform(data, data.Length, name, out var transformed, out _)) output = transformed;
            }
            catch
            {
                // Left as read.
            }

            File.WriteAllBytes(target, output);
        }

        private static bool IsZip(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
            }
            catch
            {
                return false;
            }
        }

        private static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace("..", "_");
        }
    }
}
