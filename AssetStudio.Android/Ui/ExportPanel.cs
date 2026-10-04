using System;
using System.Collections.Generic;
using Android.App;
using Android.Views;
using Android.Widget;
using AssetStudio;

namespace AssetStudioMobile.Ui
{
    /// <summary>
    /// The export screen: folder input, the ways to get files into it, the export controls, the
    /// progress bar and the log.
    ///
    /// It owns its views and raises events; MainActivity keeps the behaviour -- permissions, the
    /// extractor, intents -- and subscribes. Before this, all of it was built inline in a nine
    /// hundred line activity, and every control was a field on the activity that the behaviour
    /// reached into.
    /// </summary>
    internal sealed class ExportPanel
    {
        private const int LogHistoryLimit = 2000;
        private const int LogVisibleLines = 14;

        private readonly Activity _activity;
        private readonly List<string> _logLines = new List<string>();
        private readonly TextView _log;
        private readonly TextView _status;
        private readonly ProgressBar _bar;
        private readonly Button _import;
        private readonly Button _scan;
        private readonly Button _export;
        private readonly Button _shizuku;
        private readonly Button _grant;

        /// <summary>The scrollable screen, ready to be set as the content view.</summary>
        public View Root { get; }

        public EditText InputPath { get; }
        public EditText OutputPath { get; }
        public Spinner Kind { get; }
        public CheckBox Overwrite { get; }
        public TextView PermissionStatus { get; }

        /// <summary>
        /// The permission buttons are hidden once they are satisfied. Exposed as visibility rather
        /// than as the buttons themselves so the owner does not reach into the panel's views.
        /// </summary>
        public ViewStates GrantVisibility
        {
            set => _grant.Visibility = value;
        }

        public ViewStates ShizukuVisibility
        {
            set => _shizuku.Visibility = value;
        }

        public event Action ShizukuRequested;
        public event Action GrantRequested;
        public event Action LoadRequested;
        public event Action PickFolderRequested;
        public event Action PickApkRequested;
        public event Action ImportFromAppRequested;
        public event Action ScanRequested;
        public event Action ExportRequested;
        public event Action SelfTestRequested;
        public event Action BrowseRequested;

        public ExportPanel(Activity activity)
        {
            _activity = activity;
            var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };

            _shizuku = new Button(activity) { Text = "Shizuku: request permission" };
            _shizuku.Click += (_, _) => ShizukuRequested?.Invoke();
            root.AddView(_shizuku);

            _grant = new Button(activity) { Text = "Grant all-files access" };
            _grant.Click += (_, _) => GrantRequested?.Invoke();
            root.AddView(_grant);

            PermissionStatus = new TextView(activity) { TextSize = 11f };
            root.AddView(PermissionStatus);

            root.AddView(new TextView(activity) { Text = "Bundle folder" });
            var inputRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            InputPath = new EditText(activity) { Hint = "/sdcard/Download/mygame", TextSize = 12f };
            InputPath.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            inputRow.AddView(InputPath);
            var load = new Button(activity) { Text = "Load" };
            load.Click += (_, _) => LoadRequested?.Invoke();
            inputRow.AddView(load);
            root.AddView(inputRow);

            _import = new Button(activity) { Text = "Pick folder" };
            _import.Click += (_, _) => PickFolderRequested?.Invoke();
            root.AddView(_import);

            // An APK is a ZIP, and the loader already handles ZIPs, so both of these end the same
            // way as picking a folder: stage the bytes somewhere readable, then Scan.
            var apkRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            var pickApk = new Button(activity) { Text = "Pick .apk" };
            pickApk.Click += (_, _) => PickApkRequested?.Invoke();
            apkRow.AddView(pickApk);
            var fromApp = new Button(activity) { Text = "Import from app" };
            fromApp.Click += (_, _) => ImportFromAppRequested?.Invoke();
            apkRow.AddView(fromApp);
            root.AddView(apkRow);

