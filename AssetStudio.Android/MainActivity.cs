using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AssetStudio;
using AssetStudioMobile.Ui;

namespace AssetStudioMobile
{
    [Activity(Label = "@string/app_name", MainLauncher = true, Exported = true)]
    public class MainActivity : Activity
    {
        private const int ReqPickTree = 1001;
        private const int ReqPickApk = 1002;

        /// <summary>The export screen. It owns its views; this class owns the behaviour.</summary>
        private ExportPanel _panel;

        /// <summary>Kept so the browser can hand the screen back when it closes.</summary>
        private View _mainRoot;

        private PreviewPanel _browser;

        /// <summary>One batch load at a time: BrowseBatch reuses the extractor's manager.</summary>
        private bool _browsing;

        private Extractor _extractor;
        private string _inputDir;
        private string _outputDir;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Input must live somewhere we can hand to System.IO: Android's content:// URIs have
            // no seekable stream, and AssetStudio's loader is built entirely around
            // Directory.GetFiles(..., SearchOption.AllDirectories). Imported trees are therefore
            // copied onto the filesystem first.
            //
            // The external files dir is preferred over FilesDir: it is reachable from a file
            // manager and from `adb push /sdcard/Android/data/<pkg>/files/bundles`, which makes
            // it trivial to sideload a bundle for testing. Falls back to private storage.
            var external = GetExternalFilesDir(null)?.AbsolutePath;
            _inputDir = Path.Combine(external ?? FilesDir.AbsolutePath, "bundles");
            _outputDir = Path.Combine(
                global::Android.OS.Environment.GetExternalStoragePublicDirectory(
                    global::Android.OS.Environment.DirectoryDownloads)?.AbsolutePath
                ?? Path.Combine(external ?? FilesDir.AbsolutePath, "out"),
                "AssetStudioExport");
            Directory.CreateDirectory(_inputDir);

            Logger.Default = new AndroidLogger(this);

            // Every bundle block is LZ4; the managed codec has no SIMD path on ARM64.
            Lz4Native.Install();

            // ConvertToImage reports its parts, because one number for "texture decode" cannot say
            // whether to look at the codec, the copy, or the flip.
            Texture2DExtensions.PhaseTiming = Stats.AddPhase;

            _mainRoot = BuildUi();
            SetContentView(_mainRoot);
            _panel.InputPath.Text = _inputDir;
            _panel.OutputPath.Text = _outputDir;
            RefreshPermissionUi();
            Append($"input : {_inputDir}");
            Append($"output: {_outputDir}");
            Append("Import a folder of Unity bundles, then Scan, then Export.");

            // `-e dumpastc 1` captures real compressed ASTC payloads so the decoders can be
            // benchmarked on the data a game actually contains instead of on a test image. This has
            // to come after the UI exists -- Append touches the log view, and calling it earlier
            // threw in OnCreate, which restarted the activity without its intent and silently did
            // nothing at all.
            if (Intent?.GetStringExtra("dumpastc") != null || Intent?.GetIntExtra("dumpastc", 0) > 0)
                InstallAstcDump();

            RunIntentAction(Intent);
        }

        private View BuildUi()
        {
            _panel = new ExportPanel(this);

            _panel.ShizukuRequested += () => ShizukuBridge.RequestPermission();
            _panel.GrantRequested += RequestAllFilesAccess;
            _panel.LoadRequested += LoadFromPathField;
            _panel.PickFolderRequested += PickTree;
            _panel.PickApkRequested += PickApkFile;
            _panel.ImportFromAppRequested += ChooseInstalledApp;
            _panel.ScanRequested += () => RunOnBackground(Scan);
            _panel.ExportRequested += () => RunOnBackground(() => Export(_panel.Kind.SelectedItemPosition, _panel.Overwrite.Checked));
            _panel.SelfTestRequested += () => RunOnBackground(() => SelfTest.AppendResults(Append));
            _panel.BrowseRequested += OpenBrowser;

            return Inset(_panel.Root);
        }

