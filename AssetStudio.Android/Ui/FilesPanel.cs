using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.App;
using Android.Views;
using Android.Widget;

namespace AssetStudioMobile.Ui
{
    /// <summary>
    /// A file browser for the app's own directories, with delete.
    ///
    /// This exists because of a hole Android 11 opened: /sdcard/Android/data/&lt;pkg&gt;/files is not
    /// reachable by any other app, by a file manager, or by the user through the system UI. The app
    /// fills it with staged bundles, staged APKs and decrypted copies -- gigabytes of them -- and
    /// then nothing on the device can remove them except Shizuku, adb, or the app itself. So the app
    /// has to offer it.
    ///
    /// Deleting is the whole point, so the sizes come first: a directory that says 4.01 GB is one to
    /// think about, and the confirmation says how much is about to go.
    /// </summary>
    internal sealed class FilesPanel
    {
        public View Root { get; }

        /// <summary>Raised when the user leaves.</summary>
        public event Action Closed;

        private readonly Activity _activity;
        private readonly string _root;
        private readonly Action<Action> _background;
        private readonly Action<Action> _ui;

        private readonly TextView _path;
        private readonly TextView _total;
        private readonly TextView _status;
        private readonly ListView _list;
        private readonly ArrayAdapter<string> _adapter;

        private readonly List<Entry> _entries = new List<Entry>();
        private string _current;

        private sealed class Entry
        {
            public string Path;
            public bool IsDirectory;
            public string Label;
        }

        public FilesPanel(Activity activity, string root, Action<Action> background, Action<Action> ui)
        {
            _activity = activity;
            _root = Path.GetFullPath(root);
            _background = background ?? (work => work());
            _ui = ui ?? (work => work());

            _path = UiKit.Label(activity, "", 12);
            _total = UiKit.Label(activity, "", 12, bold: true);
            _status = UiKit.Caption(activity, "点目录进入 · 长按删除 · 上面可以清空此处");

            _adapter = new ArrayAdapter<string>(activity, Android.Resource.Layout.SimpleListItem1);
            _list = new ListView(activity) { Adapter = _adapter };
            _list.ItemClick += (_, e) =>
            {
                var position = (int)e.Position;
                if (position < 0 || position >= _entries.Count) return;

                var entry = _entries[position];
                if (entry.IsDirectory) Enter(entry.Path);
                else ConfirmDelete(entry.Path, false, "删除");
            };

            // Long press deletes a directory without having to walk into it first, which is the
            // common case: a staged tree nobody wants to open.
            _list.ItemLongClick += (_, e) =>
            {
                var position = (int)e.Position;
                if (position >= 0 && position < _entries.Count)
                {
                    var entry = _entries[position];
                    ConfirmDelete(entry.Path, entry.IsDirectory, "删除");
                }
                e.Handled = true;
            };

            var close = UiKit.Button(activity, "返回导出", () => Closed?.Invoke());
            var up = UiKit.Button(activity, "上级", () => Enter(Path.GetDirectoryName(_current)));
            var wipe = UiKit.Button(activity, "清空此处", () => ConfirmDelete(_current, true, "清空"));

            Root = UiKit.Column(activity,
                UiKit.Row(activity, close, up, wipe),
                UiKit.Field(activity, "当前目录", _path),
                _total,
                _status,
                UiKit.Fill(_list));
            ((LinearLayout)Root).SetPadding(UiKit.Dp(activity, 12), UiKit.Dp(activity, 12),
                                            UiKit.Dp(activity, 12), UiKit.Dp(activity, 12));

            Enter(_root);
        }