            _scan = new Button(activity) { Text = "Rescan" };
            _scan.Click += (_, _) => ScanRequested?.Invoke();
            root.AddView(_scan);

            var browse = new Button(activity) { Text = "浏览 / 预览" };
            browse.Click += (_, _) => BrowseRequested?.Invoke();
            root.AddView(browse);

            root.AddView(new TextView(activity) { Text = "Export to" });
            OutputPath = new EditText(activity) { TextSize = 12f };
            root.AddView(OutputPath);

            // Built from the enum rather than the export_kinds string array. The two drifted apart
            // when TextureRaw was added: the array still had 7 entries, the intent selected index 7,
            // and the Spinner's ArrayAdapter.getItem threw ArrayIndexOutOfBoundsException while
            // laying out -- a crash on the UI thread, not a mislabelled row.
            Kind = new Spinner(activity);
            Kind.Adapter = new ArrayAdapter<string>(activity, Android.Resource.Layout.SimpleSpinnerDropDownItem,
                                                    Enum.GetNames(typeof(ExportKind)));
            root.AddView(new TextView(activity) { Text = "Export kind" });
            root.AddView(Kind);

            Overwrite = new CheckBox(activity) { Text = "Overwrite existing" };
            root.AddView(Overwrite);

            _export = new Button(activity) { Text = "Export" };
            _export.Click += (_, _) => ExportRequested?.Invoke();
            root.AddView(_export);

            var selftest = new Button(activity) { Text = "Run codec self-test" };
            selftest.Click += (_, _) => SelfTestRequested?.Invoke();
            root.AddView(selftest);

            _bar = new ProgressBar(activity, null, Android.Resource.Attribute.ProgressBarStyleHorizontal, 0) { Max = 100 };
            root.AddView(_bar);

            _status = new TextView(activity);
            root.AddView(_status);

            _log = new TextView(activity) { TextSize = 10f };
            root.AddView(_log);

            // Everything lives in ONE ScrollView. A nested ScrollView for the log plus
            // FullScroll() auto-scrolling is what pushed the top buttons off-screen:
            // ScrollView.FullScroll delegates to the parent first, so scrolling the log to the
            // bottom scrolled the whole page and hid "Import" and "Scan". The log is instead
            // capped at the most recent lines (see Append), which keeps the page short enough
            // that the buttons stay visible without scrolling.
            var outer = new ScrollView(activity) { FillViewport = true };
            outer.AddView(root);
            Root = outer;
        }

        /// <summary>
        /// Status text must be set on the UI thread: every long operation runs on a background
        /// thread, and touching a View from there throws CalledFromWrongThreadException.
        /// </summary>
        public void SetStatus(string text) => _activity.RunOnUiThread(() => _status.Text = text);

        public void Report(int current, int total)
        {
            var percent = total <= 0 ? 0 : (int)(100L * current / total);
            _activity.RunOnUiThread(() =>
            {
                _bar.Progress = percent;
                if (total > 0) _status.Text = $"{current}/{total} ({percent}%)";
            });
        }

        public void SetBusy(bool busy)
        {
            _activity.RunOnUiThread(() =>
            {
                _import.Enabled = !busy;
                _scan.Enabled = !busy;
                _export.Enabled = !busy;
                if (busy) _status.Text = "Working...";
            });
        }

        public void Append(string line)
        {
            lock (_logLines)
            {
                _logLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
                if (_logLines.Count > LogHistoryLimit)
                    _logLines.RemoveRange(0, _logLines.Count - LogHistoryLimit);

                // Only the tail is shown. Auto-scrolling instead would scroll the page (see the
                // constructor) and hide the buttons.
                var start = Math.Max(0, _logLines.Count - LogVisibleLines);
                var text = start > 0
                    ? "...\n" + string.Join("\n", _logLines.GetRange(start, _logLines.Count - start))
                    : string.Join("\n", _logLines);
                _activity.RunOnUiThread(() => _log.Text = text);
            }
        }
    }
}