        private void RunIntentAction(Intent intent)
        {
            var action = intent?.GetStringExtra("action");
            if (string.IsNullOrEmpty(action)) return;

            // The type-tree reader round-trips every Texture2D/Material/AnimationClip through JSON
            // (AssetStudio/TypeTreeHelper.cs:190). The GUI exposes the same tradeoff as a checkbox
            // (AssetStudioGUIForm.cs:2545) and the CLI as --avoid-typetree; this makes it
            // switchable from adb so the two can be measured on the same device.
            if (intent.HasExtra("typetree")) Extractor.UseTypeTree = intent.GetBooleanExtra("typetree", true);

            switch (action.ToLowerInvariant())
            {
                case "selftest":
                    RunOnBackground(() => SelfTest.AppendResults(Append));
                    break;
                case "scan":
                    RunOnBackground(Scan);
                    break;
                case "apk":
                {
                    // Scripted form of the "Import from app" button, so extracting an installed app
                    // can be driven from adb like the rest of this.
                    var package = intent.GetStringExtra("package");
                    if (string.IsNullOrWhiteSpace(package))
                    {
                        Append("ERROR: -e package is required for action=apk");
                        break;
                    }
                    RunOnBackground(() =>
                    {
                        var apps = ApkImport.ListInstalled(this, Append);
                        var app = apps.Find(a => a.PackageName == package);
                        if (app == null)
                        {
                            Append($"ERROR: {package} is not in the visible package list");
                            return;
                        }
                        ImportInstalledApp(app);
                    });
                    break;
                }
                case "load":
                {
                    // `adb shell input text` cannot reliably type '/' through a CJK IME, so the
                    // path comes in as an extra instead.
                    var p = intent.GetStringExtra("path");
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        _panel.InputPath.Text = p;
                        LoadFromPathField();
                    }
                    else
                    {
                        Append("ERROR: -e path is required for action=load");
                    }
                    break;
                }
                case "browse":
                {
                    // Scripted form of the 浏览 button: resolve the path, scan, then open the
                    // browser. Kind is left at Auto on purpose -- browsing wants every previewable
                    // type, and a filter would hide the TextAssets and Sprites.
                    var browsePath = intent.GetStringExtra("path");
                    RunOnBackground(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(browsePath))
                        {
                            var readable = ResolveReadable(browsePath);
                            if (readable == null) return;
                            _inputDir = readable;
                            RunOnUiThread(() => _panel.InputPath.Text = readable);
                        }

                        Scan();
                        RunOnUiThread(OpenBrowser);
                    });
                    break;
                }
                case "export":
                {
                    // Optional: point the run at a directory without touching the UI field.
                    // Resolved off the UI thread, with the same Shizuku fallback the Load button
                    // uses, so this works on a game's own Android/data directory too.
                    var exportPath = intent.GetStringExtra("path");

                    var kindName = intent.GetStringExtra("kind");
                    var idx = 0;
                    if (!string.IsNullOrEmpty(kindName))
                    {
                        idx = Math.Max(0, Array.IndexOf(Enum.GetNames(typeof(ExportKind)), kindName));
                    }
                    if (intent.GetBooleanExtra("overwrite", false)) _panel.Overwrite.Checked = true;

                    // Scan() reads the kind off the spinner to decide which object types to build,
                    // so the spinner has to agree with what was asked for here. Clamped: an index
                    // past the end of the adapter is a crash, and an unknown kind name used to
                    // produce exactly that.
                    _panel.Kind.SetSelection(Math.Min(idx, _panel.Kind.Adapter.Count - 1));

                    RunOnBackground(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(exportPath))
                        {
                            var readable = ResolveReadable(exportPath);
                            if (readable == null) return;
                            _inputDir = readable;
                            RunOnUiThread(() => _panel.InputPath.Text = readable);
                        }

                        // Each `am start` is a fresh process, so an export launched this way has
                        // no loaded assets unless we scan first.
                        if (_extractor == null) Scan();
                        Export(idx, _panel.Overwrite.Checked);
                    });
                    break;
                }
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            RefreshPermissionUi();
        }

        private void RefreshPermissionUi()
        {
            if (_panel?.PermissionStatus == null) return;
            var granted = StorageAccess.HasAllFilesAccess();
            _panel.GrantVisibility = granted ? ViewStates.Gone : ViewStates.Visible;

            var shizuku = ShizukuBridge.State;
            _panel.ShizukuVisibility = shizuku == ShizukuState.Ready ? ViewStates.Gone : ViewStates.Visible;
            _panel.PermissionStatus.Text = (granted
                    ? "all-files access: GRANTED (read/write any path)"
                    : "all-files access: not granted. SAF still works but copies files.") +
                "\n" + ShizukuBridge.Describe() +
                (shizuku == ShizukuState.Ready
                    ? "\nneeded for /sdcard/Android/data"
                    : "");
        }

        private void RequestAllFilesAccess()
        {
            try
            {
                StartActivity(StorageAccess.BuildAllFilesAccessIntent(this));
            }
            catch (Exception ex)
            {
                Append($"ERROR: cannot open all-files settings ({ex.Message})");
            }
        }

        /// <summary>Loads the directory typed into the path field, then scans it (feature 4).</summary>
        private void LoadFromPathField()
        {
            var typed = _panel.InputPath.Text;
            if (string.IsNullOrWhiteSpace(typed))
            {
                Append("ERROR: no path given");
                return;
            }

            RunOnBackground(() =>
            {
                var readable = ResolveReadable(typed);
                if (readable == null) return;
                _inputDir = readable;
                RunOnUiThread(() => _panel.InputPath.Text = readable);
                Scan();
            });
        }

        /// <summary>
        /// Turns a supplied path into one this app can actually read, staging through Shizuku when
        /// it points into somebody else's Android/data. Runs off the UI thread; returns null when
        /// it cannot be made readable.
        ///
        /// Both entry points need this. The scripted `export` action used to validate and give up
        /// instead, so `am start -e action export -e path /sdcard/Android/data/...` failed with a
        /// permission error even though the same directory worked through the UI -- which is
        /// exactly how it was found.
        /// </summary>
        private string ResolveReadable(string path)
        {
            var full = path?.Trim() ?? "";
            if (full.Length == 0)
            {
                Append("ERROR: no path given");
                return null;
            }
            try { full = Path.GetFullPath(full); } catch { /* keep the raw text for the error */ }

            var problem = StorageAccess.ValidateReadableDirectory(full);
            if (problem == null) return full;

            Append($"{full}: {problem}");
            Append("all-files access does not cover /sdcard/Android/data; trying Shizuku...");

            if (ShizukuBridge.State != ShizukuState.Ready)
            {
                Append($"ERROR: cannot read it, and Shizuku is not usable ({ShizukuBridge.State}). " +
                       "Copy the game's files to /sdcard/Download first.");
                SetStatus("cannot read, and Shizuku unavailable");
                return null;
            }

            // Stage into our own EXTERNAL app dir, not FilesDir: the Shizuku service runs as
            // uid 2000 and cannot write into /data/user/0/<pkg>, which is the app's private
            // sandbox. The external dir is writable by shell (ext_data_rw) and by us.
            var externalRoot = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;
            var staging = Path.Combine(externalRoot, "staged");
            return ShizukuBridge.StageDirectory(full, staging, Append, (c, t) => Report(c, t));
        }

        // ---------------- SAF import ----------------

        /// <summary>Picks a single .apk through SAF. No permission needed at all.</summary>
        /// <summary>
        /// Writes the first few compressed ASTC payloads it sees to the app's files directory.
        /// </summary>
        private void InstallAstcDump()
        {
            var dir = Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath, "astc-dump");
            Directory.CreateDirectory(dir);
            var count = 0;
            Texture2DConverter.RawTextureDump = (format, buffer, length, width, height) =>
            {
                // Runs on every worker thread inside the decode path, so it must not throw and must
                // not let two threads pick the same file name. The first version did both and took
                // the export down with it.
                try
                {
                    var name = format.ToString();
                    if (!name.StartsWith("ASTC_RGB_", StringComparison.Ordinal)) return;

                    var index = System.Threading.Interlocked.Increment(ref count);
                    if (index > 8) return;
                    if (length <= 0 || length > buffer.Length) return;

                    var file = Path.Combine(dir, $"{index:D2}_{name}_{width}x{height}.bin");
                    File.WriteAllBytes(file, buffer.AsSpan(0, length).ToArray());
                }
                catch (Exception ex)
                {
                    Append($"astc dump failed: {ex.Message}");
                }
            };
            Append("ASTC dump armed, writing to " + dir);
        }

        private void PickApkFile()
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.SetType("application/vnd.android.package-archive");
            intent.AddCategory(Intent.CategoryOpenable);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            try
            {
                StartActivityForResult(Intent.CreateChooser(intent, "Select an APK"), ReqPickApk);
            }
            catch (Exception ex)
            {
                Append($"ERROR: no document picker ({ex.Message})");
            }
        }

        /// <summary>Lists installed apps and extracts the one picked.</summary>
        private void ChooseInstalledApp()
        {
            RunOnBackground(() =>
            {
                var apps = ApkImport.ListInstalled(this, Append);
                if (apps.Count == 0)
                {
                    Append("no installed apps are visible; QUERY_ALL_PACKAGES is declared, so this " +
                           "would be a package-visibility surprise worth reporting");
                    return;
                }

                var labels = new string[apps.Count];
                for (var i = 0; i < apps.Count; i++) labels[i] = apps[i].ToString();

                RunOnUiThread(() =>
                {
                    new AlertDialog.Builder(this)
                        .SetTitle("Export from an installed app")
                        .SetItems(labels, (_, e) =>
                        {
                            if (e.Which >= 0 && e.Which < apps.Count) ImportInstalledApp(apps[e.Which]);
                        })
                        .Show();
                });
            });
        }

        private void ImportInstalledApp(ApkImport.InstalledApp app)
        {
            RunOnBackground(() =>
            {
                Append($"reading {app.Label} ({app.PackageName}), {app.Apks.Length} APK file(s)");
                var external = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;
                var dir = ApkImport.StageApks(this, app, external, Append);
                if (dir == null)
                {
                    SetStatus("cannot read that app's APK");
                    return;
                }
                _inputDir = dir;
                RunOnUiThread(() => _panel.InputPath.Text = dir);
                Scan();
            });
        }

        private void ImportPickedApk(Android.Net.Uri uri)
        {
            RunOnBackground(() =>
            {
                var external = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;
                var dir = ApkImport.StagePickedApk(this, uri, external, Append);
                if (dir == null)
                {
                    SetStatus("cannot read the picked APK");
                    return;
                }
                _inputDir = dir;
                RunOnUiThread(() => _panel.InputPath.Text = dir);
                Scan();
            });
        }

        private void PickTree()
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
            try
            {
                StartActivityForResult(Intent.CreateChooser(intent, "Select bundle folder"), ReqPickTree);
            }
            catch (Exception ex)
            {
                Append($"ERROR: no document picker ({ex.Message})");
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (resultCode != Result.Ok || data?.Data == null) return;

            if (requestCode == ReqPickApk)
            {
                ImportPickedApk(data.Data);
                return;
            }

            if (requestCode != ReqPickTree) return;

            var uri = data.Data;

            // If all-files access is held we can usually map the tree back to a real path and read
            // it in place, which avoids copying potentially gigabytes of bundles.
            var docId = DocumentsContract.GetTreeDocumentId(uri);
            Append($"picked: {uri} (docId='{docId}')");

            var direct = StorageAccess.TryResolveTreePath(this, uri);
            if (direct != null)
            {
                Append($"Using {direct} directly (no copy)");
                _inputDir = direct;
                _panel.InputPath.Text = direct;
                RunOnBackground(Scan); // feature 4: no separate scan step
                return;
            }

            try
            {
                ContentResolver.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
            }
            catch
            {
                // not all providers offer it; the copy below does not need it
            }

            RunOnBackground(() => ImportTree(uri));
        }

        private void ImportTree(Android.Net.Uri uri)
        {
            // A picked tree is a content:// URI, but for the file-manager provider it names a real
            // path. Resolving it puts a picked folder on the same routes as a typed-in one --
            // read in place, or staged with Shizuku when it belongs to another app -- instead of
            // always paying for a byte-by-byte copy through the provider. The copy stays as the
            // fallback for providers that name nothing on disk.
            var picked = SafPaths.ToFileSystemPath(uri);
            if (picked != null)
            {
                Append($"picked {picked}");
                var readable = ResolveReadable(picked);
                if (readable != null)
                {
                    _inputDir = readable;
                    RunOnUiThread(() => _panel.InputPath.Text = readable);
                    Scan(); // feature 4: load straight after the directory is chosen
                    return;
                }
                Append("that path is not usable, so copying through the document provider instead");
            }

            Append($"Importing {uri} ...");
            var copied = ImportUtils.CopyTree(this, uri, _inputDir, Append, (c, t) => Report(c, t));
            Append($"Imported {copied} file(s) into {_inputDir}");
            RunOnUiThread(() => _panel.InputPath.Text = _inputDir);
            Scan(); // feature 4: load straight after the directory is chosen
        }

        // ---------------- work ----------------

        private void Scan()
        {
            var files = Directory.Exists(_inputDir)
                ? Directory.GetFiles(_inputDir, "*.*", SearchOption.AllDirectories).Length
                : 0;
            if (files == 0)
            {
                SetStatus("No input files in the input folder");
                Append("No input files. Pick a folder, or set a path and press Load.");
                return;
            }

            _extractor?.Dispose();
            _extractor = new Extractor
            {
                Info = Append,
                Warn = Append,
                Progress = Report,
                ReportPath = Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath,
                                          "last-report.txt"),
            };

            // The kind decides which object types are worth building; see Extractor.FilterFor.
            _extractor.Load(_inputDir, (ExportKind)_panel.Kind.SelectedItemPosition);

            // A tree too big to hold is not read until Export asks for it, so there are no object
            // counts to show yet, only what the scan recognised.
            SetStatus(_extractor.CandidateCount == 0
                ? "No Unity files found"
                : _extractor.LoadedObjects > 0
                    ? $"{_extractor.LoadedFiles} file(s), {_extractor.LoadedObjects} object(s) loaded"
                    : $"{_extractor.CandidateCount} candidate file(s) -- will be read on export");
        }

        private void Export(int kindIndex, bool overwrite)
        {
            if (_extractor == null)
            {
                SetStatus("Scan first");
                Append("Nothing loaded yet. Pick a folder or set a path and press Load.");
                return;
            }

            // The load was filtered to one kind's types, so exporting something else has to go back
            // through the loader. The filter only ever widens (SetAssetFilter unions), so it cannot
            // be reset on a live AssetsManager -- Scan() builds a fresh one.
            if (_extractor.LoadedKind != (ExportKind)kindIndex)
            {
                Append($"export kind changed to {(ExportKind)kindIndex}, reloading");
                Scan();
                if (_extractor == null) return;
            }

            var options = new ExportOptions
            {
                Kind = (ExportKind)kindIndex,
                Overwrite = overwrite,
            };

            var baseDir = string.IsNullOrWhiteSpace(_panel.OutputPath?.Text)
                ? _outputDir
                : _panel.OutputPath.Text.Trim();
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(baseDir, $"{options.Kind}_{stamp}");
            Append($"Exporting {options.Kind} -> {dest}");

            var report = _extractor.Export(dest, options);
            Append(report.ToString());

            foreach (var e in report.Errors.Take(20)) Append("  " + e);
            SetStatus($"Exported {report.Exported}/{report.Matched} -> {Path.GetFileName(dest)}");
        }

        // ---------------- plumbing ----------------

        /// <summary>
        /// Opens the browser on the batches the scan produced. Loading is one batch at a time
        /// because the objects have to stay resident to be previewed, and the whole tree does not
        /// fit in memory -- the same reason Export batches, walked instead of hidden.
        /// </summary>
        private void OpenBrowser()
        {
            if (_extractor == null || _extractor.BatchCount == 0)
            {
                SetStatus("先 Scan 一次");
                Append("先 Scan 一次，再来浏览。");
                return;
            }

            _browser = new PreviewPanel(this, _extractor.BatchCount, RunOnBackground, RunOnUiThread);
            _browser.Closed += () =>
            {
                // The player holds a decoder on the clip that is open; leaving it running would keep
                // it alive behind the export screen.
                _browser.StopAudio();
                _browser = null;
                SetContentView(_mainRoot);
            };
            _browser.BatchRequested += LoadBatchForBrowser;

            SetContentView(Inset(_browser.Root));
            LoadBatchForBrowser(0);
        }

        /// <summary>
        /// Wraps a screen root so it is not laid out behind the status bar and toolbar.
        ///
        /// Edge-to-edge is the default at targetSdk 35, and the browser hit exactly this: its batch
        /// buttons were drawn under the title bar, so there was no way to reach any batch but the
        /// first one. BuildUi does the same for the main screen.
        /// </summary>
        private View Inset(View root)
        {
            var basePad = (int)(16 * Resources.DisplayMetrics.Density);
            root.SetPadding(basePad, basePad, basePad, basePad);

            var outer = new FrameLayout(this);
            outer.AddView(root);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                outer.SetOnApplyWindowInsetsListener(new InsetListener(root, basePad));
            }
            return outer;
        }

        private void LoadBatchForBrowser(int index)
        {
            if (_extractor == null || _browser == null) return;
            if (index < 0 || index >= _extractor.BatchCount) return;
            if (_browsing) return;

            _browsing = true;
            _browser.SetBusy($"正在加载第 {index + 1}/{_extractor.BatchCount} 批…");
            RunOnBackground(() =>
            {
                try
                {
                    var objects = _extractor.BrowseBatch(index);
                    RunOnUiThread(() => _browser?.SetBatch(index, objects));
                }
                catch (Exception ex)
                {
                    RunOnUiThread(() => _browser?.SetBusy("加载失败：" + ex.Message));
                }
                finally
                {
                    _browsing = false;
                }
            });
        }

        private void RunOnBackground(Action work)
        {
            SetBusy(true);
            new Thread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    RunOnUiThread(() => Append($"ERROR: {ex}"));
                }
                finally
                {
                    RunOnUiThread(() => SetBusy(false));
                }
            })
            { IsBackground = true, Name = "assetstudio", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        }

        private void SetBusy(bool busy) => _panel?.SetBusy(busy);

        private void Report(int cur, int total) => _panel?.Report(cur, total);

        /// <summary>
        /// Status text must be set on the UI thread: every long operation runs on a background
        /// thread, and touching a View from there throws CalledFromWrongThreadException.
        /// </summary>
        private void SetStatus(string text) => _panel?.SetStatus(text);

        private void Append(string line) => _panel?.Append(line);

        protected override void OnDestroy()
        {
            _extractor?.Dispose();
            base.OnDestroy();
        }
    }

    /// <summary>
    /// Pads a view by the system bar / display cutout insets, on top of its own base padding.
    /// </summary>
    internal sealed class InsetListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        private readonly View _target;
        private readonly int _basePad;

        public InsetListener(View target, int basePad)
        {
            _target = target;
            _basePad = basePad;
        }

        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            var bars = insets.GetInsets(
                WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            _target.SetPadding(
                _basePad + bars.Left,
                _basePad + bars.Top,
                _basePad + bars.Right,
                _basePad + bars.Bottom);
            return insets;
        }
    }

    internal sealed class AndroidLogger : AssetStudio.ILogger
    {
        /// <summary>
        /// Debug is off. The loader emits one Debug line per decompressed block, and the 4.1 GB
        /// cache is ~105k of them, all of it inside the loops it is reporting on: a string
        /// interpolation, a main-thread post and a JNI call per block. Info and up are kept.
        /// </summary>
        public static bool Verbose;

        /// <summary>
        /// Warnings are capped. Loading the 4.1 GB cache fails on 6587 Material objects, and each
        /// one logs a five-line header plus a full stack trace: ~250k lines of logcat and one JNI
        /// call each, for a message that is the same every time.
        /// </summary>
        private const int WarningLimit = 200;

        private readonly MainActivity _activity;
        private int _warnings;

        public AndroidLogger(MainActivity activity) => _activity = activity;

        public void Log(LoggerEvent loggerEvent, string message, bool ignoreLevel = false)
        {
            if (loggerEvent < LoggerEvent.Info && !Verbose) return;

            if (loggerEvent >= LoggerEvent.Warning)
            {
                var n = Interlocked.Increment(ref _warnings);
                if (n > WarningLimit)
                {
                    if (n == WarningLimit + 1) Android.Util.Log.Info("AssetStudio", "[Warning] further warnings suppressed");
                    return;
                }
            }

            // Deliberately no RunOnUiThread: android.util.Log is thread safe, and routing every
            // line through the main looper made it the bottleneck for work that runs on a
            // background thread.
            Android.Util.Log.Info("AssetStudio", $"[{loggerEvent}] {message}");
        }
    }
}
