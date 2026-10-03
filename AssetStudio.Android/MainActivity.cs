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

namespace AssetStudioMobile
{
    [Activity(Label = "@string/app_name", MainLauncher = true, Exported = true)]
    public class MainActivity : Activity
    {
        private const int ReqPickTree = 1001;

        private TextView _status;
        private TextView _log;
        private ProgressBar _bar;
        private Button _btnImport;
        private Button _btnGrant;
        private Button _btnShizuku;
        private TextView _permStatus;
        private EditText _inputPath;
        private EditText _outputPath;
        private Button _btnScan;
        private Button _btnExport;
        private Spinner _kind;
        private CheckBox _overwrite;

        private const int LogHistoryLimit = 2000;
        private const int LogVisibleLines = 14;

        private readonly List<string> _logLines = new List<string>();
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

            SetContentView(BuildUi());
            _inputPath.Text = _inputDir;
            _outputPath.Text = _outputDir;
            RefreshPermissionUi();
            Append($"input : {_inputDir}");
            Append($"output: {_outputDir}");
            Append("Import a folder of Unity bundles, then Scan, then Export.");

            RunIntentAction(Intent);
        }

        private View BuildUi()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };

            _btnShizuku = new Button(this) { Text = "Shizuku: request permission" };
            _btnShizuku.Click += (_, __) => ShizukuBridge.RequestPermission();
            root.AddView(_btnShizuku);

            _btnGrant = new Button(this) { Text = "Grant all-files access" };
            _btnGrant.Click += (_, __) => RequestAllFilesAccess();
            root.AddView(_btnGrant);

            _permStatus = new TextView(this) { TextSize = 11f };
            root.AddView(_permStatus);

            root.AddView(new TextView(this) { Text = "Bundle folder" });
            var inputRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _inputPath = new EditText(this) { Hint = "/sdcard/Download/mygame", TextSize = 12f };
            _inputPath.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            inputRow.AddView(_inputPath);
            var loadBtn = new Button(this) { Text = "Load" };
            loadBtn.Click += (_, __) => LoadFromPathField();
            inputRow.AddView(loadBtn);
            root.AddView(inputRow);

            _btnImport = new Button(this) { Text = "Pick folder (copies files)" };
            _btnImport.Click += (_, __) => PickTree();
            root.AddView(_btnImport);

            _btnScan = new Button(this) { Text = "Rescan" };
            _btnScan.Click += (_, __) => RunOnBackground(Scan);
            root.AddView(_btnScan);

            root.AddView(new TextView(this) { Text = "Export to" });
            _outputPath = new EditText(this) { TextSize = 12f };
            root.AddView(_outputPath);

            _kind = new Spinner(this);
            _kind.Adapter = ArrayAdapter.CreateFromResource(this, Resource.Array.export_kinds, Android.Resource.Layout.SimpleSpinnerDropDownItem);
            root.AddView(new TextView(this) { Text = "Export kind" });
            root.AddView(_kind);

            _overwrite = new CheckBox(this) { Text = "Overwrite existing" };
            root.AddView(_overwrite);

            _btnExport = new Button(this) { Text = "Export" };
            _btnExport.Click += (_, __) => RunOnBackground(() => Export(_kind.SelectedItemPosition, _overwrite.Checked));
            root.AddView(_btnExport);

            var selftest = new Button(this) { Text = "Run codec self-test" };
            selftest.Click += (_, __) => RunOnBackground(() => SelfTest.AppendResults(Append));
            root.AddView(selftest);

            _bar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal, 0) { Max = 100 };
            root.AddView(_bar);

            _status = new TextView(this);
            root.AddView(_status);

            _log = new TextView(this) { TextSize = 10f };
            root.AddView(_log);

            // Everything lives in ONE ScrollView. A nested ScrollView for the log plus
            // FullScroll() auto-scrolling is what pushed the top buttons off-screen:
            // ScrollView.FullScroll delegates to the parent first, so scrolling the log to the
            // bottom scrolled the whole page and hid "Import" and "Scan". The log is instead
            // capped at the most recent lines (see Append), which keeps the page short enough
            // that the buttons stay visible without scrolling.
            var outer = new ScrollView(this) { FillViewport = true };
            outer.AddView(root);

            // targetSdk >= 35 on Android 15/16 is edge-to-edge by default, so the content view is
            // laid out behind the status bar and toolbar unless the insets are applied. Without
            // this the first row of buttons sits underneath the title bar.
            var basePad = (int)(16 * Resources.DisplayMetrics.Density);
            root.SetPadding(basePad, basePad, basePad, basePad);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                outer.SetOnApplyWindowInsetsListener(new InsetListener(root, basePad));
            }

            return outer;
        }

        /// <summary>
        /// Allows driving the app from `adb shell am start` without tapping, e.g. for automated
        /// smoke tests:
        ///
        ///   adb shell am start -n com.aelurum.assetstudiomod/crc6457c8bc28ddf7d589.MainActivity \
        ///       -e action selftest
        ///   adb shell am start -n ... -e action scan
        ///   adb shell am start -n ... -e action export --es kind Texture2D
        /// </summary>
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
                case "load":
                {
                    // `adb shell input text` cannot reliably type '/' through a CJK IME, so the
                    // path comes in as an extra instead.
                    var p = intent.GetStringExtra("path");
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        _inputPath.Text = p;
                        LoadFromPathField();
                    }
                    else
                    {
                        Append("ERROR: -e path is required for action=load");
                    }
                    break;
                }
                case "export":
                {
                    // Optional: point the run at a directory without touching the UI field.
                    var exportPath = intent.GetStringExtra("path");
                    if (!string.IsNullOrWhiteSpace(exportPath))
                    {
                        var problem = StorageAccess.ValidateReadableDirectory(exportPath);
                        if (problem != null)
                        {
                            Append($"ERROR: {exportPath}: {problem}");
                            break;
                        }
                        _inputDir = Path.GetFullPath(exportPath.Trim());
                        _inputPath.Text = _inputDir;
                    }

                    var kindName = intent.GetStringExtra("kind");
                    var idx = 0;
                    if (!string.IsNullOrEmpty(kindName))
                    {
                        idx = Math.Max(0, Array.IndexOf(Enum.GetNames(typeof(ExportKind)), kindName));
                    }
                    if (intent.GetBooleanExtra("overwrite", false)) _overwrite.Checked = true;

                    // Scan() reads the kind off the spinner to decide which object types to build,
                    // so the spinner has to agree with what was asked for here.
                    _kind.SetSelection(idx);

                    RunOnBackground(() =>
                    {
                        // Each `am start` is a fresh process, so an export launched this way has
                        // no loaded assets unless we scan first.
                        if (_extractor == null) Scan();
                        Export(idx, _overwrite.Checked);
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
            if (_permStatus == null || _btnGrant == null) return;
            var granted = StorageAccess.HasAllFilesAccess();
            _btnGrant.Visibility = granted ? ViewStates.Gone : ViewStates.Visible;

            var shizuku = ShizukuBridge.State;
            _btnShizuku.Visibility = shizuku == ShizukuState.Ready ? ViewStates.Gone : ViewStates.Visible;
            _permStatus.Text = (granted
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
            var typed = _inputPath.Text;
            if (string.IsNullOrWhiteSpace(typed))
            {
                Append("ERROR: no path given");
                return;
            }

            RunOnBackground(() =>
            {
                var full = typed.Trim();
                try { full = Path.GetFullPath(full); } catch { /* keep the raw text for the error */ }

                var problem = StorageAccess.ValidateReadableDirectory(full);
                if (problem == null)
                {
                    _inputDir = full;
                    RunOnUiThread(() => _inputPath.Text = full);
                    Scan();
                    return;
                }

                Append($"{full}: {problem}");
                Append("all-files access does not cover /sdcard/Android/data; trying Shizuku...");

                if (ShizukuBridge.State != ShizukuState.Ready)
                {
                    Append($"ERROR: cannot read it, and Shizuku is not usable ({ShizukuBridge.State}). " +
                           "Copy the game's files to /sdcard/Download first.");
                    SetStatus("cannot read, and Shizuku unavailable");
                    return;
                }

                // Stage into our own EXTERNAL app dir, not FilesDir: the Shizuku service runs as
                // uid 2000 and cannot write into /data/user/0/<pkg>, which is the app's private
                // sandbox. The external dir is writable by shell (ext_data_rw) and by us.
                var externalRoot = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;
                var staging = Path.Combine(externalRoot, "staged");
                var staged = ShizukuBridge.StageDirectory(full, staging, Append, (c, t) => Report(c, t));
                _inputDir = staged;
                RunOnUiThread(() => _inputPath.Text = staged);
                Scan();
            });
        }

        // ---------------- SAF import ----------------

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
            if (requestCode != ReqPickTree || resultCode != Result.Ok || data?.Data == null) return;

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
                _inputPath.Text = direct;
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
            Append($"Importing {uri} ...");
            var copied = ImportUtils.CopyTree(this, uri, _inputDir, Append, (c, t) => Report(c, t));
            Append($"Imported {copied} file(s) into {_inputDir}");
            _inputPath.Text = _inputDir;
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
            };

            // The kind decides which object types are worth building; see Extractor.FilterFor.
            _extractor.Load(_inputDir, (ExportKind)_kind.SelectedItemPosition);

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

            var baseDir = string.IsNullOrWhiteSpace(_outputPath?.Text)
                ? _outputDir
                : _outputPath.Text.Trim();
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(baseDir, $"{options.Kind}_{stamp}");
            Append($"Exporting {options.Kind} -> {dest}");

            var report = _extractor.Export(dest, options);
            Append(report.ToString());

            foreach (var e in report.Errors.Take(20)) Append("  " + e);
            SetStatus($"Exported {report.Exported}/{report.Matched} -> {Path.GetFileName(dest)}");
        }

        // ---------------- plumbing ----------------

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

        private void SetBusy(bool busy)
        {
            RunOnUiThread(() =>
            {
                _btnImport.Enabled = !busy;
                _btnScan.Enabled = !busy;
                _btnExport.Enabled = !busy;
                if (busy) SetStatus("Working...");
            });
        }

        private void Report(int cur, int total)
        {
            var pct = total <= 0 ? 0 : (int)(100L * cur / total);
            RunOnUiThread(() =>
            {
                _bar.Progress = pct;
                if (total > 0) SetStatus($"{cur}/{total} ({pct}%)");
            });
        }

        /// <summary>
        /// Status text must be set on the UI thread: every long operation runs on a background
        /// thread, and touching a View from there throws CalledFromWrongThreadException.
        /// </summary>
        private void SetStatus(string text) => RunOnUiThread(() => _status.Text = text);

        private void Append(string line)
        {
            lock (_logLines)
            {
                _logLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
                if (_logLines.Count > LogHistoryLimit) _logLines.RemoveRange(0, _logLines.Count - LogHistoryLimit);

                // Only the tail is shown. Auto-scrolling instead would scroll the page (see
                // BuildUi) and hide the buttons.
                var start = Math.Max(0, _logLines.Count - LogVisibleLines);
                var text = start > 0 ? "...\n" + string.Join("\n", _logLines.GetRange(start, _logLines.Count - start)) : string.Join("\n", _logLines);
                RunOnUiThread(() => _log.Text = text);
            }
        }

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
