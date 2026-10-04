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
        private const int ReqPickFile = 1002;
        private const int ReqPickScript = 1003;

        /// <summary>The export screen. It owns its views; this class owns the behaviour.</summary>
        private ExportPanel _panel;

        /// <summary>Kept so the browser can hand the screen back when it closes.</summary>
        private View _mainRoot;

        private PreviewPanel _browser;

        /// <summary>One batch load at a time: BrowseBatch reuses the extractor's manager.</summary>
        private bool _browsing;

        /// <summary>And one index at a time, for the same reason.</summary>
        private bool _indexing;

        /// <summary>
        /// The categories in play, empty meaning everything.
        ///
        /// One set, not two: the browser's filter writes it and the export reads it, so what was
        /// filtered and what gets written cannot drift apart -- which is what happened when the
        /// export and the browser each kept their own list.
        /// </summary>
        private readonly HashSet<ExportKind> _categories = new HashSet<ExportKind>();

        /// <summary>How to write each object. Auto unless asked otherwise.</summary>
        private ExportKind _mode = ExportKind.Auto;

        private Extractor _extractor;

        /// <summary>Kept so a rescan keeps using the script the user picked.</summary>
        private Scripting.LuaDecryptor _decryptor;
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
            Append($"输入：{_inputDir}");
            Append($"输出：{_outputDir}");
            Append("先选资源文件夹（或导入应用 / 选择文件），再「重新扫描」。");

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
            _panel.PickApkRequested += PickResourceFile;
            _panel.ImportFromAppRequested += ChooseInstalledApp;
            _panel.ScanRequested += () => RunOnBackground(Scan);

            _panel.SelfTestRequested += () => RunOnBackground(() => SelfTest.AppendResults(Append));
            _panel.BrowseRequested += OpenBrowser;
            _panel.ManageFilesRequested += OpenFiles;
            _panel.PickScriptRequested += PickScript;
            _panel.ClearScriptRequested += ClearScript;

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

            // Optional: a Lua decryption script to run over the bytes before the loader reads them.
            // The picker is the normal way in; this is so a run can be driven from adb, and so a
            // script can be tried without going through the file chooser every time.
            var scriptPath = intent.GetStringExtra("script");
            if (!string.IsNullOrWhiteSpace(scriptPath)) LoadScriptFromPath(scriptPath);

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
                        Append("错误：action=apk 需要 -e package");
                        break;
                    }
                    RunOnBackground(() =>
                    {
                        var apps = ApkImport.ListInstalled(this, Append);
                        var app = apps.Find(a => a.PackageName == package);
                        if (app == null)
                        {
                            Append($"错误：{package} 不在可见的软件列表里");
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
                        Append("错误：action=load 需要 -e path");
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

                    // `-e kind Texture` still means what it always meant. A category name selects
                    // that category; a mode name (Auto, JsonDump, RawData, TextureRaw) chooses how
                    // to write everything.
                    var kindName = intent.GetStringExtra("kind");
                    if (!string.IsNullOrEmpty(kindName) && Enum.TryParse<ExportKind>(kindName, true, out var parsed))
                    {
                        _categories.Clear();
                        if (Array.IndexOf(Extractor.Categories, parsed) >= 0) _categories.Add(parsed);
                        else _mode = parsed;
                    }

                    var categoryList = intent.GetStringExtra("categories");
                    if (!string.IsNullOrEmpty(categoryList))
                    {
                        _categories.Clear();
                        foreach (var name in categoryList.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (Enum.TryParse<ExportKind>(name.Trim(), true, out var one)
                                && Array.IndexOf(Extractor.Categories, one) >= 0) _categories.Add(one);
                        }
                    }

                    // `-e overwrite` is accepted and ignored: every export goes to a new timestamped
                    // directory, so there has never been anything to overwrite in practice.
                    _ = intent.GetBooleanExtra("overwrite", false);

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
                        Export(true);
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
                    ? "所有文件访问权限：已授予（可读写任意路径）"
                    : "所有文件访问权限：未授予。仍可用 SAF，但会把文件复制一份。") +
                "\n" + ShizukuBridge.Describe() +
                (shizuku == ShizukuState.Ready
                    ? "\n访问 /sdcard/Android/data 时需要它"
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
                Append($"错误：打不开「所有文件访问」设置（{ex.Message}）");
            }
        }

        /// <summary>Loads the directory typed into the path field, then scans it (feature 4).</summary>
        private void LoadFromPathField()
        {
            var typed = _panel.InputPath.Text;
            if (string.IsNullOrWhiteSpace(typed))
            {
                Append("错误：没有填路径");
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
                Append("错误：没有填路径");
                return null;
            }
            try { full = Path.GetFullPath(full); } catch { /* keep the raw text for the error */ }

            var problem = StorageAccess.ValidateReadableDirectory(full);
            if (problem == null) return full;

            Append($"{full}: {problem}");
            Append("「所有文件访问」覆盖不到 /sdcard/Android/data，改用 Shizuku…");

            if (ShizukuBridge.State != ShizukuState.Ready)
            {
                Append($"错误：读不到它，而且 Shizuku 不可用（{ShizukuBridge.State}）。" +
                       "先把游戏文件复制到 /sdcard/Download。");
                SetStatus("读不到，且 Shizuku 不可用");
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
                    Append($"ASTC 导出失败：{ex.Message}");
                }
            };
            Append("ASTC 采样已武装，写入 " + dir);
        }

        private void PickScript()
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            // A .lua file has no reliable MIME type, so this cannot filter by one.
            intent.SetType("*/*");
            intent.AddCategory(Intent.CategoryOpenable);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            try
            {
                StartActivityForResult(Intent.CreateChooser(intent, "选择 Lua 解密脚本"), ReqPickScript);
            }
            catch (Exception ex)
            {
                Append($"ERROR: 没有可用的文件选择器（{ex.Message}）");
            }
        }

        /// <summary>
        /// Copies the picked script somewhere readable and compiles it, so a script that does not
        /// load is reported now rather than in the middle of a scan.
        /// </summary>
        private void ImportScript(Android.Net.Uri uri)
        {
            try
            {
                var name = ImportUtils.LeafNameOf(uri);
                if (string.IsNullOrEmpty(name)) name = "script.lua";

                var dir = Path.Combine(FilesRoot(), "scripts");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);

                using (var input = ContentResolver.OpenInputStream(uri))
                using (var output = File.Create(path))
                {
                    input.CopyTo(output);
                }

                LoadScriptFromPath(path);
            }
            catch (Exception ex)
            {
                Append($"读取解密脚本失败：{ex.Message}");
            }
        }

        private void LoadScriptFromPath(string path)
        {
            var decryptor = Scripting.LuaDecryptor.Load(path, out var error);
            if (decryptor == null)
            {
                _decryptor = null;
                _panel.ScriptName = null;
                Append($"解密脚本 {Path.GetFileName(path)} 加载失败：{error}");
                return;
            }

            _decryptor = decryptor;
            _panel.ScriptName = Path.GetFileName(path);
            if (_extractor != null) ApplyDecryptor(_extractor);
            Append($"lua: 已加载 {Path.GetFileName(path)}");
        }

        private void ClearScript()
        {
            _decryptor = null;
            _panel.ScriptName = null;
            if (_extractor != null) ApplyDecryptor(_extractor);
            Append("lua: 已清除解密脚本");
        }

        private void ApplyDecryptor(Extractor extractor)
        {
            extractor.Decryptor = _decryptor;
            extractor.DecryptStagingRoot = Path.Combine(FilesRoot(), "decrypted");
        }

        private string FilesRoot() => GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;

        private void PickResourceFile()
        {
            var intent = new Intent(Intent.ActionOpenDocument);

            // Anything, not just an APK: a bundle, a .assets file, a zip. An APK is only one place
            // resources live, and the loader identifies files by their contents anyway.
            intent.SetType("*/*");
            intent.AddCategory(Intent.CategoryOpenable);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            try
            {
                StartActivityForResult(Intent.CreateChooser(intent, "选择资源文件"), ReqPickFile);
            }
            catch (Exception ex)
            {
                Append($"错误：没有可用的文件选择器（{ex.Message}）");
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
                        .SetTitle("从已安装应用导出")
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

        private void ImportPickedFile(Android.Net.Uri uri)
        {
            RunOnBackground(() =>
            {
                var external = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;
                var dir = ApkImport.StagePickedFile(this, uri, external, Append);
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
                StartActivityForResult(Intent.CreateChooser(intent, "选择资源文件夹"), ReqPickTree);
            }
            catch (Exception ex)
            {
                Append($"错误：没有可用的文件选择器（{ex.Message}）");
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (resultCode != Result.Ok || data?.Data == null) return;

            if (requestCode == ReqPickScript)
            {
                ImportScript(data.Data);
                return;
            }

            if (requestCode == ReqPickFile)
            {
                ImportPickedFile(data.Data);
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
                Append($"直接使用 {direct}（不复制）");
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

            Append($"正在导入 {uri} …");
            var copied = ImportUtils.CopyTree(this, uri, _inputDir, Append, (c, t) => Report(c, t));
            Append($"已导入 {copied} 个文件到 {_inputDir}");
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
                SetStatus("输入文件夹里没有文件");
                Append("没有输入文件。选一个文件夹，或填好路径后按「加载」。");
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

            // The categories decide which object types are worth building; see Extractor.FiltersFor.
            ApplyDecryptor(_extractor);
            _extractor.Load(_inputDir, _categories);

            // A tree too big to hold is not read until Export asks for it, so there are no object
            // counts to show yet, only what the scan recognised.
            SetStatus(_extractor.CandidateCount == 0
                ? "没有找到 Unity 资源文件"
                : _extractor.LoadedObjects > 0
                    ? $"{_extractor.LoadedFiles} file(s), {_extractor.LoadedObjects} object(s) loaded"
                    : $"{_extractor.CandidateCount} candidate file(s) -- will be read on export");
        }

        private void Export(bool overwrite)
        {
            if (_extractor == null)
            {
                SetStatus("请先「重新扫描」");
                Append("还没有加载内容。先选文件夹或填好路径后按「加载」。");
                return;
            }

            // The load was narrowed to whatever categories were selected when it ran, so exporting a
            // different set has to go back through the loader -- the manager's filter cannot be
            // widened once set.
            if (!SameCategories(_extractor.LoadedCategories, _categories))
            {
                Append($"类别改为 {SelectionTag()}，重新加载");
                Scan();
                if (_extractor == null) return;
            }

            var options = new ExportOptions
            {
                Mode = _mode,
                Categories = _categories.ToArray(),
                Overwrite = overwrite,
            };

            var dest = OutputDirectory();
            Append($"正在导出 {SelectionTag()} → {dest}");

            var report = _extractor.Export(dest, options);
            Append(report.ToString());
            foreach (var e in report.Errors.Take(20)) Append("  " + e);
            RunOnUiThread(() => _browser?.ClearProgress());
            SetStatus($"已导出 {report.Exported}/{report.Matched} → {Path.GetFileName(dest)}");
        }

        /// <summary>Writes the objects the browser's index selected, loading each file once.</summary>
        private void ExportFiltered(IReadOnlyList<Extractor.IndexEntry> entries, bool overwrite)
        {
            if (_extractor == null || entries == null || entries.Count == 0) return;

            var options = new ExportOptions { Mode = _mode, Overwrite = overwrite };
            var dest = OutputDirectory();

            Append($"正在导出筛选出的 {entries.Count} 个对象 → {dest}");
            var report = _extractor.ExportIndexed(entries, dest, options);
            Append(report.ToString());
            foreach (var e in report.Errors.Take(20)) Append("  " + e);
            RunOnUiThread(() => _browser?.ClearProgress());
            SetStatus($"已导出 {report.Exported}/{report.Matched} → {Path.GetFileName(dest)}");
        }

        private string OutputDirectory()
        {
            var baseDir = string.IsNullOrWhiteSpace(_panel.OutputPath?.Text)
                ? _outputDir
                : _panel.OutputPath.Text.Trim();
            return Path.Combine(baseDir, $"{SelectionTag()}_{DateTime.Now:yyyyMMdd_HHmmss}");
        }

        /// <summary>A short, file-name-safe tag for what is selected, for the output directory.</summary>
        private string SelectionTag()
        {
            if (_categories.Count == 0) return _mode.ToString();
            if (_categories.Count == Extractor.Categories.Length) return "All";

            var names = Extractor.Categories.Where(_categories.Contains).Select(k => k.ToString());
            return $"{_mode}_{string.Join("-", names)}";
        }

        private static bool SameCategories(IReadOnlyCollection<ExportKind> loaded, ICollection<ExportKind> selected)
        {
            var left = loaded ?? Array.Empty<ExportKind>();
            return left.Count == selected.Count && left.All(selected.Contains);
        }

        /// <summary>
        /// Opens the file manager at the app's own files directory -- the one holding staged
        /// bundles, staged APKs and decrypted copies, and the one nothing else on the device can
        /// clean up.
        /// </summary>
        private void OpenFiles()
        {
            var root = FilesRoot();
            Directory.CreateDirectory(root);

            var panel = new FilesPanel(this, root, RunOnBackground, RunOnUiThread);
            panel.Closed += () => SetContentView(_mainRoot);
            SetContentView(Inset(panel.Root));
        }

        /// <summary>
        /// Opens the browser on the batches the scan produced. Loading is one batch at a time
        /// because the objects have to stay resident to be previewed, and the whole tree does not
        /// fit in memory -- the same reason Export batches, walked instead of hidden.
        /// </summary>
        private void OpenBrowser()
        {
            if (_extractor == null || _extractor.BatchCount == 0)
            {
                SetStatus("请先「重新扫描」一次");
                Append("请先「重新扫描」一次，再来浏览。");
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
            _browser.ExportRequested += target => RunOnBackground(() => ExportSingle(target));
            _browser.IndexRequested += kinds => RunOnBackground(() => BuildIndex(kinds));
            _browser.IndexedRequested += entry => RunOnBackground(() => ShowIndexed(entry));
            _browser.ExportAllRequested += () => RunOnBackground(() => Export(true));
            _browser.ExportFilteredRequested += entries =>
                RunOnBackground(() => ExportFiltered(entries, true));
            _browser.SelectionChanged += kinds =>
            {
                _categories.Clear();
                foreach (var kind in kinds) _categories.Add(kind);
            };

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

        /// <summary>
        /// Writes the one object the browser is showing, through the same plan and the same writers
        /// the full export uses, so the file is the one a full export would have produced.
        /// </summary>
        private void ExportSingle(AssetStudio.Object target)
        {
            if (_extractor == null || target == null) return;

            var options = new ExportOptions
            {
                Mode = _mode,

                // The directory is new each time, so there is nothing to protect by skipping.
                Overwrite = true,
            };

            var baseDir = string.IsNullOrWhiteSpace(_panel.OutputPath?.Text)
                ? _outputDir
                : _panel.OutputPath.Text.Trim();
            var dest = Path.Combine(baseDir, $"{options.Mode}_{DateTime.Now:yyyyMMdd_HHmmss}");

            var name = (target as NamedObject)?.m_Name;
            if (string.IsNullOrEmpty(name)) name = $"pathID {target.m_PathID}";

            try
            {
                var report = _extractor.ExportOne(target, dest, options);
                Append($"{target.type} \"{name}\" -> {dest} ({report.Exported} file(s), {report.Failed} failed)");
                RunOnUiThread(() => _browser?.SetStatus($"已导出 {report.Exported} 个文件到 {dest}"));
            }
            catch (Exception ex)
            {
                Append($"导出 \"{name}\" 失败：{ex.Message}");
                RunOnUiThread(() => _browser?.SetStatus("导出失败：" + ex.Message));
            }
        }

        /// <summary>
        /// Reads the whole tree once and keeps only what is needed to list what matched.
        ///
        /// This is what makes a filter mean the tree instead of the batch in front of you. It is a
        /// full read of every bundle, so it is an explicit action with progress, and it stops if the
        /// browser is closed -- there is no point finishing an index nobody will look at.
        /// </summary>
        private void BuildIndex(IReadOnlyCollection<ExportKind> kinds)
        {
            if (_extractor == null || _browser == null) return;
            if (_indexing) return;

            _indexing = true;
            try
            {
                Append($"正在索引 {string.Join("+", kinds)} …");
                var entries = _extractor.BuildIndex(
                    kinds,
                    (done, total) => _browser?.SetStatus($"索引中 {done}/{total} 批…"),
                    () => _browser == null);

                Append($"索引完成：{entries.Count} 个对象");
                RunOnUiThread(() => _browser?.SetIndex(entries));
            }
            catch (Exception ex)
            {
                Append($"索引失败：{ex.Message}");
                RunOnUiThread(() => _browser?.SetStatus("索引失败：" + ex.Message));
            }
            finally
            {
                _indexing = false;
            }
        }

        private void ShowIndexed(Extractor.IndexEntry entry)
        {
            if (_extractor == null || _browser == null) return;

            AssetStudio.Object loaded = null;
            try
            {
                loaded = _extractor.LoadIndexed(entry);
            }
            catch (Exception ex)
            {
                Append($"加载条目失败：{ex.Message}");
            }

            RunOnUiThread(() => _browser?.ShowIndexed(entry, loaded));
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

        private void Report(int cur, int total)
        {
            // Both screens: a scan reports while the export screen is up, an export reports while the
            // browser is, and only one of them is ever the one being looked at.
            _panel?.Report(cur, total);
            _browser?.SetProgress(cur, total);
        }

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
