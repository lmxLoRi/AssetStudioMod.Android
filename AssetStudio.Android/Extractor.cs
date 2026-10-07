using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using Object = AssetStudio.Object;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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

        /// <summary>AudioClips, written as .wav or .ogg depending on what they hold.</summary>
        Audio,

        /// <summary>MonoBehaviours as JSON, which is what their fields only exist as.</summary>
        MonoBehaviour,

        /// <summary>Fonts, as the .ttf/.otf the file holds.</summary>
        Font,

        /// <summary>VideoClips, as whatever container the file holds.</summary>
        Video,

        /// <summary>
        /// Everything the categories above do not cover: GameObjects, Transforms, CanvasRenderers,
        /// MonoScripts, Materials and the rest of the scene-side objects.
        ///
        /// Without it, selecting every category still showed 4211 of the 10706 objects in a test
        /// game and there was no way to see or explain the other 6495.
        /// </summary>
        Other,

        JsonDump,
        RawData,

        /// <summary>
        /// Texture2D only, written as the bytes the file holds -- no decode to RGBA, no encode to
        /// PNG. This is what a DDS/KTX/TGA writer needs, and it is here first as a measurement of
        /// what the export costs once the codecs are out of the way.
        /// </summary>
        TextureRaw,
    }

    public sealed class ExportOptions
    {
        /// <summary>How to write each object: Auto, JsonDump, RawData or TextureRaw.</summary>
        public ExportKind Mode = ExportKind.Auto;

        /// <summary>
        /// Which categories to write. Empty means everything. The same selection the browser
        /// filters with, so "what I filtered" and "what I exported" are the same set by
        /// construction rather than by two lists agreeing.
        /// </summary>
        public IReadOnlyCollection<ExportKind> Categories;
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

        /// <summary>Objects Auto could not describe, written as the bytes the file holds instead.</summary>
        public int RawFallback;

        /// <summary>Skips by type, so "3370 skipped" says which assets it was talking about.</summary>
        public readonly Dictionary<string, int> SkippedByType = new Dictionary<string, int>();
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
                parts.Add("没有导出器：" + string.Join(",", u));
            }
            return $"匹配={Matched} 导出={Exported} 跳过={Skipped} 失败={Failed} [{string.Join(" ", parts)}]";
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
        /// <summary>
        /// How much one AssetsManager.LoadFilesAndFolders call may bring in.
        ///
        /// The loader reads every object of every file it is handed and keeps all of them alive
        /// until Clear(), so the size of that one call is the app's peak memory. Handing it a whole
        /// game directory is what killed the process on the 4.1 GB / 4901-bundle Addressables cache
        /// at /sdcard/Android/data/com.pinkcore.starlusts: it read ~4900 bundles into memory in one
        /// go and was gone before the load finished. Whichever budget is reached first closes a
        /// batch, so a few huge bundles are bounded as well as many small ones.
        ///
        /// These are input bytes, not the memory the objects take, which is the decompressed size
        /// and can be several times larger. Both are deliberately well under what the device has
        /// (the 4.1 GB cache becomes ~64 batches and ~64 MB in flight at a time).
        /// </summary>
        private const int BatchFiles = 64;

        /// <summary>
        /// Input bytes one worker may hold at a time.
        ///
        /// This is a *per worker* budget, so it has to come down as workers go up: ReadAssets turns a
        /// batch into roughly five times its size in live objects, and with four workers the old
        /// 64 MB each put 2.0 GB of private memory in the process (measured), which is both a
        /// danger on a smaller device and the likely reason four workers only bought 1.4x. 32 MB
        /// each keeps the total in flight near what a single worker used to hold.
        /// </summary>
        private const long BatchBytes = 32L * 1024 * 1024;

        /// <summary>How many failure messages an export keeps, so one bad batch cannot fill memory.</summary>
        private const int MaxReportedErrors = 100;

        /// <summary>
        /// How many batches are loaded, encoded and written at once.
        ///
        /// One worker per batch: a batch is loaded once and then written, so the load of one
        /// overlaps the encode of another. This is a memory budget as much as a CPU one -- every
        /// worker holds its own batch of loaded objects (see BatchBytes) plus a decoded image, 4 MB
        /// for 1024x1024 Bgra32 and 16 MB for 2048x2048, per asset it is writing.
        /// </summary>
        private static readonly int ExportThreads = Math.Max(2, Environment.ProcessorCount / 2);

        /// <summary>Every file the scan decided was Unity data, in path order.</summary>
        private List<Candidate> _candidates;

        /// <summary>
        /// True when the load was batched and the objects were released again, so Export has to
        /// read the tree a second time. False means it all fit one batch and is still in
        /// <see cref="_assetsManager"/>.
        /// </summary>
        private bool _released;

        private readonly AssetsManager _assetsManager = new AssetsManager();

        public Action<string> Info;
        public Action<string> Warn;
        public Action<int, int> Progress;

        private void LogInfo(string m) => Info?.Invoke(m);
        private void LogWarn(string m) => Warn?.Invoke(m);
        private void Report(int cur, int total) => Progress?.Invoke(cur, total);

        /// <summary>What the scan found. Plain counters, because a filtered or batched load keeps no objects.</summary>
        public int LoadedFiles => _loadedFiles;
        public int LoadedObjects => _loadedObjects;

        // Backing fields, not auto-properties: the count pass adds to these from several workers.
        private int _loadedFiles;
        private int _loadedObjects;

        /// <summary>The kind this instance was loaded for; see <see cref="FilterFor"/>.</summary>
        /// <summary>Which categories the last load was narrowed to. Empty means everything.</summary>
        public IReadOnlyCollection<ExportKind> LoadedCategories { get; private set; } = Array.Empty<ExportKind>();

        /// <summary>Where to write the phase report, so a measurement survives the run.</summary>
        public string ReportPath;

        /// <summary>
        /// Optional user script that rewrites a file's bytes before the loader reads them, for
        /// bundles a game has encrypted. Null means the files are read as they are.
        /// </summary>
        internal Scripting.LuaDecryptor Decryptor;

        /// <summary>Where rewritten files are written. Required when <see cref="Decryptor"/> is set.</summary>
        public string DecryptStagingRoot;

        /// <summary>How many files the scan decided were Unity data.</summary>
        public int CandidateCount => _candidates?.Count ?? 0;

        /// <summary>
        /// AssetsManager.LoadViaTypeTree, which the GUI and CLI both expose.
        ///
        /// With it on, every Texture2D, Material, AnimationClip and Texture2DArray is read by
        /// walking the file's type tree into an OrderedDictionary, serialising that to JSON and
        /// deserialising it into the typed object (TypeTreeHelper.ReadTypeByteArray). Off, the same
        /// objects are read straight from the stream by their field layout. Off is what non-type-tree
        /// bundles always use, so it is a maintained path, but a type tree is what makes a mismatched
        /// or newer class layout survive, which is why upstream defaults it on.
        /// </summary>
        public static bool UseTypeTree = true;

        /// <summary>
        /// Which object types to materialise for an export kind.
        ///
        /// ReadAssets builds a C# object for every entry in every file's object table and holds it
        /// until Clear(). On the 4.1 GB cache that is 1.66M objects; a Texture export reads 17766
        /// of them, so 99% was deserialised, allocated and kept for nothing. The loader's filter
        /// drops the rest before they are built -- ObjectReader does no IO, it only copies the
        /// entry's metadata, so a filtered-out object costs nothing at all.
        ///
        /// Null means no filter. Auto, JsonDump and RawData can reach any type, and Auto's JSON
        /// fallback matches everything, so there is nothing to filter by.
        /// </summary>
        private static ClassIDType[] FilterFor(ExportKind kind) => kind switch
        {
            ExportKind.Texture => new[] { ClassIDType.Texture2D },
            ExportKind.Sprite => new[] { ClassIDType.Sprite },   // also pulls Texture2D + SpriteAtlas
            ExportKind.Mesh => new[] { ClassIDType.Mesh },
            ExportKind.TextAsset => new[] { ClassIDType.TextAsset },
            ExportKind.Audio => new[] { ClassIDType.AudioClip },
            ExportKind.MonoBehaviour => new[] { ClassIDType.MonoBehaviour },
            ExportKind.Font => new[] { ClassIDType.Font },
            ExportKind.Video => new[] { ClassIDType.VideoClip },
            ExportKind.TextureRaw => new[] { ClassIDType.Texture2D },
            _ => null,
        };

        /// <summary>
        /// The asset categories, which both the export and the browser select from.
        ///
        /// Not the whole enum: Auto, JsonDump, RawData and TextureRaw are output formats rather than
        /// categories, and one list serves both screens so a category cannot exist in one and not
        /// the other -- which is exactly what had happened with audio.
        /// </summary>
        public static readonly ExportKind[] Categories =
        {
            ExportKind.Texture,
            ExportKind.Sprite,
            ExportKind.Mesh,
            ExportKind.TextAsset,
            ExportKind.Audio,
            ExportKind.MonoBehaviour,
            ExportKind.Font,
            ExportKind.Video,
            ExportKind.Other,
        };

        /// <summary>Human labels, in the same order as <see cref="Categories"/>.</summary>
        public static readonly string[] CategoryLabels =
            { "贴图", "Sprite", "网格", "文本", "音频", "脚本", "字体", "视频", "其它" };

        /// <summary>True when an object is in any of the selected categories.</summary>
        internal static bool MatchesAny(AssetStudio.Object o, IReadOnlyCollection<ExportKind> kinds)
            => CategoryOf(o, kinds) != ExportKind.Auto || kinds == null || kinds.Count == 0;

        /// <summary>
        /// The first selected category an object belongs to.
        ///
        /// The index records this rather than just the object's type: FilterFor(Sprite) also pulls
        /// Texture2D in, because a Sprite needs its texture loaded, and matching on type alone would
        /// then show every texture under a Sprite filter.
        /// </summary>
        internal static ExportKind CategoryOf(AssetStudio.Object o, IReadOnlyCollection<ExportKind> kinds)
        {
            if (kinds == null || kinds.Count == 0) return ExportKind.Auto;
            foreach (var kind in kinds)
            {
                if (Matches(o, kind)) return kind;
            }
            return ExportKind.Auto;
        }

        /// <summary>True when a category other than Other claims this object.</summary>
        private static bool IsCategorised(AssetStudio.Object o)
        {
            foreach (var kind in Categories)
            {
                if (kind == ExportKind.Other) continue;
                if (Matches(o, kind)) return true;
            }
            return false;
        }

        /// <summary>The loader filter for a set of categories, or null when there is nothing to narrow to.</summary>
        private static ClassIDType[] FiltersFor(IEnumerable<ExportKind> kinds)
        {
            // Other cannot be expressed as a set of type ids -- it is everything except the others --
            // so asking for it means reading every type.
            foreach (var kind in kinds)
            {
                if (kind == ExportKind.Other) return null;
            }

            var set = new HashSet<ClassIDType>();
            foreach (var kind in kinds)
            {
                var filter = FilterFor(kind);
                if (filter != null) set.UnionWith(filter);
            }
            return set.Count == 0 ? null : set.ToArray();
        }

        /// <summary>One scanned file, with the size the sniff already read.</summary>
        private readonly struct Candidate
        {
            public readonly string Path;
            public readonly long Length;

            public Candidate(string path, long length)
            {
                Path = path;
                Length = length;
            }
        }

        /// <summary>
        /// Recursively scans <paramref name="root"/> and loads every Unity file found, keeping only
        /// the object types <paramref name="kind"/> can export.
        /// </summary>
        public void Load(string root, IReadOnlyCollection<ExportKind> categories)
        {
            // A single file is a valid input, not only a folder. Without this the caller had to
            // copy a picked or typed file into a directory of its own first, which is a copy of the
            // whole thing for no reason when it can simply be opened where it lies.
            if (!File.Exists(root) && !Directory.Exists(root))
                throw new FileNotFoundException(root);

            LoadedCategories = categories ?? Array.Empty<ExportKind>();
            _indexedCandidate = -1;
            _candidateByPath = null;
            _assetsManager.LoadViaTypeTree = UseTypeTree;

            // Cleared first: SetAssetFilter only unions into its set, so setting a second kind
            // without clearing would load the union of both.
            _assetsManager.ClearAssetFilter();
            var filter = FiltersFor(LoadedCategories);
            if (filter != null)
            {
                _assetsManager.SetAssetFilter(filter);
                LogInfo($"只加载这些类型：{string.Join(", ", filter)}");
            }

            var full = Path.GetFullPath(root);
            LogInfo($"扫描 {full}");

            var clock = Stopwatch.StartNew();

            // 1. Walk. One pass of readdir plus a stat per entry: linear, and parallelising it
            //    only adds contention on the same directory inode.
            var everything = File.Exists(full)
                ? new[] { full }
                : Directory.GetFiles(full, "*.*", SearchOption.AllDirectories);
            var walkMs = clock.ElapsedMilliseconds;
            clock.Restart();

            // 2. Decide by header, not by name. The loader already identifies files by content in
            //    FileReader.CheckFileType, so the pre-filter has to agree with it, and an extension
            //    list cannot. A game directory is mostly extensionless files: an Addressables
            //    cache, for one, is ~4900 __data bundles interleaved with ~4900 __info JSON
            //    manifests. Those are identical extensionless shapes, so a name-based filter lets
            //    every manifest through to be opened, sniffed and thrown away again.
            // Decryption has to happen before the sniff, not before the load: an encrypted bundle
            // does not look like a bundle, so a file the script can fix would otherwise be dropped
            // as "not Unity data" and never reach a loader.
            if (Decryptor != null && Decryptor.Active)
            {
                var decryptMark = Stopwatch.GetTimestamp();
                everything = DecryptAll(everything);
                LogInfo($"lua：{Decryptor.Name} 跑过 {everything.Length} 个文件 " +
                        $"({(Stopwatch.GetTimestamp() - decryptMark) * 1000 / Stopwatch.Frequency} ms)");
            }

            var candidates = SniffCandidates(everything);
            var sniffMs = clock.ElapsedMilliseconds;
            clock.Restart();

            LogInfo($"{everything.Length} 个文件中有 {candidates.Count} 个候选 " +
                    $"（遍历 {walkMs} ms，嗅探 {sniffMs} ms，排除 {everything.Length - candidates.Count}）");
            Report(0, candidates.Count);

            _candidates = candidates;
            _released = false;
            _loadedFiles = 0;
            _loadedObjects = 0;

            var batches = new List<(int Start, int Count)>(Batches(_candidates));
            _batches = batches;
            BatchCount = batches.Count;
            if (batches.Count <= 1)
            {
                // Small enough to hold, so hold it: Export then works straight out of memory
                // instead of reading the tree a second time.
                //
                // The loader reports per-file progress through this hook and nothing else reports
                // during a load, so without it a load just looks frozen. Only wired here: the hook
                // reports a percentage of one call, which would restart from 0 for every batch.
                // Fully qualified: this class has its own Progress field, which would otherwise win.
                var previous = AssetStudio.Progress.Default;
                AssetStudio.Progress.Default = new InlineProgress<int>(pct => Report(pct, 100));
                try
                {
                    LoadBatch(0, _candidates.Count);
                }
                finally
                {
                    AssetStudio.Progress.Default = previous;
                }
                LogInfo($"已加载 {LoadedFiles} 个序列化文件、{LoadedObjects} 个对象，来自 {_lastParent}");
            }
            else
            {
                // Deliberately not read here. Export has to read every batch anyway, and reading it
                // twice was the single most expensive thing the app did: the extra pass measured
                // ~97s of a 492s run on the 4.1 GB cache, purely to put two numbers on the status
                // line before the real work had started. Export accumulates and reports them
                // instead, so they still appear -- just when they are actually known.
                _released = true;
                LogInfo($"{_candidates.Count} 个候选文件分 {batches.Count} 批，" +
                        $"导出时加载（每批最多 {BatchFiles} 个文件或 {BatchBytes / (1024 * 1024)} MB）");
            }

            LogInfo($"加载耗时 {clock.ElapsedMilliseconds} ms");
            Report(1, 1);
        }

        private string _lastParent;

        /// <summary>Which candidate is currently materialised for the index, or -1.</summary>
        private int _indexedCandidate = -1;

        /// <summary>The batch split the scan produced, kept so browsing can walk it.</summary>
        private List<(int Start, int Count)> _batches;

        /// <summary>How many batches the last scan split the candidate files into.</summary>
        public int BatchCount { get; private set; }

        /// <summary>
        /// One object found by the index pass: enough to list it now and to find it again later.
        ///
        /// It deliberately holds no reference to the object. The whole point of the index is that
        /// the objects are not kept -- a phone cannot hold the tree -- so what is kept is the file
        /// it came from and how to find it inside that file.
        /// </summary>
        public sealed class IndexEntry
        {
            public int Candidate;
            public long PathID;
            public ClassIDType Type;
            public string Name;
            public string Source;

            /// <summary>Which selected category it matched, so the filter can be applied exactly.</summary>
            public ExportKind Category;
        }

        /// <summary>
        /// Walks every candidate once and records what matches, without keeping any of it.
        ///
        /// This is what makes filtering mean the whole tree instead of the batch in front of you.
        /// It is a full read of everything, so it is an explicit action with progress rather than
        /// something that happens when the browser opens, and it can be stopped part way.
        /// </summary>
        public List<IndexEntry> BuildIndex(IReadOnlyCollection<ExportKind> kinds,
                                           Action<int, int> progress, Func<bool> cancelled)
        {
            if (_candidates == null) throw new InvalidOperationException("请先扫描一个文件夹。");
            if (_batches == null) _batches = new List<(int Start, int Count)>(Batches(_candidates));

            // The manager was loaded with a filter that cannot be widened, so it is replaced rather
            // than added to -- otherwise an index for a second category would still only see the
            // first.
            _assetsManager.Clear();
            _assetsManager.ClearAssetFilter();
            var narrowed = FiltersFor(kinds);
            if (narrowed != null) _assetsManager.SetAssetFilter(narrowed);

            var entries = new List<IndexEntry>();
            for (var b = 0; b < _batches.Count; b++)
            {
                if (cancelled != null && cancelled()) break;

                _assetsManager.Clear();
                var (start, count) = _batches[b];
                LoadBatch(start, count);

                // One pass over the files this batch produced. The first version of this nested the
                // file loop inside a loop over the batch's candidates, so with 64 files in a batch
                // every object was counted 64 times -- an index of the test cache reported 727,728
                // textures where the export matches 17,766.
                foreach (var file in _assetsManager.AssetsFileList)
                {
                    // A SerializedFile records the reader it came from, so the candidate is the one
                    // whose path is a prefix of it. A zip's entries carry the archive path plus the
                    // entry name, which is why this compares prefixes rather than equality.
                    var fullPath = SourcePath(file);
                    var candidate = CandidateFor(fullPath);
                    if (candidate < 0) candidate = start;   // nothing matched; still has to point somewhere

                    foreach (var o in file.Objects)
                    {
                        var category = CategoryOf(o, kinds);
                        if (category == ExportKind.Auto) continue;

                        entries.Add(new IndexEntry
                        {
                            Candidate = candidate,
                            PathID = o.m_PathID,
                            Type = o.type,
                            Category = category,
                            Name = SafeName((o as NamedObject)?.m_Name),
                            Source = fullPath,
                        });
                    }
                }

                progress?.Invoke(b + 1, _batches.Count);
            }

            return entries;
        }

        /// <summary>
        /// Loads the one file an index entry came from and hands back that object.
        ///
        /// The caller keeps it only as long as it is on screen; the next call replaces it.
        /// </summary>
        public AssetStudio.Object LoadIndexed(IndexEntry entry)
        {
            if (entry == null || _candidates == null) return null;

            // The index holds no objects, so opening one means loading its file. Re-loading it for
            // the next entry from the same file would make browsing by index feel slow for no
            // reason: a batch is sixty-odd bundles and several entries usually share one.
            if (_indexedCandidate != entry.Candidate)
            {
                _assetsManager.Clear();
                LoadBatch(entry.Candidate, 1);
                _indexedCandidate = entry.Candidate;
            }

            foreach (var file in _assetsManager.AssetsFileList)
            {
                // The source is checked as well: a pathID is only unique inside one SerializedFile,
                // and a candidate can be an archive holding dozens of them.
                if (!string.IsNullOrEmpty(entry.Source)
                    && !string.Equals(SourcePath(file), entry.Source, StringComparison.Ordinal)) continue;

                foreach (var o in file.Objects)
                {
                    if (o.m_PathID == entry.PathID && o.type == entry.Type) return o;
                }
            }

            return null;
        }

        /// <summary>
        /// Loads one batch and keeps its objects, for browsing.
        ///
        /// This deliberately holds them, which the export path does not: a phone cannot hold the
        /// whole tree (that is why the export batches at all), so browsing is one batch at a time
        /// and the caller walks them. Each call replaces the previous batch.
        /// </summary>
        public IReadOnlyList<Object> BrowseBatch(int index)
        {
            if (_candidates == null) throw new InvalidOperationException("请先扫描一个文件夹。");
            if (_batches == null) _batches = new List<(int Start, int Count)>(Batches(_candidates));
            if (index < 0 || index >= _batches.Count) return Array.Empty<Object>();

            _assetsManager.Clear();
            _indexedCandidate = -1;

            // Browsing wants every type it can show, not whatever the last export narrowed the load
            // to, and the manager's filter cannot be widened once set.
            _assetsManager.ClearAssetFilter();

            // _released is deliberately left alone. It is what tells Export whether the tree fit in
            // one batch and is therefore still in _assetsManager, and browsing replaces that with a
            // single batch. Clearing it here would make Export take the single-batch path and
            // silently write only the batch that was last looked at.
            var (start, count) = _batches[index];
            LoadBatch(start, count);

            var objects = new List<Object>();
            foreach (var file in _assetsManager.AssetsFileList) objects.AddRange(file.Objects);
            return objects;
        }

        /// <summary>
        /// Loads one range of the scan. This is the only place the loader is called, so it is the
        /// only place that decides how much is in memory at once.
        /// </summary>
        /// <summary>
        /// Loads a set of bundles in one call.
        ///
        /// One call, not one per bundle. The survey loads a batch at a time and reads these files
        /// perfectly well; the second pass used to hand them over one at a time, and a single-file
        /// load of this game's cache -- where a bundle is a directory holding "__data" and "__info" --
        /// comes back with nothing at all. Its models then assembled from an empty manager, which is
        /// where "这批里缺 骨架" came from.
        /// </summary>
        private void LoadTogether(List<Candidate> files)
        {
            if (files.Count == 0) return;

            LoadBatch(_assetsManager, files, 0, files.Count, out var loaded, out var objects, out var parent);
            _lastParent = parent;
            _loadedFiles += loaded;
            _loadedObjects += objects;

            if (loaded > 0) return;

            // Nothing came of handing over the files themselves. This game's cache stores a bundle as
            // a directory holding "__data" and "__info", and that one file loads as an empty manager
            // while the directory it sits in loads; the survey never noticed because it reads whole
            // batches. Nothing is lost by trying, since a load that read no file has nothing to clear.
            var dirs = new List<string>();
            foreach (var file in files)
            {
                var dir = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir)) dirs.Add(dir);
            }

            if (dirs.Count == 0 || dirs.Count > 64) return;

            _assetsManager.Clear();

            var asFolders = new List<Candidate>(dirs.Count);
            foreach (var dir in dirs) asFolders.Add(new Candidate(dir, 0));

            LoadBatch(_assetsManager, asFolders, 0, asFolders.Count, out loaded, out objects, out parent);
            _lastParent = parent;
            _loadedFiles += loaded;
            _loadedObjects += objects;
        }

        private void LoadBatch(int start, int count)
        {
            LoadBatch(_assetsManager, _candidates, start, count, out var files, out var objects, out var parent);

            // Accumulated here, not discarded. Since LoadBatch grew a static overload for the
            // parallel workers this one quietly dropped its out parameters, so every single-batch
            // load reported "已加载 0 个序列化文件" no matter how much it had actually read.
            _lastParent = parent;
            _loadedFiles += files;
            _loadedObjects += objects;
        }

        /// <summary>
        /// Loads one range of the scan into <paramref name="manager"/>.
        ///
        /// AssetsManager keeps everything it knows in the instance -- the file list, the seen-file
        /// hashes, the PPtr index cache -- so the only way to have two batches in flight is to have
        /// two of them. FileReader used to share one static header buffer, which made that a data
        /// race; it does not any more.
        /// </summary>
        private static void LoadBatch(AssetsManager manager, List<Candidate> candidates, int start, int count,
                                      out int files, out int objects, out string parent)
        {
            // AssetsManager.LoadFilesAndFolders runs every entry through Path.GetFullPath, so it
            // needs absolute paths. Relative names would silently resolve against the process
            // working directory, which on Android is "/" and matches nothing.
            // (It also clears the list it is given, hence the local.)
            var paths = new List<string>(count);
            for (var i = 0; i < count; i++) paths.Add(candidates[start + i].Path);

            // Dependencies are not a problem for batching: LoadAssetsFile queues the externals it
            // finds next to the file on disk into importFiles, and Load() drains that queue while
            // it grows, so a batch pulls in what it needs from the tree by name. A dependency
            // shared by many batches is simply loaded more than once.
            manager.LoadFilesAndFolders(out parent, paths);
            files = manager.AssetsFileList.Count;
            objects = manager.AssetsFileList.Sum(f => f.Objects.Count);
        }

        /// <summary>A loader of its own, so several batches can be in flight at once.</summary>
        private Worker NewWorker()
        {
            var worker = new Worker();
            worker.Manager.LoadViaTypeTree = UseTypeTree;

            var filter = FiltersFor(LoadedCategories);
            if (filter != null) worker.Manager.SetAssetFilter(filter);
            return worker;
        }

        private sealed class Worker : IDisposable
        {
            public readonly AssetsManager Manager = new AssetsManager();
            public void Dispose() => Manager.Clear();
        }

        /// <summary>
        /// Splits the scan into load batches, cut by whichever budget comes first.
        ///
        /// Consecutive files only, in path order: the scan is sorted, so a bundle's parts and its
        /// siblings stay next to each other, and the loader's dependency lookup is by name rather
        /// than by position, so nothing depends on a batch boundary falling anywhere in particular.
        /// </summary>
        /// <summary>
        /// The batches the model passes (Live2D, skeletons) walk.
        ///
        /// Smaller than an export's, and with a collection after each, because those passes hold a
        /// batch's decompressed objects while they look for models -- and 64 bundles of 32 MB can
        /// decompress to far more than that, with the pools and the collector holding on to it. The
        /// measured peak on a 3.9 GB cache was 1.3 GB and climbing before this; an export survives
        /// that because it writes and drops one batch at a time, but this did not.
        /// </summary>
        private static IEnumerable<(int Start, int Count)> ModelBatches(List<Candidate> files)
            => Batches(files, ModelBatchFiles, ModelBatchBytes);

        private const int ModelBatchFiles = 16;
        private const long ModelBatchBytes = 8L * 1024 * 1024;

        /// <summary>Releases a batch's memory before the next one is read.</summary>
        private static void ReleaseBatch()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static IEnumerable<(int Start, int Count)> Batches(List<Candidate> files)
            => Batches(files, BatchFiles, BatchBytes);

        private static IEnumerable<(int Start, int Count)> Batches(List<Candidate> files, int maxFiles, long maxBytes)
        {
            var i = 0;
            while (i < files.Count)
            {
                var end = i;
                long bytes = 0;
                while (end < files.Count && end - i < maxFiles)
                {
                    // Always take at least one file, even one bigger than the whole budget.
                    if (end > i && bytes + files[end].Length > maxBytes) break;
                    bytes += files[end].Length;
                    end++;
                }

                yield return (i, end - i);
                i = end;
            }
        }

        private const int HeaderLen = 1152; // the window FileReader reads for its own check

        // Signatures copied from FileReader so the two agree on what a Unity file is. Duplicated
        // rather than called because that type shares one static header buffer across instances
        // (FileReader.headerBuff), so it cannot be used from several threads at once.
        private static readonly byte[] GZipMagic = { 0x1f, 0x8b };
        private static readonly byte[] BrotliMagic = { 0x62, 0x72, 0x6F, 0x74, 0x6C, 0x69 };
        private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };
        private static readonly byte[] ZipSpannedMagic = { 0x50, 0x4B, 0x07, 0x08 };
        private static readonly byte[] UnityFsMagic = { 0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00 };

        /// <summary>
        /// Names that cannot be Unity data, so they are not even worth opening. A shortcut, not the
        /// decision: everything it lets through is still judged by its header.
        /// </summary>
        private static readonly HashSet<string> HopelessNames = new(StringComparer.Ordinal)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tga",
            ".mp3", ".ogg", ".wav", ".ttf", ".otf",
            ".dll", ".so", ".dat", ".db", ".sqlite", ".lck",
            ".json", ".txt", ".xml", ".plist", ".yml", ".yaml", ".log", ".ver",
        };

        /// <summary>
        /// Keeps the files the loader would actually do something with. A header the signatures do
        /// not match becomes FileType.ResourceFile, which LoadFile disposes and moves straight past,
        /// so dropping those here does not change what gets loaded -- only the time spent working
        /// out that they never were going to load.
        ///
        /// Returns the size alongside the path because the read already had it (fs.Length): batching
        /// needs to know how many bytes it is taking on, and this way that costs no second stat.
        /// </summary>
        /// <summary>
        /// Hands every file to the script and returns the paths to load: the rewritten copy for the
        /// ones it changed, the original for the ones it left alone.
        ///
        /// Whole files, because the host cannot know what part of the file a scheme touches. That is
        /// also why a per-byte Lua loop over a multi-gigabyte tree is impractical -- MoonSharp
        /// interprets, so the cost is the script's, and a script for a scheme like that wants to be
        /// written around string.find/string.sub rather than per character.
        /// </summary>
        private string[] DecryptAll(string[] files)
        {
            var root = string.IsNullOrEmpty(DecryptStagingRoot)
                ? Path.Combine(Path.GetTempPath(), "assetstudio-decrypted")
                : DecryptStagingRoot;
            Directory.CreateDirectory(root);

            var result = new string[files.Length];
            var rewritten = 0;
            var failed = 0;

            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                result[i] = file;

                byte[] data;
                try
                {
                    data = File.ReadAllBytes(file);
                }
                catch (Exception ex)
                {
                    LogWarn($"lua：读不了 {Path.GetFileName(file)}：{ex.Message}");
                    continue;
                }

                if (!Decryptor.TryTransform(data, data.Length, Path.GetFileName(file), out var output, out var error))
                {
                    if (error != null)
                    {
                        failed++;

                        // One broken file is usually every file; a bounded number of lines says so
                        // without burying the log.
                        if (failed <= 5) LogWarn($"lua：{Path.GetFileName(file)}：{error}");
                    }
                    continue;
                }

                try
                {
                    var target = Path.Combine(root, $"{i:D6}_{Path.GetFileName(file)}");
                    File.WriteAllBytes(target, output);
                    result[i] = target;
                    rewritten++;
                }
                catch (Exception ex)
                {
                    LogWarn($"lua：写不了解密结果 {Path.GetFileName(file)}：{ex.Message}");
                }
            }

            LogInfo($"lua：改写 {rewritten} 个文件，失败 {failed} 个");
            return result;
        }

        private List<Candidate> SniffCandidates(string[] files)
        {
            var kept = new List<Candidate>(files.Length);
            var gate = new object();
            var seen = 0;

            // The only part that fans out. Each file costs one short read, so this is dominated by
            // open/close latency rather than CPU, which is what threads are good for. The load
            // itself stays serial: AssetsManager is not thread safe.
            Parallel.ForEach(files,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                () => new byte[HeaderLen],
                (path, _, buffer) =>
                {
                    if (!HopelessNames.Contains(Path.GetExtension(path).ToLowerInvariant()) &&
                        HasUnityHeader(path, buffer, out var size))
                    {
                        var candidate = new Candidate(path, size);
                        lock (gate) kept.Add(candidate);
                    }

                    // Throttled: one UI post per file would cost more than the reads do.
                    if (Interlocked.Increment(ref seen) % 250 == 0) Report(seen, files.Length);
                    return buffer;
                },
                _ => { });

            kept.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path)); // Parallel.ForEach makes no order guarantee.
            return kept;
        }

        /// <summary>
        /// Mirrors AssetStudio.FileReader.CheckFileType. Every branch that returns something other
        /// than ResourceFile is reproduced, so nothing the loader would act on gets dropped.
        ///
        /// <paramref name="size"/> is the file length, handed back so the caller does not have to
        /// stat a file it has just opened.
        /// </summary>
        private static bool HasUnityHeader(string path, byte[] buffer, out long size)
        {
            int len;
            size = 0;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    4096, FileOptions.SequentialScan);
                size = fs.Length;
                if (size < 8) return false;

                len = (int)Math.Min(HeaderLen, size);
                var read = 0;
                while (read < len)
                {
                    var n = fs.Read(buffer, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                len = read;
            }
            catch
            {
                return false;
            }

            if (len < 8) return false;

            switch (Asciiz(buffer, len, 20))
            {
                case "UnityWeb":
                case "UnityRaw":
                case "UnityArchive":
                case "UnityFS":
                case "UnityWebData1.0":
                case "TuanjieWebData1.0":
                    return true;
            }

            if (Matches(buffer, len, 0, GZipMagic)) return true;
            if (Matches(buffer, len, 32, BrotliMagic)) return true;
            if (IsSerializedFile(buffer, len, size)) return true;
            if (Matches(buffer, len, 0, ZipMagic) || Matches(buffer, len, 0, ZipSpannedMagic)) return true;

            // A bundle sitting at a non-zero offset inside something else. The loader only uses
            // that offset to decide where to seek, so for filtering, the magic being there is the
            // whole answer.
            return IndexOf(buffer, len, UnityFsMagic, 1) > 0;
        }

        private static bool IsSerializedFile(byte[] b, int len, long size)
        {
            if (size < 20 || len < 20) return false;

            long fileSize = BeUInt32(b, 4);
            var version = BeUInt32(b, 8);
            long dataOffset = BeUInt32(b, 12);

            if (version >= 22)
            {
                if (size < 48 || len < 40) return false;
                fileSize = BeInt64(b, 24);
                dataOffset = BeInt64(b, 32);
            }

            // The header states its own total size. If that disagrees with the file on disk this is
            // not a serialized file that happens to be truncated, it is something else entirely.
            return fileSize == size && dataOffset <= size;
        }

        private static string Asciiz(byte[] b, int len, int max)
        {
            var n = 0;
            while (n < max && n < len && b[n] != 0) n++;
            return Encoding.ASCII.GetString(b, 0, n);
        }

        private static bool Matches(byte[] b, int len, int offset, byte[] magic)
        {
            if (offset < 0 || offset + magic.Length > len) return false;
            for (var i = 0; i < magic.Length; i++)
            {
                if (b[offset + i] != magic[i]) return false;
            }
            return true;
        }

        private static int IndexOf(byte[] b, int len, byte[] magic, int start)
        {
            for (var i = start; i + magic.Length <= len; i++)
            {
                if (Matches(b, len, i, magic)) return i;
            }
            return -1;
        }

        private static uint BeUInt32(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        private static long BeInt64(byte[] b, int o) =>
            (long)(((ulong)BeUInt32(b, o) << 32) | BeUInt32(b, o + 4));

        /// <summary>
        /// AssetStudio.Progress.Default hands out percentages through <see cref="IProgress{T}"/>.
        /// Progress&lt;T&gt; would bounce them off the thread pool, since the scan thread has no
        /// SynchronizationContext, and they would arrive out of order; this keeps them in order.
        /// </summary>
        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;

            public InlineProgress(Action<T> handler) => _handler = handler;

            public void Report(T value) => _handler(value);
        }


        /// <summary>
        /// Writes a single object -- the one the browser is showing.
        ///
        /// Export writes the whole tree, which is thousands of files when someone only wanted the
        /// one they had just found. This goes through the same plan and the same writers, so the
        /// file it produces is the same file the full export would have produced.
        /// </summary>
        public ExportReport ExportOne(AssetStudio.Object target, string outputRoot, ExportOptions options)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            Directory.CreateDirectory(outputRoot);

            var report = new ExportReport();
            var claimed = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
            Merge(report, WriteTargets(new List<AssetStudio.Object> { target }, outputRoot, options, false,
                                       claimed, _assetsManager));
            Finish(report);
            return report;
        }

        /// <summary>
        /// Writes the objects an index selected.
        ///
        /// The index holds no objects, so this loads the files they came from -- each one once, no
        /// matter how many of its objects were selected. Everything else is the same plan and the
        /// same writers the tree export uses, so the files are the ones that export would produce.
        /// </summary>
        public ExportReport ExportIndexed(IReadOnlyList<IndexEntry> entries, string outputRoot,
                                          ExportOptions options)
        {
            var report = new ExportReport();
            Directory.CreateDirectory(outputRoot);

            var claimed = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

            // Reported, because a filtered export used to give no sign of life at all: no progress,
            // no status, nothing until it finished, so it read as a dead button.
            var groups = entries.GroupBy(e => e.Candidate).ToList();
            var done = 0;

            foreach (var group in groups)
            {
                Report(++done, groups.Count);

                // Keyed by source file as well: one candidate can be a zip holding dozens of
                // serialized files, and a pathID is only unique inside one of them, so matching on
                // pathID and type alone pulled in namesakes from its siblings.
                var wanted = new HashSet<(string, long, ClassIDType)>(
                    group.Select(e => (e.Source, e.PathID, e.Type)));

                _assetsManager.Clear();
                _indexedCandidate = -1;
                LoadBatch(group.Key, 1);

                var targets = new List<AssetStudio.Object>();
                foreach (var file in _assetsManager.AssetsFileList)
                {
                    foreach (var o in file.Objects)
                    {
                        if (wanted.Contains((SourcePath(file), o.m_PathID, o.type))) targets.Add(o);
                    }
                }


                Merge(report, WriteTargets(targets, outputRoot, options, false, claimed, _assetsManager));
            }

            Finish(report);
            return report;
        }

        /// <summary>
        /// Writes every Live2D Cubism model the loaded tree holds, one folder each.
        ///
        /// Whole-tree by nature: a model's parts are spread across bundles, so this wants everything
        /// loaded at once rather than a batch at a time -- see MainActivity.ExportLive2D, which
        /// loads without a filter first.
        /// </summary>
        /// <summary>
        /// One model's worth of the tree: what it is, and which bundles its parts are in.
        /// </summary>
        private sealed class ModelGroup
        {
            public string Key;
            public readonly HashSet<string> Sources = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// The batches this model's parts were seen in.
            ///
            /// Kept because a recorded path is not always loadable on its own. This game's cache
            /// stores a bundle as a directory holding "__data" and "__info", and handing that one
            /// file to the loader returns nothing -- while the batch it was discovered in loads
            /// perfectly. Recording where a part was seen means the second pass can read it the same
            /// way the survey did.
            /// </summary>
            public readonly List<int> BatchCandidates = new List<int>();
        }

        /// <summary>
        /// First half of a two-phase model export: read the tree a batch at a time and keep only the
        /// fact that a model exists and where its parts live.
        ///
        /// Nothing is written and nothing is kept beyond a name and a handful of paths per model, so
        /// a 4 GB cache costs a few megabytes to survey. Merging by key is what makes a model whose
        /// parts are spread over several bundles one model rather than one piece per batch.
        /// </summary>
        private List<ModelGroup> DiscoverModels(
            Func<AssetsManager, IReadOnlyDictionary<AssetStudio.Object, string>,
                 IEnumerable<(string Key, List<string> Sources)>> discover,
            string what, Action<string> log)
        {
            var groups = new Dictionary<string, ModelGroup>(StringComparer.Ordinal);
            var batches = new List<(int Start, int Count)>(ModelBatches(_candidates));

            // The parts are found through container paths, not through references, so following a
            // bundle's externals would only pour the tree into memory.
            var previous = _assetsManager.LoadDependencies;
            _assetsManager.LoadDependencies = false;
            try
            {

            for (var b = 0; b < batches.Count; b++)
            {
                var (start, count) = batches[b];

                _assetsManager.Clear();
                _indexedCandidate = -1;
                LoadBatch(start, count);

                var containers = ContainersIn(_assetsManager);
                var emitted = 0;

                foreach (var (key, sources) in discover(_assetsManager, containers))
                {
                    if (!groups.TryGetValue(key, out var group))
                        groups[key] = group = new ModelGroup { Key = key };

                    foreach (var source in sources) group.Sources.Add(source);
                    emitted++;

                    if (group.BatchCandidates.Count < 64)
                    {
                        for (var k = 0; k < count; k++) group.BatchCandidates.Add(start + k);
                    }
                }

                _assetsManager.Clear();
                ReleaseBatch();
                Report(b + 1, batches.Count);
            }

            log($"{what}：扫完 {batches.Count} 批，找到 {groups.Count} 个模型");
            }
            finally
            {
                _assetsManager.LoadDependencies = previous;
            }

            return groups.Values.ToList();
        }

        /// <summary>
        /// Second half: load each model's own bundles and hand it to the ordinary export.
        ///
        /// One model at a time. The peak is that model rather than a batch, and every part is present
        /// however many bundles it was spread over -- which is the whole point of having surveyed
        /// first.
        /// </summary>
        private int ExportModels(List<ModelGroup> groups, string what,
                                 Func<AssetsManager, IReadOnlyDictionary<AssetStudio.Object, string>, int> write,
                                 Action<string> log)
        {
            var written = 0;
            var retried = 0;
            var recovered = 0;
            var reused = 0;

            // What the manager is currently holding. A model's parts can all be in one bundle, and on
            // this cache one bundle held several dozen models -- 120 MB of them -- so loading it once
            // per model read the same file thirty times. It also happens that the second read of that
            // file comes back empty, which is what left those models written with an atlas and no
            // skeleton. Keeping what is already loaded fixes both.
            var loaded = new HashSet<int>();

            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var indexes = new List<int>();

                foreach (var source in group.Sources)
                {
                    var index = CandidateFor(source);
                    if (index >= 0 && !indexes.Contains(index)) indexes.Add(index);
                }

                // Deliberately not the batch each part was seen in. Adding those made every group's
                // candidate set different, so the reuse check below never matched, so the same 120 MB
                // bundle was loaded once per model -- and the second load of it comes back empty, which
                // is what left 176 models written with an atlas and no skeleton. Loading by the parts
                // alone means a bundle shared by several models is read once and kept.

                if (indexes.Count == 0)
                {
                    log($"{what}：{group.Key} 的 bundle 已不在候选里，跳过");
                    continue;
                }

                log($"{what}：[{i + 1}/{groups.Count}] {group.Key}（{indexes.Count} 个 bundle）");

                var alreadyLoaded = indexes.All(loaded.Contains);

                if (alreadyLoaded)
                {
                    reused++;
                }
                else
                {
                    _assetsManager.Clear();
                    loaded.Clear();

                    // Same reason as the survey: this model's own bundles are all it needs.
                    var previousLoad = _assetsManager.LoadDependencies;
                    _assetsManager.LoadDependencies = false;
                    try
                    {
                        LoadTogether(indexes.Select(i => _candidates[i]).ToList());
                    }
                    finally
                    {
                        _assetsManager.LoadDependencies = previousLoad;
                    }

                    foreach (var index in indexes) loaded.Add(index);
                }

                if (_assetsManager.AssetsFileList.Count == 0)
                {
                    var retryPrevious = _assetsManager.LoadDependencies;
                    _assetsManager.LoadDependencies = false;
                    try
                    {
                        _assetsManager.Clear();
                        LoadTogether(indexes.Select(j => _candidates[j]).ToList());
                    }
                    finally
                    {
                        _assetsManager.LoadDependencies = retryPrevious;
                    }

                    if (_assetsManager.AssetsFileList.Count > 0) recovered++;
                }

                try
                {
                    written += write(_assetsManager, ContainersIn(_assetsManager));
                }
                catch (Exception ex)
                {
                    log($"{what}：{group.Key} 导出失败 {ex.GetType().Name}: {ex.Message}");
                }

                _assetsManager.Clear();
                ReleaseBatch();
            }

            log($"{what}：读空 {retried} 组，重试后恢复 {recovered} 组，复用已加载 {reused} 组");
            return written;
        }

        /// <summary>
        /// Writes every Live2D Cubism model in the tree, one folder each.
        ///
        /// Two passes, because the tree does not fit in memory and a model's parts do not all live in
        /// one bundle: the first reads it a batch at a time and notes where each model's parts are,
        /// the second loads one model's bundles at a time and rebuilds it. The containers are read
        /// again in the second pass, and they come out the same -- a container path belongs to the
        /// bundle that publishes the asset -- so the extractor sees exactly what it would have seen
        /// with the whole tree loaded.
        /// </summary>
        public int ExportLive2D(string outputRoot, Action<string> log)
        {
            if (_candidates == null) throw new InvalidOperationException("请先扫描一个文件夹。");

            Directory.CreateDirectory(outputRoot);
            log ??= _ => { };

            // Whatever filter the preview or the index left on the manager, it is not wanted here. The
            // survey reads the tree unfiltered; a filtered manager materialises fewer objects, so the
            // skeleton the survey found in a bundle need not be there on the export's second read.
            _assetsManager.ClearAssetFilter();

            var groups = DiscoverModels(Live2DExport.Discover, "Live2D", log);
            return ExportModels(groups, "Live2D",
                (manager, containers) => Live2DExport.Export(manager, containers, outputRoot, log), log);
        }

        /// <summary>Writes every Spine and DragonBones model in the tree, one folder each. See
        /// <see cref="ExportLive2D"/> for why this is two passes.</summary>
        public int ExportSkeletons(string outputRoot, ExportOptions options, Action<string> log)
        {
            if (_candidates == null) throw new InvalidOperationException("请先扫描一个文件夹。");

            Directory.CreateDirectory(outputRoot);
            log ??= _ => { };

            // Whatever filter the preview or the index left on the manager, it is not wanted here.
            _assetsManager.ClearAssetFilter();

            var models = new Dictionary<string, SkeletonExport.Plan>(StringComparer.Ordinal);
            var textures = new Dictionary<string, SkeletonExport.Ref>(StringComparer.Ordinal);
            var batches = new List<(int Start, int Count)>(ModelBatches(_candidates));

            // First pass: read the tree a batch at a time and record which asset is what. Parts are
            // gathered by the model's name, so a model whose skeleton and atlas sit in different
            // bundles still arrives as one model -- and nothing is written yet, which is the point:
            // what an asset is gets decided once, from its content, and handed to the writer as a
            // reference rather than left for a second, name-based guess to get wrong.
            var previous = _assetsManager.LoadDependencies;
            _assetsManager.LoadDependencies = false;
            try
            {
                for (var b = 0; b < batches.Count; b++)
                {
                    var (start, count) = batches[b];

                    _assetsManager.Clear();
                    _indexedCandidate = -1;
                    LoadBatch(start, count);

                    foreach (var part in SkeletonExport.Discover(_assetsManager, ContainersIn(_assetsManager)))
                    {
                        if (!models.TryGetValue(part.Key, out var model))
                        {
                            models[part.Key] = model = new SkeletonExport.Plan
                            {
                                Key = part.Key,
                                Base = part.Base,
                                DragonBones = part.DragonBones,
                            };
                        }

                        if (part.Skeleton.IsSet && !model.Skeleton.IsSet)
                        {
                            model.Skeleton = part.Skeleton;
                            model.SkeletonName = part.SkeletonName;
                        }

                        if (part.Atlas.IsSet && !model.Atlas.IsSet)
                        {
                            model.Atlas = part.Atlas;
                            model.AtlasName = part.AtlasName;
                        }

                        foreach (var page in part.Pages)
                            if (!model.Pages.Contains(page)) model.Pages.Add(page);

                        foreach (var source in part.Sources) model.Sources.Add(source);
                    }

                    foreach (var pair in SkeletonExport.TextureRefs(_assetsManager))
                        textures[pair.Key] = pair.Value;

                    _assetsManager.Clear();
                    ReleaseBatch();
                    Report(b + 1, batches.Count);
                }
            }
            finally
            {
                _assetsManager.LoadDependencies = previous;
            }

            // An atlas names its pages and a page may be in a bundle no part of the model points at,
            // so the textures are traced once every batch has been read.
            foreach (var model in models.Values)
            {
                foreach (var page in model.Pages)
                {
                    if (!textures.TryGetValue(page, out var reference)) continue;

                    model.PageRefs[page] = reference;
                    if (!string.IsNullOrEmpty(reference.Bundle)) model.Sources.Add(reference.Bundle);
                }
            }

            // An extension-less TextAsset is a skeleton candidate whether or not it belongs to anything,
            // so the survey collects a great many names that never become models. A model is something
            // with an atlas; the rest are dropped here, which keeps the count honest and the second pass
            // down to what will actually be written.
            foreach (var key in models.Where(m => !m.Value.Atlas.IsSet).Select(m => m.Key).ToList())
                models.Remove(key);

            log($"骨骼动画：扫完 {batches.Count} 批，找到 {models.Count} 个模型");

            // Second pass: load each model's bundles and write exactly what the first pass named.
            var written = 0;
            var loaded = new HashSet<int>();

            foreach (var model in models.Values.OrderBy(m => m.Base, StringComparer.Ordinal))
            {
                var indexes = new List<int>();
                foreach (var source in model.Sources)
                {
                    var index = CandidateFor(source);
                    if (index >= 0 && !indexes.Contains(index)) indexes.Add(index);
                }

                // Only a model with an atlas is a model. An extension-less TextAsset is a skeleton
                // candidate whether or not it belongs to anything, so the ones that never found an
                // atlas are dropped here rather than written out as folders of their own.
                if (!model.Atlas.IsSet) continue;

                if (indexes.Count == 0)
                {
                    log($"{model.Base}：找不到它所在的 bundle");
                    continue;
                }

                if (!indexes.All(loaded.Contains))
                {
                    _assetsManager.Clear();
                    loaded.Clear();

                    var loadPrevious = _assetsManager.LoadDependencies;
                    _assetsManager.LoadDependencies = false;
                    try
                    {
                        LoadTogether(indexes.Select(i => _candidates[i]).ToList());
                    }
                    finally
                    {
                        _assetsManager.LoadDependencies = loadPrevious;
                    }

                    foreach (var index in indexes) loaded.Add(index);
                }

                try
                {
                    written += SkeletonExport.Write(_assetsManager, ContainersIn(_assetsManager),
                                                    outputRoot, options, log, model);
                }
                catch (Exception ex)
                {
                    log($"{model.Base}：导出失败 {ex.GetType().Name}: {ex.Message}");
                }
            }

            log($"骨骼动画：已导出 {written} 个模型到 {outputRoot}");
            return written;
        }

        public ExportReport Export(string outputRoot, ExportOptions options)
        {
            var report = new ExportReport();
            Directory.CreateDirectory(outputRoot);

            var claimed = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

            if (!_released)
            {
                // The tree fit one batch, so everything is still loaded and there is nothing to
                // read again. Per-asset progress, because the asset count is the whole job.
                var targets = CollectTargets(_assetsManager, options);
                Report(0, targets.Count);
                Merge(report, WriteTargets(targets, outputRoot, options, true, claimed, _assetsManager));
                Finish(report);
                return report;
            }

            // Batched: read one batch in, export what it holds, drop it, next. Peak memory is one
            // batch however many bundles the tree has, which is the point -- a 4.1 GB cache exports
            // in the same footprint as a small one.
            var batches = new List<(int Start, int Count)>(Batches(_candidates));
            LogInfo($"分 {batches.Count} 批、{ExportThreads} 线程导出 {_candidates.Count} 个文件 " +
                    $"（每批最多 {BatchFiles} 个文件或 {BatchBytes / (1024 * 1024)} MB）");

            var list = _candidates;
            var done = 0;

            // One worker per batch: it loads its own batch and writes it while the other workers do
            // the same, so the load of one batch overlaps the encode of another. Progress is by file
            // here -- the asset count is only known once the whole tree has been read.
            Parallel.ForEach(batches,
                new ParallelOptions { MaxDegreeOfParallelism = ExportThreads },
                NewWorker,
                (batch, loopState, worker) =>
                {
                    var loadMark = Stopwatch.GetTimestamp();
                    LoadBatch(worker.Manager, list, batch.Start, batch.Count, out var f, out var o, out _);
                    Stats.AddPhase("导出：加载一批", Stopwatch.GetTimestamp() - loadMark);

                    Interlocked.Add(ref _loadedFiles, f);
                    Interlocked.Add(ref _loadedObjects, o);
                    var targets = CollectTargets(worker.Manager, options);
                    var fragment = WriteTargets(targets, outputRoot, options, false, claimed, worker.Manager);
                    lock (report) Merge(report, fragment);
                    worker.Manager.Clear();
                    Report(Interlocked.Add(ref done, batch.Count), list.Count);
                    return worker;
                },
                worker => worker.Dispose());

            LogInfo($"共读入 {LoadedFiles} 个序列化文件、{LoadedObjects} 个对象");
            Finish(report);
            var summary = Stats.Report();
            LogInfo(summary);

            // Also to a file. The UI log shows the last 14 lines, and logcat is not usable on this
            // device either -- the system's own GPU spam rolls the 256 KB buffer over before a five
            // minute export finishes, and the report comes back empty. A file is the only place a
            // measurement reliably survives to be read.
            if (!string.IsNullOrEmpty(ReportPath))
            {
                try { File.WriteAllText(ReportPath, summary); }
                catch (Exception ex) { LogWarn($"报告写不出去：{ex.Message}"); }
            }
            return report;
        }

        /// <summary>Everything currently loaded that the requested kind covers.</summary>
        private static List<AssetStudio.Object> CollectTargets(AssetsManager manager, ExportOptions options)
        {
            // Auto takes everything and lets Plan() decide the format per asset, so a bundle only
            // exports as far as there is a real exporter for its types. The explicit kinds stay
            // available for exporting one category at a time.
            var targets = new List<AssetStudio.Object>();
            foreach (var f in manager.AssetsFileList)
            {
                if (f?.Objects == null) continue;
                foreach (var o in f.Objects)
                {
                    if (o != null && MatchesAny(o, options.Categories)) targets.Add(o);
                }
            }
            return targets;
        }

        /// <summary>
        /// Writes one batch's assets into <paramref name="report"/>, which accumulates across
        /// batches. <paramref name="perAssetProgress"/> is false when the caller drives the bar
        /// itself at file granularity instead.
        ///
        /// Runs several assets at once: the work is one independent decode, encode and file write
        /// per asset, and serially it left seven of the phone's eight cores idle while the PNG
        /// encoder saturated one (measured: 101% of a single core for the whole export).
        /// </summary>
        /// <summary>
        /// Writes one batch's assets and returns what happened to them.
        ///
        /// Serial on purpose: the parallelism is one level up, one worker per batch, because that
        /// also lets one batch's load overlap another batch's writes. <paramref name="claimed"/> is
        /// shared by every worker.
        /// </summary>
        private ExportReport WriteTargets(List<AssetStudio.Object> targets, string outputRoot,
                                          ExportOptions options, bool perAssetProgress,
                                          ConcurrentDictionary<string, bool> claimed,
                                          AssetsManager manager)
        {
            var report = new ExportReport { Matched = targets.Count };

            foreach (var g in targets.GroupBy(o => o.type.ToString()))
            {
                report.ByType[g.Key] = Get(report.ByType, g.Key) + g.Count();
            }

            // Built once for whatever this manager has loaded. The container list is per file and
            // the assets it names have to be resolved through it, so it is read here rather than
            // once per asset.
            var containers = ContainersIn(manager);

            var done = 0;
            foreach (var obj in targets)
            {
                ExportPlan plan;
                try
                {
                    plan = Plan(obj, options, containers);
                }
                catch (Exception ex)
                {
                    Fail(report, obj, ex);
                    if (perAssetProgress) Report(++done, targets.Count);
                    continue;
                }

                if (plan == null)
                {
                    var typeName = obj.type.ToString();
                    report.Unexported[typeName] = Get(report.Unexported, typeName) + 1;
                }
                else
                {
                    // Identity and destination are different questions, tracked separately in one
                    // shared map under different prefixes.
                    //
                    // Identity is name plus pathID: an Addressables cache keeps one asset in many
                    // bundles, and writing it once is the point.
                    var identity = "id\u0000" + DisplayName(obj) + "\u0000" + obj.m_PathID;
                    if (!claimed.TryAdd(identity, true))
                    {
                        report.Skipped++;
                        var skippedType = obj.type.ToString();
                        report.SkippedByType[skippedType] = Get(report.SkippedByType, skippedType) + 1;

                        if (report.Skipped <= 5)
                            LogWarn($"跳过：{DisplayName(obj)}（pathID {obj.m_PathID}）已经在别的包里导出过了");
                    }
                    else
                    {
                        // One folder per object type, so a tree of thousands does not land in one
                        // directory: textures beside textures, scripts beside scripts, and a model's
                        // parts findable together.
                        var folder = Path.Combine(outputRoot, obj.type.ToString());

                        // Created once per folder rather than once per asset.
                        if (claimed.TryAdd("dir\u0000" + folder, true)) Directory.CreateDirectory(folder);

                        var dest = ClaimPath(claimed, folder, DisplayName(obj), plan.Extension);
                        if (plan.Extension == ".rawdata" && options.Mode != ExportKind.RawData) report.RawFallback++;

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
                            Fail(report, obj, ex);
                        }
                    }
                }

                if (perAssetProgress) Report(++done, targets.Count);
            }

            return report;
        }

        /// <summary>Records one asset that could not be planned or written.</summary>
        private void Fail(ExportReport report, AssetStudio.Object obj, Exception ex)
        {
            report.Failed++;
            var msg = $"{DisplayName(obj)} ({obj.type}): {ex.GetType().Name}: {ex.Message}";

            // Capped: a batch of assets sharing one failure mode can produce thousands of messages,
            // and every one of them is a live string.
            if (report.Errors.Count < MaxReportedErrors) report.Errors.Add(msg);
            LogWarn(msg);
        }

        /// <summary>Folds one batch's fragment into the run's report.</summary>
        private static void Merge(ExportReport into, ExportReport from)
        {
            into.Matched += from.Matched;
            into.Exported += from.Exported;
            into.Skipped += from.Skipped;
            into.Failed += from.Failed;
            into.RawFallback += from.RawFallback;
            foreach (var kv in from.SkippedByType) into.SkippedByType[kv.Key] = Get(into.SkippedByType, kv.Key) + kv.Value;

            foreach (var kv in from.ByType) into.ByType[kv.Key] = Get(into.ByType, kv.Key) + kv.Value;
            foreach (var kv in from.Unexported) into.Unexported[kv.Key] = Get(into.Unexported, kv.Key) + kv.Value;

            foreach (var e in from.Errors)
            {
                if (into.Errors.Count >= MaxReportedErrors) break;
                into.Errors.Add(e);
            }
        }

        /// <summary>One summary for the whole run, rather than one per batch.</summary>
        private void Finish(ExportReport report)
        {
            if (report.ByType.Count > 0)
            {
                LogInfo($"{report.Matched} asset(s): " + string.Join(" ",
                    report.ByType.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => $"{kv.Key} x{kv.Value}")));
            }

            if (report.SkippedByType.Count > 0)
            {
                LogWarn($"{report.Skipped} 个因路径重复被跳过：" + string.Join(" ",
                    report.SkippedByType.OrderByDescending(kv => kv.Value).Take(8)
                        .Select(kv => $"{kv.Key}x{kv.Value}")));
            }

            if (report.RawFallback > 0)
            {
                LogInfo($"{report.RawFallback} 个对象没有可读字段，已写出原始字节");
            }

            if (report.Unexported.Count > 0)
            {
                LogWarn("没有可用的导出器：" + string.Join(", ",
                    report.Unexported.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => $"{kv.Key}x{kv.Value}")));
            }
        }

        private static int Get(Dictionary<string, int> d, string k)
            => d.TryGetValue(k, out var v) ? v : 0;

        internal static bool Matches(AssetStudio.Object o, ExportKind kind) => kind switch
        {
            // Everything has an exporter: Plan() falls back to the bytes the file holds when nothing
            // can describe the object. Deciding that here by calling Plan() meant dumping every
            // object twice on an Auto export -- once to decide, once to write.
            ExportKind.Auto => true,
            ExportKind.Audio => o is AudioClip,
            ExportKind.MonoBehaviour => o is MonoBehaviour,
            ExportKind.Font => o is Font f && f.m_FontData != null && f.m_FontData.Length > 0,
            ExportKind.Video => o is VideoClip v && v.m_VideoData != null && v.m_ExternalResources.m_Size > 0,

            // Deliberately last and defined as "none of the others", so the categories partition the
            // tree: selecting all of them is everything, and adding a category later cannot silently
            // steal objects from this one.
            ExportKind.Other => !IsCategorised(o),
            ExportKind.Texture => o is Texture2D t && t.m_Width > 0 && t.m_Height > 0,
            ExportKind.Sprite => o is Sprite s && s.m_Rect.width > 0 && s.m_Rect.height > 0,
            ExportKind.Mesh => o is Mesh,
            ExportKind.TextAsset => o is TextAsset,
            ExportKind.JsonDump => true,
            ExportKind.RawData => true,
            ExportKind.TextureRaw => o is Texture2D t2 && t2.m_Width > 0 && t2.m_Height > 0,
            _ => false,
        };

        internal sealed class ExportPlan
        {
            public string Extension;
            public Action<AssetStudio.Object, string, ExportOptions> Write;
        }

        /// <summary>
        /// Chooses the output format for one asset from its actual runtime type. This is the whole
        /// point of ExportKind.Auto: nothing is dropped just because no checkbox covered it, every
        /// type either has a real exporter here or falls back to a JSON dump.
        /// </summary>
        internal static ExportPlan Plan(AssetStudio.Object o, ExportOptions options,
                                        IReadOnlyDictionary<AssetStudio.Object, string> containers = null)
        {
            switch (o)
            {
                case Texture2D raw when options.Mode == ExportKind.TextureRaw
                                        && raw.m_Width > 0 && raw.m_Height > 0:
                    return new ExportPlan
                    {
                        // Whatever the file holds: DXT1/5, ASTC, ETC2, or plain RGBA32. This is the
                        // step that a DDS/KTX/TGA header would be prepended to.
                        Extension = ".bin",
                        Write = (obj, dest, opt) =>
                        {
                            var data = ((Texture2D)obj).image_data.GetData();
                            File.WriteAllBytes(dest, data);
                            Stats.AddRaw(data.Length);
                        },
                    };

                case Texture2D tex when tex.m_Width > 0 && tex.m_Height > 0:
                    return new ExportPlan
                    {
                        Extension = ".png",
                        Write = (obj, dest, opt) =>
                        {
                            var asset = (Texture2D)obj;
                            var opaque = FormatHasNoAlpha(asset.m_TextureFormat);
                            var t0 = Stopwatch.GetTimestamp();

                            // Straight to a buffer and into the encoder, with no ImageSharp image in
                            // between: LoadPixelData was 39.4s of CPU for 8487 textures, allocating
                            // and copying into an intermediate the encoder never wanted.
                            if (RustPngNative.Available)
                            {
                                var buffer = asset.DecodeToBgraBuffer(out var w, out var h);
                                Stats.AddTexture(asset.m_TextureFormat.ToString(),
                                                 Stopwatch.GetTimestamp() - t0, (long)w * h);
                                if (buffer != null)
                                {
                                    // A fresh mark: reusing t0 here counted the decode a second time
                                    // and inflated the encode by everything the decode costs.
                                    var encodeMark = Stopwatch.GetTimestamp();
                                    try
                                    {
                                        if (RustPngNative.TryWriteBuffer(dest, buffer, w, h,
                                                                         opt.FlipTextures, opaque))
                                        {
                                            Stats.AddEncode(Stopwatch.GetTimestamp() - encodeMark);
                                            return;
                                        }
                                    }
                                    finally
                                    {
                                        Texture2DExtensions.ReturnDecodedBuffer(buffer);
                                    }
                                }
                                // Fall through: the swizzle path, or the encoder refused.
                            }

                            var image = asset.ConvertToImage(false);
                            Stats.AddTexture(asset.m_TextureFormat.ToString(),
                                             Stopwatch.GetTimestamp() - t0,
                                             (long)asset.m_Width * asset.m_Height);
                            WriteImage(image, dest, opt, opt.FlipTextures, opaque);
                        },
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
                                throw new InvalidOperationException("网格没有可用的几何数据");
                        },
                    };

                case TextAsset ta:
                    return new ExportPlan
                    {
                        Extension = TextAssetExtension(ta, containers),
                        Write = (obj, dest, opt) => File.WriteAllBytes(dest, ((TextAsset)obj).m_Script),
                    };

                case AudioClip clip:
                {
                    // Converted at plan time rather than in the writer, because the extension decides
                    // the output path and only the conversion knows whether this is a wav or an ogg.
                    var payload = AudioCodec.Convert(clip, out var audioExtension, out var audioError);
                    if (payload == null)
                    {
                        throw new InvalidOperationException(audioError ?? "音频转换失败");
                    }

                    var bytes = payload;
                    return new ExportPlan
                    {
                        Extension = audioExtension,
                        Write = (obj, dest, opt) => File.WriteAllBytes(dest, bytes),
                    };
                }

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
                    if (options.Mode == ExportKind.RawData)
                    {
                        return RawPlan();
                    }

                    // Decided here rather than inside the writer: the extension has to be known
                    // before the output path is built, and asking "can this be dumped?" is the same
                    // work as dumping it.
                    var json = TryDump(o);
                    if (json != null)
                    {
                        return new ExportPlan
                        {
                            Extension = ".json",
                            Write = (obj, dest, opt) => File.WriteAllText(dest, json, new UTF8Encoding(false)),
                        };
                    }

                    // No embedded type tree and no fields reflection can read. Writing the bytes the
                    // file holds is the only honest thing left, and it is what makes Auto mean
                    // "everything" instead of "everything that happens to be dumpable" -- 3413 of
                    // 10706 objects in one test game were silently dropped without it.
                    return RawPlan();
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

        private static ExportPlan RawPlan() => new ExportPlan
        {
            Extension = ".rawdata",
            Write = (obj, dest, opt) => File.WriteAllBytes(dest, obj.GetRawData()),
        };

        /// <summary>
        /// The object as JSON, or null when neither the embedded type tree nor reflection over the
        /// parsed fields can describe it. DumpObject swallows its own failures and returns null, so
        /// null here means "nothing to say", not "an error happened".
        /// </summary>
        private static string TryDump(AssetStudio.Object o)
        {
            try
            {
                var json = o.Dump() ?? o.DumpObject();
                return string.IsNullOrEmpty(json) ? null : json;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteJson(AssetStudio.Object obj, string dest)
        {
            // Dump() needs an embedded type tree; DumpObject() reflects over parsed fields.
            var json = obj.Dump() ?? obj.DumpObject();
            if (string.IsNullOrEmpty(json))
                throw new InvalidOperationException("no typetree and no dumpable fields");
            File.WriteAllText(dest, json, new UTF8Encoding(false));
        }

        /// <summary>
        /// Formats that really have no alpha channel, so every pixel is opaque and the PNG can be
        /// written as RGB -- a quarter less data into the deflate, and measurably more than a
        /// quarter faster.
        ///
        /// ASTC is deliberately not here. Its blocks are 128 bits whatever Unity calls them, and
        /// "ASTC_RGB_6x6" is a compression setting rather than a promise that the data has no alpha:
        /// listed as opaque, it forced an RGB PNG over a Spine atlas page whose transparency was
        /// real, and the model came out shredded -- against the desktop, whose encoder always writes
        /// RGBA. The pixel scan decides for ASTC, and it is exact: if no alpha byte is anything but
        /// opaque, RGB loses nothing.
        ///
        /// The remaining entries name formats with no alpha plane at all, where the decoded alpha is
        /// filler rather than data.
        /// </summary>
        private static bool FormatHasNoAlpha(TextureFormat format) => format switch
        {
            TextureFormat.RGB24 => true,
            TextureFormat.RGB565 => true,
            TextureFormat.ETC_RGB4 => true,
            TextureFormat.ETC_RGB4_3DS => true,
            TextureFormat.ETC_RGB4Crunched => true,
            TextureFormat.ETC2_RGB => true,
            _ => false,
        };

        private static void WriteImage(Image image, string dest, ExportOptions options,
                                       bool flipDeferred = false, bool assumeOpaque = false)
        {
            if (image == null)
                throw new InvalidOperationException("texture decode returned no image (unsupported format?)");

            using (image)
            {
                var t0 = Stopwatch.GetTimestamp();

                // Rust first, ImageSharp second: both write the same PNG, and they are ordered by
                // what each one's deflate measured (205s against 931s of CPU for 8487 textures).
                // Falls through on any failure, so a missing or broken library costs speed rather
                // than the export.
                if (options.ImageFormat == ImageFormat.Png && image is Image<Bgra32> bgra
                    && RustPngNative.Available && RustPngNative.TryWrite(dest, bgra, flipDeferred, assumeOpaque))
                {
                    Stats.AddEncode(Stopwatch.GetTimestamp() - t0);
                    return;
                }

                // Falling back to ImageSharp, so the flip the native path would have done has to
                // happen here after all.
                if (flipDeferred) image.Mutate(x => x.Flip(FlipMode.Vertical));

                using (var fs = File.Create(dest))
                {
                    if (options.ImageFormat == ImageFormat.Png) image.Save(fs, FastPng());
                    else image.WriteToStream(fs, options.ImageFormat);
                }
                Stats.AddEncode(Stopwatch.GetTimestamp() - t0);
            }
        }

        /// <summary>
        /// PNG settings picked by measurement, because the default is the worst possible choice for
        /// a bulk export on a phone.
        ///
        /// ImageSharp defaults to FilterMethod.Adaptive, which computes all five filters for every
        /// scanline and keeps the smallest, at CompressionLevel.DefaultCompression. The encode is
        /// ~99% of the per-asset cost (decode and flip together are under 1%), so its settings are
        /// the settings for the whole export. Measured on one 860x730 texture:
        ///
        ///     Adaptive + level 6 (the default)   211 ms   736 KB
        ///     None     + level 1                  48 ms   977 KB
        ///     Paeth    + level 1                  44 ms   848 KB
        ///
        /// Paeth at low effort is both faster and smaller than no filter at all, so there is no
        /// reason to give the compression up: ~4.5x the speed for ~15% more bytes.
        /// </summary>
        private static PngEncoder FastPng() => new PngEncoder
        {
            FilterMethod = PngFilterMethod.Paeth,
            CompressionLevel = PngCompressionLevel.Level1,
        };

        /// <summary>
        /// Human-readable name for an asset.
        ///
        /// Object.Name is a public field that AssetStudio never assigns -- the real name lives in
        /// NamedObject.m_Name -- so it must not be used for file naming. The pathID suffix keeps
        /// names unique when several assets share a name (common across bundles/containers).
        /// </summary>
        /// <summary>
        /// What an asset is called: its own name and nothing else.
        ///
        /// A pathID used to be appended to every file, and the source bundle to every unnamed one,
        /// which is unique and unreadable:
        /// "unnamed_CAB-40a03710042fe83c0197cce4b4f1a7e9_-7528991862304389744.json". Uniqueness is
        /// still needed -- a game holds several assets called "icon" and one directory cannot hold
        /// both -- but it belongs in the path, and only where it is needed. See ClaimPath.
        /// </summary>
        internal static string DisplayName(AssetStudio.Object obj)
            => SafeName((obj as NamedObject)?.m_Name);

        /// <summary>
        /// Which candidate holds a SerializedFile, or -1.
        ///
        /// LoadFilesAndFolders pulls in dependencies as well as the files it was handed, and a
        /// dependency can live in another batch. Searching only the current batch is what made an
        /// index entry name the wrong file: every object of a bundle that had been loaded as someone
        /// else's dependency was filed under that someone else, so opening or exporting it then
        /// searched the wrong file and found nothing. It cost two thirds of one filtered export
        /// (612 of 4265) and every preview opened from an index.
        /// </summary>
        /// <summary>
        /// The real path of the file a SerializedFile came from.
        ///
        /// Not fullName. For a file inside a bundle, fullName is the reader's path on the
        /// *decompressed* stream, which is the bundle's internal name -- "CAB-40a03710042fe83c0197cce4b4f1a7e9"
        /// -- and it is not a path at all, nor is it unique: every bundle from one build can carry
        /// the same internal name. originalPath is the path the bundle was opened from, which is
        /// what the scan's candidates are keyed by and what makes an index entry findable again.
        ///
        /// Preferring fullName is what made every index entry name a file that does not exist: the
        /// entry could not be traced back to its bundle, so opening one reported "the object is not
        /// in this file" and a filtered export matched 368 of 1905 objects.
        /// </summary>
        internal static string SourcePath(SerializedFile file)
            => file?.originalPath ?? file?.fullName ?? file?.fileName ?? string.Empty;

        private int CandidateFor(string source)
        {
            if (_candidates == null || string.IsNullOrEmpty(source)) return -1;

            // The common case is an exact hit: a plain bundle's reader path is the candidate's path.
            _candidateByPath ??= BuildCandidateIndex();
            if (_candidateByPath.TryGetValue(source, out var direct)) return direct;

            // Otherwise it is something inside a container -- a zip's entry path is the archive's
            // path plus the entry name -- so the longest candidate path that is a prefix wins.
            var best = -1;
            var bestLength = -1;
            foreach (var pair in _candidateByPath)
            {
                if (pair.Key.Length <= bestLength) continue;
                if (!source.StartsWith(pair.Key, StringComparison.Ordinal)) continue;

                best = pair.Value;
                bestLength = pair.Key.Length;
            }
            return best;
        }

        private Dictionary<string, int> _candidateByPath;

        private Dictionary<string, int> BuildCandidateIndex()
        {
            var map = new Dictionary<string, int>(_candidates.Count, StringComparer.Ordinal);
            for (var i = 0; i < _candidates.Count; i++) map[_candidates[i].Path] = i;
            return map;
        }

        /// <summary>
        /// The path each asset is published under, from the AssetBundle's own container list.
        ///
        /// This is where a Live2D model's extensions actually live. Its parts are named "model",
        /// "physics", "motion" with no extension at all, and the bundle records that they are
        /// published as "assets/live2d/xxx/model.moc3", ".../xxx.physics3.json" and so on. The
        /// desktop reads the extension from there; without it those files can only come out as
        /// ".txt", which is what they were doing.
        ///
        /// The mapping is per file, exactly as the desktop builds it: a PreloadData object supplies
        /// the table, and each container entry names a slice of it.
        /// </summary>
        private static Dictionary<AssetStudio.Object, string> ContainersIn(AssetsManager manager)
        {
            var map = new Dictionary<AssetStudio.Object, string>();

            foreach (var file in manager.AssetsFileList)
            {
                var preloadTable = new List<PPtr<AssetStudio.Object>>();

                foreach (var asset in file.Objects)
                {
                    switch (asset)
                    {
                        case PreloadData preload:
                            preloadTable = preload.m_Assets;
                            break;

                        case AssetBundle bundle:
                            var streamed = bundle.m_IsStreamedSceneAssetBundle;
                            if (!streamed) preloadTable = bundle.m_PreloadTable;

                            foreach (var entry in bundle.m_Container)
                            {
                                var size = streamed ? preloadTable.Count : entry.Value.preloadSize;
                                var end = entry.Value.preloadIndex + size;

                                for (var k = entry.Value.preloadIndex; k < end && k < preloadTable.Count; k++)
                                {
                                    if (preloadTable[k].TryGet(out var target) && target != null)
                                        map[target] = entry.Key;
                                }
                            }
                            break;

                        case ResourceManager resources:
                            foreach (var entry in resources.m_Container)
                            {
                                if (entry.Value.TryGet(out var target) && target != null)
                                    map[target] = entry.Key;
                            }
                            break;
                    }
                }
            }

            return map;
        }

        /// <summary>
        /// What extension a TextAsset is written with.
        ///
        /// A TextAsset's name is often its real file name with its real extension already on it. A
        /// Live2D model is a set of them -- "model.model3.json", "model.physics3.json",
        /// "model.motion3.json" -- and appending ".txt" turns a model into "model.model3.json.txt",
        /// which nothing will open. The desktop keeps the name's extension for the same reason.
        ///
        /// The desktop then falls back to the extension of the asset's *container* path -- the path
        /// the bundle publishes the asset under, like "assets/live2d/model.moc3" -- and only then to
        /// ".txt". That is not replicated here: the container path has to be resolved through the
        /// bundle's preload table, and the near substitute (the bundle's own file name) produces
        /// ".unity3d" on a TextAsset that is nothing of the sort. ".txt" is honest until the real
        /// container path is available.
        /// </summary>
        private static string TextAssetExtension(TextAsset asset,
                                                 IReadOnlyDictionary<AssetStudio.Object, string> containers)
        {
            var name = asset?.m_Name;
            if (!string.IsNullOrEmpty(name) && Path.HasExtension(name)) return string.Empty;

            // The container path, as the desktop does. It carries the extension the asset is
            // published with, and that extension is the one the game's own tooling expects -- an
            // ".asset" here is not a mistake to be corrected into ".json", it is the name the
            // skeleton is addressed by, and the viewer's own profile dialog asks for exactly that
            // file. A "looks like JSON so call it .json" rule used to override this and was wrong.
            if (containers != null && asset != null && containers.TryGetValue(asset, out var container)
                && !string.IsNullOrEmpty(container))
            {
                var fromContainer = Path.GetExtension(container);
                if (!string.IsNullOrEmpty(fromContainer)) return fromContainer;
            }

            return ".txt";
        }

        /// <summary>
        /// The first free name for an asset: "icon.png", then "icon (2).png", and so on.
        ///
        /// A name is not unique and one directory cannot hold two files of it, so collisions still
        /// have to be separated -- but a counter says so in a few characters and only where it is
        /// needed, unlike the pathID this replaced.
        ///
        /// The extension is not added when the name already ends with it, so a TextAsset called
        /// "setting_config.json" is written as setting_config.json rather than setting_config.json.txt.
        /// </summary>
        private static string ClaimPath(ConcurrentDictionary<string, bool> claimed, string root,
                                        string stem, string extension)
        {
            if (stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) extension = string.Empty;

            var candidate = Path.Combine(root, stem + extension);
            if (claimed.TryAdd("path\u0000" + candidate, true)) return candidate;

            for (var i = 2; i < 100000; i++)
            {
                candidate = Path.Combine(root, $"{stem} ({i}){extension}");
                if (claimed.TryAdd("path\u0000" + candidate, true)) return candidate;
            }

            return Path.Combine(root, stem + extension);
        }

        /// <summary>The file an object came from, as a short name to disambiguate by.</summary>
        private static string SourceTag(AssetStudio.Object obj)
        {
            var file = obj.assetsFile;
            var path = file?.fileName;
            if (string.IsNullOrEmpty(path)) path = file?.fullName;
            if (string.IsNullOrEmpty(path)) return null;

            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name)) return null;

            var safe = SafeName(name);
            return safe == "unnamed" ? null : safe;
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

        /// <summary>
        /// Releases everything the loader is holding and forgets the scan, so the instance can be
        /// reused or dropped without the objects staying reachable.
        /// </summary>
        public void Clear()
        {
            _indexedCandidate = -1;
            _candidateByPath = null;
            _assetsManager.Clear();
            _candidates = null;
            _released = false;
            _loadedFiles = 0;
            _loadedObjects = 0;
        }

        public void Dispose() => Clear();
    }
}
