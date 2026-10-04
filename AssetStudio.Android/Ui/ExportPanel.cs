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
        private readonly Button _import;
        private readonly Button _scan;
        private readonly Button _shizuku;
        private readonly Button _grant;

        /// <summary>The scrollable screen, ready to be set as the content view.</summary>
        public View Root { get; }

        public EditText InputPath { get; }
        public EditText OutputPath { get; }
        public TextView PermissionStatus { get; }

        private readonly TextView _scriptLabel;

        /// <summary>Which Lua decryption script is loaded, or that none is.</summary>
        public string ScriptName
        {
            set => _scriptLabel.Text = string.IsNullOrEmpty(value) ? "未加载解密脚本" : value;
        }

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
        public event Action SelfTestRequested;
        public event Action BrowseRequested;
        public event Action PickScriptRequested;
        public event Action ClearScriptRequested;

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

            // Encrypted bundles are a per-game scheme, so the tool takes the scheme as a script
            // rather than pretending to know it.
            var scriptRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            var pickScript = new Button(activity) { Text = "解密脚本" };
            pickScript.Click += (_, _) => PickScriptRequested?.Invoke();
            scriptRow.AddView(pickScript);
            var clearScript = new Button(activity) { Text = "清除" };
            clearScript.Click += (_, _) => ClearScriptRequested?.Invoke();
            scriptRow.AddView(clearScript);
            root.AddView(scriptRow);

            _scriptLabel = new TextView(activity) { TextSize = 11f, Text = "未加载解密脚本" };
            root.AddView(_scriptLabel);

            root.AddView(new TextView(activity) { Text = "Export to" });
            OutputPath = new EditText(activity) { TextSize = 12f };
            root.AddView(OutputPath);

            var selftest = new Button(activity) { Text = "Run codec self-test" };
            selftest.Click += (_, _) => SelfTestRequested?.Invoke();
            root.AddView(selftest);

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

        /// <summary>
        /// Progress goes to the status line. The bar that used to be here moved with the export:
        /// this screen had one nobody watched, because every export now runs from the browser.
        /// </summary>
        public void Report(int current, int total)
        {
            if (total <= 0) return;
            var percent = (int)(100L * current / total);
            _activity.RunOnUiThread(() => _status.Text = $"{current}/{total} ({percent}%)");
        }

        public void SetBusy(bool busy)
        {
            _activity.RunOnUiThread(() =>
            {
                _import.Enabled = !busy;
                _scan.Enabled = !busy;
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
