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
            _outputDir = Path.Combine(external ?? FilesDir.AbsolutePath, "out");
            Directory.CreateDirectory(_inputDir);

            Logger.Default = new AndroidLogger(this);

            SetContentView(BuildUi());
            Append($"input : {_inputDir}");
            Append($"output: {_outputDir}");
            Append("Import a folder of Unity bundles, then Scan, then Export.");

            RunIntentAction(Intent);
        }

        private View BuildUi()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };

            _btnImport = new Button(this) { Text = "Import bundle folder..." };
            _btnImport.Click += (_, __) => PickTree();
            root.AddView(_btnImport);

            _btnScan = new Button(this) { Text = "Scan / load" };
            _btnScan.Click += (_, __) => RunOnBackground(Scan);
            root.AddView(_btnScan);

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

            switch (action.ToLowerInvariant())
            {
                case "selftest":
                    RunOnBackground(() => SelfTest.AppendResults(Append));
                    break;
                case "scan":
                    RunOnBackground(Scan);
                    break;
                case "export":
                    var kindName = intent.GetStringExtra("kind");
                    var idx = 0;
                    if (!string.IsNullOrEmpty(kindName))
                    {
                        idx = Math.Max(0, Array.IndexOf(Enum.GetNames(typeof(ExportKind)), kindName));
                    }
                    if (intent.GetBooleanExtra("overwrite", false)) _overwrite.Checked = true;
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
            SetStatus($"{copied} file(s) imported");
        }

        // ---------------- work ----------------

        private void Scan()
        {
            var files = Directory.Exists(_inputDir)
                ? Directory.GetFiles(_inputDir, "*.*", SearchOption.AllDirectories).Length
                : 0;
            if (files == 0)
            {
                SetStatus("No input files. Import a bundle folder first.");
                return;
            }

            _extractor?.Dispose();
            _extractor = new Extractor
            {
                Info = Append,
                Warn = Append,
                Progress = Report,
            };

            _extractor.Load(_inputDir);
            SetStatus($"{_extractor.Files.Count()} file(s), {_extractor.CountAssets()} object(s) loaded");
        }

        private void Export(int kindIndex, bool overwrite)
        {
            if (_extractor == null)
            {
                SetStatus("Scan first.");
                return;
            }

            var options = new ExportOptions
            {
                Kind = (ExportKind)kindIndex,
                Overwrite = overwrite,
            };

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(_outputDir, $"{options.Kind}_{stamp}");
            Append($"Exporting {options.Kind} -> {dest}");

            var report = _extractor.Export(dest, options);
            Append($"matched={report.Matched} exported={report.Exported} " +
                   $"skipped={report.Skipped} failed={report.Failed}");

            foreach (var e in report.Errors.Take(20)) Append("  " + e);
            SetStatus($"Exported {report.Exported} -> {dest}");
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
        private readonly MainActivity _activity;
        public AndroidLogger(MainActivity activity) => _activity = activity;

        public void Log(LoggerEvent loggerEvent, string message, bool ignoreLevel = false)
            => _activity.RunOnUiThread(() => Android.Util.Log.Info("AssetStudio", $"[{loggerEvent}] {message}"));
    }
}
