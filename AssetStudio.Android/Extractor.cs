using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;
using SixLabors.ImageSharp;

namespace AssetStudioMobile
{
    public enum ExportKind
    {
        Texture,
        Sprite,
        JsonDump,
        TextAsset,
        RawData,
    }

    public sealed class ExportOptions
    {
        public ExportKind Kind = ExportKind.Texture;
        public bool Overwrite;
        public bool FlipTextures = true;
        public SpriteMaskMode SpriteMask = SpriteMaskMode.Off;
        public ImageFormat ImageFormat = ImageFormat.Png;
    }

    public sealed class ExportReport
    {
        public int Matched;
        public int Exported;
        public int Skipped;
        public int Failed;
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>
    /// Loads Unity bundles/serialized files from a directory tree and exports assets.
    ///
    /// AssetStudio's loader is entirely synchronous and has no cancellation support, so this
    /// type is deliberately synchronous too and callers must run it off the UI thread.
    /// </summary>
    public sealed class Extractor : IDisposable
    {
        private readonly AssetsManager _assetsManager = new AssetsManager();

        public Action<string> Info;
        public Action<string> Warn;
        public Action<int, int> Progress;

        private void LogInfo(string m) => Info?.Invoke(m);
        private void LogWarn(string m) => Warn?.Invoke(m);
        private void Report(int cur, int total) => Progress?.Invoke(cur, total);

        public IEnumerable<SerializedFile> Files => _assetsManager.AssetsFileList;

        public int CountAssets() => _assetsManager.AssetsFileList.Sum(f => f.Objects.Count);

        /// <summary>Recursively scans <paramref name="root"/> and loads every Unity file found.</summary>
        public void Load(string root)
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

            var full = Path.GetFullPath(root);
            LogInfo($"Scanning {full}");
            var candidates = Directory.GetFiles(full, "*.*", SearchOption.AllDirectories)
                .Where(IsCandidateUnityFile)
                .ToList();
            LogInfo($"{candidates.Count} candidate file(s)");
            Report(0, candidates.Count);

            // AssetsManager.LoadFilesAndFolders runs every entry through Path.GetFullPath, so it
            // needs absolute paths. Relative names would silently resolve against the process
            // working directory, which on Android is "/" and matches nothing.
            // (It also clears the list it is given, hence the local.)
            var paths = new List<string>(candidates);
            _assetsManager.LoadFilesAndFolders(out var actualParent, paths);
            LogInfo($"Loaded {_assetsManager.AssetsFileList.Count} serialized file(s), " +
                    $"{CountAssets()} object(s) from {actualParent}");
            Report(candidates.Count, candidates.Count);
        }

        private static bool IsCandidateUnityFile(string path)
        {
            try
            {
                if (new FileInfo(path).Length < 8) return false;
            }
            catch
            {
                return false;
            }

            // Skip obvious non-assets so we do not pull every unrelated file in the tree.
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".mp3":
                case ".ogg":
                case ".wav":
                case ".dll":
                case ".so":
                case ".json":
                case ".txt":
                case ".xml":
                    return false;
                default:
                    return true;
            }
        }

        public ExportReport Export(string outputRoot, ExportOptions options)
        {
            var report = new ExportReport();
            Directory.CreateDirectory(outputRoot);

            var targets = new List<AssetStudio.Object>();
            foreach (var f in _assetsManager.AssetsFileList)
            {
                if (f?.Objects == null) continue;
                foreach (var o in f.Objects)
                {
                    if (o != null && Matches(o, options.Kind)) targets.Add(o);
                }
            }
            report.Matched = targets.Count;
            LogInfo($"{targets.Count} asset(s) matched {options.Kind}");
            Report(0, targets.Count);

            var done = 0;
            foreach (var obj in targets)
            {
                var dest = Path.Combine(outputRoot, DisplayName(obj) + ExtensionFor(options.Kind));
                try
                {
                    if (File.Exists(dest) && !options.Overwrite)
                    {
                        report.Skipped++;
                    }
                    else
                    {
                        ExportOne(obj, dest, options);
                        report.Exported++;
                    }
                }
                catch (Exception ex)
                {
                    report.Failed++;
                    var msg = $"{DisplayName(obj)} ({obj.type}): {ex.GetType().Name}: {ex.Message}";
                    report.Errors.Add(msg);
                    LogWarn(msg);
                }
                finally
                {
                    Report(++done, targets.Count);
                }
            }
            return report;
        }

        private static bool Matches(AssetStudio.Object o, ExportKind kind) => kind switch
        {
            ExportKind.Texture => o is Texture2D t && t.m_Width > 0 && t.m_Height > 0,
            ExportKind.Sprite => o is Sprite s && s.m_Rect.width > 0 && s.m_Rect.height > 0,
            ExportKind.JsonDump => true,
            ExportKind.TextAsset => o is TextAsset,
            ExportKind.RawData => true,
            _ => false,
        };

        private static string ExtensionFor(ExportKind kind) => kind switch
        {
            ExportKind.Texture => ".png",
            ExportKind.Sprite => ".png",
            ExportKind.TextAsset => ".txt",
            _ => ".json",
        };

        private void ExportOne(AssetStudio.Object obj, string dest, ExportOptions options)
        {
            switch (options.Kind)
            {
                case ExportKind.Texture:
                    WriteImage(((Texture2D)obj).ConvertToImage(options.FlipTextures), dest, options);
                    break;

                case ExportKind.Sprite:
                    WriteImage(((Sprite)obj).GetImage(options.SpriteMask), dest, options);
                    break;

                case ExportKind.TextAsset:
                    File.WriteAllBytes(dest, ((TextAsset)obj).m_Script);
                    break;

                case ExportKind.RawData:
                    File.WriteAllBytes(dest, obj.GetRawData());
                    break;

                case ExportKind.JsonDump:
                    // Dump() needs an embedded type tree (enableTypeTree bundles only);
                    // DumpObject() reflects over the parsed fields and always applies.
                    var json = obj.Dump() ?? obj.DumpObject();
                    if (string.IsNullOrEmpty(json))
                        throw new InvalidOperationException("no typetree and no dumpable fields");
                    File.WriteAllText(dest, json, new UTF8Encoding(false));
                    break;
            }
        }

        private static void WriteImage(Image image, string dest, ExportOptions options)
        {
            if (image == null)
                throw new InvalidOperationException("texture decode returned no image (unsupported format?)");

            using (image)
            using (var fs = File.Create(dest))
            {
                image.WriteToStream(fs, options.ImageFormat);
            }
        }

        /// <summary>
        /// Human-readable name for an asset.
        ///
        /// Object.Name is a public field that AssetStudio never assigns -- the real name lives in
        /// NamedObject.m_Name -- so it must not be used for file naming. The pathID suffix keeps
        /// names unique when several assets share a name (common across bundles/containers).
        /// </summary>
        internal static string DisplayName(AssetStudio.Object obj)
        {
            var name = (obj as NamedObject)?.m_Name;
            var safe = SafeName(name);
            return $"{safe}_{obj.m_PathID}";
        }

        internal static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";

            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                // Also filter the Windows-illegal set: Path.GetInvalidFileNameChars() reports
                // only '\0' and '/' on Unix, which would let ':' '*' '?' through and produce
                // names that cannot be copied back off the device.
                sb.Append(c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
            }

            var s = sb.ToString().Trim().TrimEnd('.');
            if (s.Length > 120) s = s.Substring(0, 120).TrimEnd();
            return s.Length == 0 ? "unnamed" : s;
        }

        public void Clear() => _assetsManager.Clear();

        public void Dispose() => Clear();
    }
}