        private void Enter(string path)
        {
            if (string.IsNullOrEmpty(path) || !Inside(path))
            {
                if (!string.IsNullOrEmpty(path)) _status.Text = "只能在自己的目录里操作";
                return;
            }

            _current = path;
            _path.Text = path;
            _status.Text = "正在读取…";
            _adapter.Clear();
            _entries.Clear();
            _adapter.NotifyDataSetChanged();

            _background(() =>
            {
                var entries = Read(path);
                _ui(() =>
                {
                    if (_current != path) return;   // the user moved on while this was reading

                    _entries.Clear();
                    _entries.AddRange(entries);
                    _adapter.Clear();
                    _adapter.AddAll(entries.Select(e => e.Label).ToList());
                    _adapter.NotifyDataSetChanged();
                    _status.Text = entries.Count == 0 ? "（空）" : "点目录进入 · 长按删除 · 上面可以清空此处";

                    // Sizes are a second pass: a directory can hold thousands of files and walking it
                    // is slower than listing it. Filling them in as they are known beats a blank list.
                    Measure(path);
                });
            });
        }

        private List<Entry> Read(string path)
        {
            var result = new List<Entry>();
            try
            {
                foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    result.Add(new Entry { Path = dir, IsDirectory = true, Label = $"📁 {Path.GetFileName(dir)}" });

                foreach (var file in Directory.GetFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    result.Add(new Entry { Path = file, IsDirectory = false, Label = $"📄 {Path.GetFileName(file)}" });
            }
            catch (Exception ex)
            {
                _ui(() => _status.Text = "读不了这个目录：" + ex.Message);
            }
            return result;
        }

        /// <summary>Fills in "name — size  [删除]" once each size is known, biggest first.</summary>
        private void Measure(string path)
        {
            _background(() =>
            {
                var sized = new List<(Entry Entry, long Bytes)>();
                long total = 0;

                foreach (var entry in _entries.ToList())
                {
                    var bytes = SizeOf(entry.Path);
                    total += bytes;
                    sized.Add((entry, bytes));
                }

                var grand = total;
                _ui(() =>
                {
                    if (_current != path) return;

                    sized.Sort((a, b) =>
                    {
                        if (a.Entry.IsDirectory != b.Entry.IsDirectory) return a.Entry.IsDirectory ? -1 : 1;
                        return b.Bytes.CompareTo(a.Bytes);
                    });

                    // The entries are rebuilt from the sorted list, in the same order the adapter
                    // is about to be filled. They used to be sorted apart -- the list by size, the
                    // entries not at all -- and since a tap is looked up in the entries by position,
                    // tapping one folder opened another.
                    _entries.Clear();
                    _entries.AddRange(sized.Select(x => x.Entry));

                    _total.Text = $"合计 {Human(grand)}";
                    _adapter.Clear();
                    foreach (var (entry, bytes) in sized)
                    {
                        var kind = entry.IsDirectory ? "📁" : "📄";
                        _adapter.Add($"{kind} {Path.GetFileName(entry.Path)}   —   {Human(bytes)}");
                    }
                    _adapter.NotifyDataSetChanged();
                });
            });
        }

        private static long SizeOf(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (!Directory.Exists(path)) return 0;

                long total = 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { /* vanished or unreadable; the total is still useful */ }
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }

        private static string Human(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        /// <summary>True when a path is inside the root, so this cannot be pointed anywhere else.</summary>
        private bool Inside(string path)
        {
            var full = Path.GetFullPath(path);
            return full == _root || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        private void ConfirmDelete(string path, bool isDirectory, string verb)
        {
            if (string.IsNullOrEmpty(path) || !Inside(path)) return;
            if (path == _root)
            {
                _status.Text = "根目录不能整个删除";
                return;
            }

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            var size = Human(SizeOf(path));

            new AlertDialog.Builder(_activity)
                .SetTitle($"{verb} {name}？")
                .SetMessage(isDirectory ? $"会连同里面的所有内容一起删除，共 {size}。" : $"共 {size}。")
                .SetPositiveButton(verb, (_, _) => Delete(path, isDirectory))
                .SetNegativeButton("取消", (_, _) => { })
                .Show();
        }

        private void Delete(string path, bool isDirectory)
        {
            _status.Text = "正在删除…";
            _background(() =>
            {
                string error = null;
                try
                {
                    if (isDirectory) Directory.Delete(path, true);
                    else File.Delete(path);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                _ui(() =>
                {
                    if (error != null) _status.Text = "删除失败：" + error;
                    Enter(_current);   // re-read either way; a partial delete still changed things
                });
            });
        }
    }
}
