using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;
using SixLabors.ImageSharp;

namespace AssetStudioMobile
{
    /// <summary>
    /// What to export. <see cref="Auto"/> dispatches on the asset's real ClassIDType and covers
    /// everything; the rest exist to export one category in isolation.
    /// </summary>
    public enum ExportKind
    {
        Auto,
        Texture,
        Sprite,
        Mesh,
        TextAsset,
        JsonDump,
        RawData,
    }

    public sealed class ExportOptions
    {
        public ExportKind Kind = ExportKind.Auto;
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

        /// <summary>How many assets produced output, keyed by ClassIDType.</summary>
        public readonly Dictionary<string, int> ByType = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Types that were seen but have no exporter, so they produced nothing.</summary>
        public readonly Dictionary<string, int> Unexported = new Dictionary<string, int>(StringComparer.Ordinal);

        public override string ToString()
        {
            var parts = new List<string>();
            foreach (var kv in ByType) parts.Add($"{kv.Key}={kv.Value}");
            if (Unexported.Count > 0)
            {
                var u = new List<string>();
                foreach (var kv in Unexported) u.Add($"{kv.Key}={kv.Value}");
                parts.Add("no exporter: " + string.Join(",", u));
            }
            return $"matched={Matched} exported={Exported} skipped={Skipped} failed={Failed} [{string.Join(" ", parts)}]";
        }
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

            // Auto takes everything and lets Plan() decide the format per asset, so a bundle only
            // exports as far as there is a real exporter for its types. The explicit kinds stay
            // available for exporting one category at a time.
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

            // Per-type tally up front, so the log says what was found before anything is written.
            foreach (var g in targets.GroupBy(o => o.type.ToString()).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                report.ByType[g.Key] = g.Count();
            }
            LogInfo($"{targets.Count} asset(s): " +
                    string.Join(" ", targets.GroupBy(o => o.type.ToString())
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => $"{g.Key} x{g.Count()}")));
            Report(0, targets.Count);

            var done = 0;
            foreach (var obj in targets)
            {
                var plan = Plan(obj, options);
                if (plan == null)
                {
                    var typeName = obj.type.ToString();
                    report.Unexported[typeName] = Get(report.Unexported, typeName) + 1;
                }
                else
                {
                    var dest = Path.Combine(outputRoot, DisplayName(obj) + plan.Extension);
                    try
                    {
                        if (File.Exists(dest) && !options.Overwrite)
                        {
                            report.Skipped++;
                        }
                        else
                        {
                            plan.Write(obj, dest, options);
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
                }

                Report(++done, targets.Count);
            }

            if (report.Unexported.Count > 0)
            {
                var u = new List<string>();
                foreach (var kv in report.Unexported) u.Add($"{kv.Key}x{kv.Value}");
                LogWarn("no exporter for: " + string.Join(", ", u));
            }
            return report;
        }

        private static int Get(Dictionary<string, int> d, string k)
            => d.TryGetValue(k, out var v) ? v : 0;

        private static bool Matches(AssetStudio.Object o, ExportKind kind) => kind switch
        {
            ExportKind.Auto => HasExporter(o),
            ExportKind.Texture => o is Texture2D t && t.m_Width > 0 && t.m_Height > 0,
            ExportKind.Sprite => o is Sprite s && s.m_Rect.width > 0 && s.m_Rect.height > 0,
            ExportKind.Mesh => o is Mesh,
            ExportKind.TextAsset => o is TextAsset,
            ExportKind.JsonDump => true,
            ExportKind.RawData => true,
            _ => false,
        };

        private sealed class ExportPlan
        {
            public string Extension;
            public Action<AssetStudio.Object, string, ExportOptions> Write;
        }

        /// <summary>
        /// Chooses the output format for one asset from its actual runtime type. This is the whole
        /// point of ExportKind.Auto: nothing is dropped just because no checkbox covered it, every
        /// type either has a real exporter here or falls back to a JSON dump.
        /// </summary>
        private static ExportPlan Plan(AssetStudio.Object o, ExportOptions options)
        {
            switch (o)
            {
                case Texture2D tex when tex.m_Width > 0 && tex.m_Height > 0:
                    return new ExportPlan
                    {
                        Extension = ".png",
                        Write = (obj, dest, opt) => WriteImage(((Texture2D)obj).ConvertToImage(opt.FlipTextures), dest, opt),
                    };

                case Sprite sprite when sprite.m_Rect.width > 0 && sprite.m_Rect.height > 0:
                    return new ExportPlan
                    {
                        Extension = ".png",
                        Write = (obj, dest, opt) => WriteImage(((Sprite)obj).GetImage(opt.SpriteMask), dest, opt),
                    };

                case Mesh mesh:
                    return new ExportPlan
                    {
                        Extension = ".obj",
                        Write = (obj, dest, opt) =>
                        {
                            if (!mesh.ExportObj(dest))
                                throw new InvalidOperationException("mesh has no usable geometry");
                        },
                    };

                case TextAsset ta:
                    return new ExportPlan
                    {
                        Extension = ".txt",
                        Write = (obj, dest, opt) => File.WriteAllBytes(dest, ((TextAsset)obj).m_Script),
                    };

                case Font font when font.m_FontData != null && font.m_FontData.Length > 0:
                    // Unity stores real TTFs/OTFs inline; sniff the magic like the CLI does.
                    return new ExportPlan
                    {
                        Extension = IsOpenType(font.m_FontData) ? ".otf" : ".ttf",
                        Write = (obj, dest, opt) => File.WriteAllBytes(dest, font.m_FontData),
                    };

                case VideoClip clip when clip.m_ExternalResources.m_Size > 0 && clip.m_VideoData != null:
                    return new ExportPlan
                    {
                        Extension = SafeExtension(Path.GetExtension(clip.m_OriginalPath), ".mp4"),
                        Write = (obj, dest, opt) => ((VideoClip)obj).m_VideoData.WriteData(dest),
                    };

                default:
                    if (options.Kind == ExportKind.RawData)
                    {
                        return new ExportPlan
                        {
                            Extension = ".rawdata",
                            Write = (obj, dest, opt) => File.WriteAllBytes(dest, obj.GetRawData()),
                        };
                    }
                    return new ExportPlan
                    {
                        Extension = ".json",
                        Write = (obj, dest, opt) => WriteJson(obj, dest),
                    };
            }
        }

        private static bool IsOpenType(byte[] d)
            => d.Length >= 4 && d[0] == 0x4F && d[1] == 0x54 && d[2] == 0x54 && d[3] == 0x4F; // "OTTO"

        private static string SafeExtension(string ext, string fallback)
        {
            if (string.IsNullOrEmpty(ext)) return fallback;
            // Only keep it if it looks like a plain extension; asset data can contain anything.
            if (ext.Length is < 2 or > 6) return fallback;
            foreach (var c in ext)
            {
                if (!char.IsLetterOrDigit(c)) return fallback;
            }
            return ext.ToLowerInvariant();
        }

        private static bool HasExporter(AssetStudio.Object o) => Plan(o, new ExportOptions()) != null;

        private static void WriteJson(AssetStudio.Object obj, string dest)
        {
            // Dump() needs an embedded type tree; DumpObject() reflects over parsed fields.
            var json = obj.Dump() ?? obj.DumpObject();
            if (string.IsNullOrEmpty(json))
                throw new InvalidOperationException("no typetree and no dumpable fields");
            File.WriteAllText(dest, json, new UTF8Encoding(false));
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
