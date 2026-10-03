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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

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
        private const int BatchFiles = 128;
        private const long BatchBytes = 64L * 1024 * 1024;

        /// <summary>How many failure messages an export keeps, so one bad batch cannot fill memory.</summary>
        private const int MaxReportedErrors = 100;

        /// <summary>
        /// How many assets are decoded, encoded and written at once.
        ///
        /// This is a memory budget as much as a CPU one: every asset in flight holds a decoded
        /// image (4 MB for 1024x1024 Bgra32, 16 MB for 2048x2048) plus the encoder's buffers, on
        /// top of the load batch that is already resident. Half the cores leaves the phone's big
        /// cores for this and still gives most of the win.
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

        /// <summary>
        /// What the scan found. These are plain counters rather than a live view of the loaded
        /// objects, because in the batched case there are no loaded objects left to count.
        /// </summary>
        public int LoadedFiles { get; private set; }
        public int LoadedObjects { get; private set; }

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

        /// <summary>Recursively scans <paramref name="root"/> and loads every Unity file found.</summary>
        public void Load(string root)
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

            var full = Path.GetFullPath(root);
            LogInfo($"Scanning {full}");

            var clock = Stopwatch.StartNew();

            // 1. Walk. One pass of readdir plus a stat per entry: linear, and parallelising it
            //    only adds contention on the same directory inode.
            var everything = Directory.GetFiles(full, "*.*", SearchOption.AllDirectories);
            var walkMs = clock.ElapsedMilliseconds;
            clock.Restart();

            // 2. Decide by header, not by name. The loader already identifies files by content in
            //    FileReader.CheckFileType, so the pre-filter has to agree with it, and an extension
            //    list cannot. A game directory is mostly extensionless files: an Addressables
            //    cache, for one, is ~4900 __data bundles interleaved with ~4900 __info JSON
            //    manifests. Those are identical extensionless shapes, so a name-based filter lets
            //    every manifest through to be opened, sniffed and thrown away again.
            var candidates = SniffCandidates(everything);
            var sniffMs = clock.ElapsedMilliseconds;
            clock.Restart();

            LogInfo($"{candidates.Count} candidate file(s) of {everything.Length} " +
                    $"(walk {walkMs} ms, sniff {sniffMs} ms, dropped {everything.Length - candidates.Count})");
            Report(0, candidates.Count);

            _candidates = candidates;
            _released = false;
            LoadedFiles = 0;
            LoadedObjects = 0;

            var batches = new List<(int Start, int Count)>(Batches(_candidates));
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
                LogInfo($"Loaded {LoadedFiles} serialized file(s), {LoadedObjects} object(s) from {_lastParent}");
            }
            else
            {
                // Too big to hold. Read through it once so the counts are real, dropping each batch
                // before opening the next; Export reads it again, one batch at a time.
                LogInfo($"too large to hold in one go: reading {batches.Count} batches and releasing each");
                var done = 0;
                foreach (var (start, count) in batches)
                {
                    LoadBatch(start, count);
                    _assetsManager.Clear();
                    done += count;
                    Report(done, _candidates.Count);
                }
                _released = true;
                LogInfo($"Loaded {LoadedFiles} serialized file(s), {LoadedObjects} object(s) total");
            }

            LogInfo($"load {clock.ElapsedMilliseconds} ms");
            Report(1, 1);
        }

        private string _lastParent;

        /// <summary>
        /// Loads one range of the scan. This is the only place the loader is called, so it is the
        /// only place that decides how much is in memory at once.
        /// </summary>
        private void LoadBatch(int start, int count)
        {
            // AssetsManager.LoadFilesAndFolders runs every entry through Path.GetFullPath, so it
            // needs absolute paths. Relative names would silently resolve against the process
            // working directory, which on Android is "/" and matches nothing.
            // (It also clears the list it is given, hence the local.)
            var paths = new List<string>(count);
            for (var i = 0; i < count; i++) paths.Add(_candidates[start + i].Path);

            // Dependencies are not a problem for batching: LoadAssetsFile queues the externals it
            // finds next to the file on disk into importFiles, and Load() drains that queue while
            // it grows, so a batch pulls in what it needs from the tree by name. A dependency
            // shared by many batches is simply loaded more than once.
            _assetsManager.LoadFilesAndFolders(out var parent, paths);
            _lastParent = parent;
            LoadedFiles += _assetsManager.AssetsFileList.Count;
            LoadedObjects += _assetsManager.AssetsFileList.Sum(f => f.Objects.Count);
        }

        /// <summary>
        /// Splits the scan into load batches, cut by whichever budget comes first.
        ///
        /// Consecutive files only, in path order: the scan is sorted, so a bundle's parts and its
        /// siblings stay next to each other, and the loader's dependency lookup is by name rather
        /// than by position, so nothing depends on a batch boundary falling anywhere in particular.
        /// </summary>
        private static IEnumerable<(int Start, int Count)> Batches(List<Candidate> files)
        {
            var i = 0;
            while (i < files.Count)
            {
                var end = i;
                long bytes = 0;
                while (end < files.Count && end - i < BatchFiles)
                {
                    // Always take at least one file, even one bigger than the whole budget.
                    if (end > i && bytes + files[end].Length > BatchBytes) break;
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


        public ExportReport Export(string outputRoot, ExportOptions options)
        {
            var report = new ExportReport();
            Directory.CreateDirectory(outputRoot);

            if (!_released)
            {
                // The tree fit one batch, so everything is still loaded and there is nothing to
                // read again. Per-asset progress, because the asset count is the whole job.
                var targets = CollectTargets(options);
                Report(0, targets.Count);
                WriteTargets(targets, outputRoot, options, report, perAssetProgress: true);
                Finish(report);
                return report;
            }

            // Batched: read one batch in, export what it holds, drop it, next. Peak memory is one
            // batch however many bundles the tree has, which is the point -- a 4.1 GB cache exports
            // in the same footprint as a small one.
            LogInfo($"exporting {_candidates.Count} file(s), at most {BatchFiles} or " +
                    $"{BatchBytes / (1024 * 1024)} MB per batch");
            var done = 0;
            foreach (var (start, count) in Batches(_candidates))
            {
                LoadBatch(start, count);
                // Progress is by file here, not by asset: the asset count is only known once the
                // whole tree has been read, and the bar restarting for every batch would be worse
                // than a coarse one.
                WriteTargets(CollectTargets(options), outputRoot, options, report, perAssetProgress: false);
                _assetsManager.Clear();
                done += count;
                Report(done, _candidates.Count);
            }
            Finish(report);
            return report;
        }

        /// <summary>Everything currently loaded that the requested kind covers.</summary>
        private List<AssetStudio.Object> CollectTargets(ExportOptions options)
        {
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
        private void WriteTargets(List<AssetStudio.Object> targets, string outputRoot, ExportOptions options,
                                  ExportReport report, bool perAssetProgress)
        {
            var tally = new Tally();
            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            var unexported = new Dictionary<string, int>(StringComparer.Ordinal);
            var errors = new List<string>();

            // Every thread tallies locally and the results are merged after the loop, so nothing in
            // the hot path takes a lock. The one thing that genuinely needs one is the set of file
            // names already spoken for.
            var claimed = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

            foreach (var g in targets.GroupBy(o => o.type.ToString()))
            {
                byType[g.Key] = Get(byType, g.Key) + g.Count();
            }

            void Fail(AssetStudio.Object obj, Exception ex)
            {
                Interlocked.Increment(ref tally.Failed);
                var msg = $"{DisplayName(obj)} ({obj.type}): {ex.GetType().Name}: {ex.Message}";

                // Capped: a batch of assets sharing one failure mode can produce thousands of
                // messages, and every one of them is a live string plus a UI post.
                lock (errors)
                {
                    if (errors.Count >= MaxReportedErrors) return;
                    errors.Add(msg);
                }
                LogWarn(msg);
            }

            Parallel.ForEach(targets,
                new ParallelOptions { MaxDegreeOfParallelism = ExportThreads },
                obj =>
                {
                    Interlocked.Increment(ref tally.Matched);

                    ExportPlan plan;
                    try
                    {
                        plan = Plan(obj, options);
                    }
                    catch (Exception ex)
                    {
                        Fail(obj, ex);
                        if (perAssetProgress) Report(Interlocked.Increment(ref tally.Done), targets.Count);
                        return;
                    }

                    if (plan == null)
                    {
                        var typeName = obj.type.ToString();
                        lock (unexported) unexported[typeName] = Get(unexported, typeName) + 1;
                    }
                    else
                    {
                        var dest = Path.Combine(outputRoot, DisplayName(obj) + plan.Extension);

                        // Two bundles can hold different assets with the same name and pathID. One
                        // file cannot hold both, so the first claim wins: serially the last write
                        // won by accident, and letting two threads write the same path at the same
                        // time would interleave them into a corrupt file.
                        if (!claimed.TryAdd(dest, true))
                        {
                            Interlocked.Increment(ref tally.Skipped);
                        }
                        else
                        {
                            try
                            {
                                if (File.Exists(dest) && !options.Overwrite)
                                {
                                    Interlocked.Increment(ref tally.Skipped);
                                }
                                else
                                {
                                    plan.Write(obj, dest, options);
                                    Interlocked.Increment(ref tally.Exported);
                                }
                            }
                            catch (Exception ex)
                            {
                                Fail(obj, ex);
                            }
                        }
                    }

                    if (perAssetProgress) Report(Interlocked.Increment(ref tally.Done), targets.Count);
                });

            report.Matched += tally.Matched;
            report.Exported += tally.Exported;
            report.Skipped += tally.Skipped;
            report.Failed += tally.Failed;

            foreach (var kv in byType) report.ByType[kv.Key] = Get(report.ByType, kv.Key) + kv.Value;
            foreach (var kv in unexported) report.Unexported[kv.Key] = Get(report.Unexported, kv.Key) + kv.Value;

            foreach (var e in errors)
            {
                if (report.Errors.Count >= MaxReportedErrors) break;
                report.Errors.Add(e);
            }
        }

        private sealed class Tally
        {
            public int Matched;
            public int Exported;
            public int Skipped;
            public int Failed;
            public int Done;
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

            if (report.Unexported.Count > 0)
            {
                LogWarn("no exporter for: " + string.Join(", ",
                    report.Unexported.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => $"{kv.Key}x{kv.Value}")));
            }
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
                if (options.ImageFormat == ImageFormat.Png) image.Save(fs, FastPng());
                else image.WriteToStream(fs, options.ImageFormat);
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

        /// <summary>
        /// Releases everything the loader is holding and forgets the scan, so the instance can be
        /// reused or dropped without the objects staying reachable.
        /// </summary>
        public void Clear()
        {
            _assetsManager.Clear();
            _candidates = null;
            _released = false;
            LoadedFiles = 0;
            LoadedObjects = 0;
        }

        public void Dispose() => Clear();
    }
}
