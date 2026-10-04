using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Android.Widget;
using AssetStudio;
using Path = System.IO.Path;
using Object = AssetStudio.Object;

namespace AssetStudioMobile.Ui
{
    /// <summary>
    /// Browses one batch of loaded objects and previews the ones that can be shown.
    ///
    /// One batch at a time, and the list is replaced when the batch changes, because the objects
    /// have to stay loaded to be previewed and a phone cannot hold a game's whole tree. That is the
    /// same reason the export batches; this just walks the batches instead of hiding them.
    ///
    /// The panel does no loading itself. It raises <see cref="BatchRequested"/> and the caller loads
    /// and calls <see cref="SetBatch"/>, so the slow part stays off the UI thread.
    /// </summary>
    internal sealed class PreviewPanel
    {
        /// <summary>
        /// Textures in a game reach 4096x4096, and a preview wants none of that. Downscaling during
        /// the conversion keeps one texture from being a 64 MB allocation, which on a phone is the
        /// difference between a preview and an out-of-memory crash.
        /// </summary>
        private const int MaxSide = 1024;

        /// <summary>A script or JSON blob can be megabytes; the view only needs the beginning.</summary>
        private const int MaxTextBytes = 256 * 1024;

        public View Root { get; }

        /// <summary>Raised with a batch index when the user asks for one. The caller loads it.</summary>
        public event Action<int> BatchRequested;

        /// <summary>Raised when the user leaves the browser.</summary>
        public event Action Closed;

        /// <summary>Raised for the object the preview page is showing, to write just that one.</summary>
        public event Action<Object> ExportRequested;

        /// <summary>Raised for "export everything", whatever the filter says.</summary>
        public event Action ExportAllRequested;

        /// <summary>Raised for "export what the filter selected", with the index behind it.</summary>
        public event Action<IReadOnlyList<Extractor.IndexEntry>> ExportFilteredRequested;

        /// <summary>Raised when the category selection changes, so the export uses the same set.</summary>
        public event Action<IReadOnlyCollection<ExportKind>> SelectionChanged;

        /// <summary>Raised when a row from the index is opened and its file has to be loaded.</summary>
        public event Action<Extractor.IndexEntry> IndexedRequested;

        /// <summary>
        /// Raised when a selection needs an index of the whole tree. It carries the categories, and
        /// fires on selection rather than from a button: narrowing to a category and then being
        /// shown only the batch in front of you is the thing the index exists to stop.
        /// </summary>
        public event Action<IReadOnlyCollection<ExportKind>> IndexRequested;

        private readonly Context _context;
        private readonly int _batchCount;
        private readonly Action<Action> _background;
        private readonly Action<Action> _ui;
        private readonly string _cacheDirectory;
        private readonly LinearLayout _page;
        private readonly TextView _title;
        private readonly Button _previous;
        private readonly Button _next;
        private readonly ListView _list;
        private readonly ArrayAdapter<string> _adapter;

        /// <summary>
        /// The selected categories. Empty means everything, which is also what decides the mode: any
        /// selection narrows the tree enough to be worth indexing, and everything does not.
        /// </summary>
        private readonly HashSet<ExportKind> _selected = new HashSet<ExportKind>();

        /// <summary>
        /// A list row. Either the object is already in memory, from a loaded batch, or the index
        /// knows which file to open to find it and it is loaded when it is asked for.
        /// </summary>
        private sealed class Row
        {
            public Object Loaded;
            public Extractor.IndexEntry Entry;
            public string Text;
        }

        private readonly List<Row> _all = new List<Row>();
        private readonly List<Row> _items = new List<Row>();
        private readonly Button _filterButton;
        private readonly Button _exportFiltered;
        private readonly EditText _search;
        private int _batch;
        private int _token;
        private bool _indexed;

        /// <summary>What the last index found, so "export the selection" has something to write.</summary>
        private IReadOnlyList<Extractor.IndexEntry> _indexEntries = Array.Empty<Extractor.IndexEntry>();
        private MediaPlayer _player;

        // The audio page's own controls, held so the ticker can update them and so StopAudio can
        // stop updating a page that is no longer on screen.
        private SeekBar _audioBar;
        private TextView _audioTime;
        private readonly Android.OS.Handler _ticker = new Android.OS.Handler(Android.OS.Looper.MainLooper);
        private readonly Java.Lang.Runnable _tick;

        public PreviewPanel(Context context, int batchCount, Action<Action> background, Action<Action> ui)
        {
            _context = context;
            _batchCount = Math.Max(1, batchCount);
            _tick = new Java.Lang.Runnable(TickAudio);
            _background = background ?? (work => work());
            _ui = ui ?? (work => work());
            _cacheDirectory = context.CacheDir?.AbsolutePath
                              ?? Path.Combine(Path.GetTempPath(), "assetstudio-preview");

            _title = UiKit.Label(context, "", 14, bold: true);
            _previous = UiKit.Button(context, "◀ 上一批", () => BatchRequested?.Invoke(_batch - 1));
            _next = UiKit.Button(context, "下一批 ▶", () => BatchRequested?.Invoke(_batch + 1));
            var close = UiKit.Button(context, "返回导出", () => Closed?.Invoke());
            var exportAll = UiKit.Button(context, "导出全部", () => ExportAllRequested?.Invoke());

            // Disabled rather than hidden when nothing is indexed: a missing button reads as a
            // feature that does not exist, a greyed one reads as "select something first".
            _exportFiltered = UiKit.Button(context, "导出筛选", () => ExportFilteredRequested?.Invoke(_indexEntries));
            _exportFiltered.Enabled = false;

            _filterButton = UiKit.Button(context, "筛选: 全部", ChooseCategories);

            // A batch can be ten thousand objects, and the type filter alone still leaves thousands
            // of them. Rebuilding the list per keystroke is a few milliseconds for that many rows.
            _search = UiKit.Input(context, "");
            _search.Hint = "按名字过滤";
            _search.TextChanged += (_, _) => ApplySelection();

            _adapter = new ArrayAdapter<string>(context, Android.Resource.Layout.SimpleListItem1);
            _list = new ListView(context) { Adapter = _adapter };
            _list.ItemClick += (_, e) =>
            {
                var position = (int)e.Position;
                if (position < 0 || position >= _items.Count) return;

                var row = _items[position];
                if (row.Loaded != null)
                {
                    Show(row.Loaded);
                    return;
                }

                // The index knows where it is, not what it is; the owner opens that one file and
                // calls back.
                SetStatus($"正在加载 {row.Text}…");
                IndexedRequested?.Invoke(row.Entry);
            };

            _page = new LinearLayout(context) { Orientation = Android.Widget.Orientation.Vertical };
            _page.AddView(UiKit.Fill(_list));

            Root = UiKit.Column(context,
                UiKit.Field(context, "浏览 · 一批一次加载，避免整棵资源树占用内存", _title),
                UiKit.Row(context, close, exportAll, _exportFiltered),
                UiKit.Row(context, _previous, _next, _filterButton),
                _search,
                UiKit.Fill(_page));
            ((LinearLayout)Root).SetPadding(UiKit.Dp(context, 12), UiKit.Dp(context, 12),
                                           UiKit.Dp(context, 12), UiKit.Dp(context, 12));
        }

        public void SetBatch(int index, IReadOnlyList<Object> objects)
        {
            _batch = index;
            _indexed = false;
            _indexEntries = Array.Empty<Extractor.IndexEntry>();
            _exportFiltered.Enabled = false;
            _all.Clear();
            foreach (var o in objects) _all.Add(new Row { Loaded = o, Text = Describe(o) });

            ShowList();
            ApplySelection();

            // SetSelection only moves the *selection*, which does nothing to a ListView in touch
            // mode: the scroll offset is left where it was, so arriving at a new batch from the
            // bottom of the old one lands at the bottom of the new one and reads as "it appended
            // more" rather than "this is the next batch". That was the first attempt at this fix
            // and it did nothing. SetSelectionFromTop is the call that scrolls, and it has to run
            // after the adapter change has been laid out, hence the Post.
            _list.Post(() =>
            {
                if (_list.Adapter != null && _list.Adapter.Count > 0) _list.SetSelectionFromTop(0, 0);
            });

            _previous.Enabled = index > 0;
            _next.Enabled = index < _batchCount - 1;
        }

        /// <summary>
        /// Replaces the batch view with everything the index found across the whole tree. Batch
        /// navigation is meaningless in this mode: the point is that the list is no longer cut up by
        /// which file things happened to live in.
        /// </summary>
        public void SetIndex(IReadOnlyList<Extractor.IndexEntry> entries)
        {
            _indexed = true;
            _indexEntries = entries;
            _all.Clear();
            foreach (var entry in entries) _all.Add(new Row { Entry = entry, Text = DescribeIndexed(entry) });

            _previous.Enabled = false;
            _next.Enabled = false;
            _exportFiltered.Enabled = entries.Count > 0;
            ApplySelection();
            _list.Post(() =>
            {
                if (_list.Adapter != null && _list.Adapter.Count > 0) _list.SetSelectionFromTop(0, 0);
            });
        }

        /// <summary>Hands the object for an index row back to the list, which then previews it.</summary>
        public void ShowIndexed(Extractor.IndexEntry entry, Object loaded)
        {
            foreach (var row in _all)
            {
                if (ReferenceEquals(row.Entry, entry)) { row.Loaded = loaded; break; }
            }

            if (loaded == null)
            {
                ShowPage(Page("索引条目", TextBody("加载失败：文件里找不到这个对象了。")));
                return;
            }

            Show(loaded);
        }

        private static string DescribeIndexed(Extractor.IndexEntry entry)
        {
            var name = string.IsNullOrEmpty(entry.Name) ? $"pathID {entry.PathID}" : entry.Name;
            var source = ShortSource(entry.Source);
            return source == null ? $"{entry.Type}  ·  {name}" : $"{entry.Type}  ·  {name}   [{source}]";
        }

        private void ApplySelection()
        {
            var query = _search.Text?.Trim();

            _items.Clear();
            foreach (var row in _all)
            {
                if (!MatchesSelection(row)) continue;
                if (!MatchesQuery(row, query)) continue;
                _items.Add(row);
            }

            _adapter.Clear();
            _adapter.AddAll(_items.Select(r => r.Text).ToList());
            _adapter.NotifyDataSetChanged();

            var byName = string.IsNullOrEmpty(query) ? "" : $"“{query}” ";
            var label = SelectionLabel();
            _title.Text = _indexed
                ? $"索引（整棵树）· {label} {byName}{_items.Count} / 共 {_all.Count} 个对象"
                : $"第 {_batch + 1}/{_batchCount} 批 · {label} {byName}{_items.Count} / 共 {_all.Count} 个对象";
        }

        private static bool MatchesQuery(Row row, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            return row.Text != null && row.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A loaded object is matched the way the export matches it. An index entry has no object to
        /// ask, so it is matched on the category the index recorded for it.
        /// </summary>
        private bool MatchesSelection(Row row)
        {
            if (_selected.Count == 0) return true;
            if (row.Loaded != null) return Extractor.MatchesAny(row.Loaded, _selected);
            return _selected.Contains(row.Entry.Category);
        }

        private string SelectionLabel()
        {
            if (_selected.Count == 0) return "全部";

            var parts = new List<string>();
            for (var i = 0; i < Extractor.Categories.Length; i++)
            {
                if (_selected.Contains(Extractor.Categories[i])) parts.Add(Extractor.CategoryLabels[i]);
            }
            return string.Join("+", parts);
        }

        /// <summary>
        /// Multi-select, because "find the animations and the audio" is a normal thing to want and a
        /// spinner can only express one of them.
        /// </summary>
        private void ChooseCategories()
        {
            var kinds = Extractor.Categories;
            var checkedStates = new bool[kinds.Length];
            for (var i = 0; i < kinds.Length; i++) checkedStates[i] = _selected.Contains(kinds[i]);

            var snapshot = new HashSet<ExportKind>(_selected);

            new AlertDialog.Builder(_context)
                .SetTitle("筛选类别（全不选 = 全部）")
                .SetMultiChoiceItems(Extractor.CategoryLabels, checkedStates, (_, e) =>
                {
                    if (e.IsChecked) _selected.Add(kinds[e.Which]);
                    else _selected.Remove(kinds[e.Which]);
                })
                .SetPositiveButton("确定", (_, _) => ApplyCategorySelection())
                .SetNeutralButton("全部", (_, _) =>
                {
                    _selected.Clear();
                    ApplyCategorySelection();
                })
                .SetNegativeButton("取消", (_, _) =>
                {
                    _selected.Clear();
                    foreach (var kind in snapshot) _selected.Add(kind);
                })
                .Show();
        }

        private void ApplyCategorySelection()
        {
            _filterButton.Text = "筛选: " + SelectionLabel();
            SelectionChanged?.Invoke(new List<ExportKind>(_selected));

            if (_selected.Count == 0)
            {
                // Nothing to narrow, so the batch view is the right one again -- and an index of the
                // whole tree would be every object in it.
                _indexed = false;
                BatchRequested?.Invoke(_batch);
                return;
            }

            SetStatus("正在索引 " + SelectionLabel() + " …");
            IndexRequested?.Invoke(_selected);
        }



        /// <summary>A one-line message where the batch summary normally sits.</summary>
        public void SetStatus(string message) => _ui(() => _title.Text = message);

        public void SetBusy(string message)
        {
            _title.Text = message;
            ShowList();
        }

        private static string Describe(Object o)
        {
            var name = (o as NamedObject)?.m_Name;
            if (string.IsNullOrEmpty(name)) name = $"pathID {o.m_PathID}";

            var source = SourceOf(o);
            return source == null ? $"{o.type}  ·  {name}" : $"{o.type}  ·  {name}   [{source}]";
        }

        /// <summary>
        /// The file an object came from, as a bare file name.
        ///
        /// A batch is sixty-odd bundles and the same name shows up in several of them -- a Sprite and
        /// its Texture2D, or a shared name in two chapters -- and without this the list gives no way
        /// to tell which is which.
        /// </summary>
        private static string SourceOf(Object o)
        {
            var file = o.assetsFile;
            if (file == null) return null;

            var path = !string.IsNullOrEmpty(file.originalPath) ? file.originalPath
                     : !string.IsNullOrEmpty(file.fullName) ? file.fullName
                     : file.fileName;
            return ShortSource(path);
        }

        /// <summary>Shared with the index, which stores the full path and shortens it the same way.</summary>
        private static string ShortSource(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return path;

            // An Addressables cache names every bundle __data and tells them apart by the hash
            // directory above it -- .../UnityCache/Shared/<hash>/<hash>/__data. The bare file name
            // then identifies nothing at all, which is what "[__data]" on every row looked like.
            if (name == "__data" || name == "__info")
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
                if (!string.IsNullOrEmpty(parent))
                {
                    var shortHash = parent.Length > 8 ? parent.Substring(0, 8) : parent;
                    return $"{shortHash}/{name}";
                }
            }

            return name;
        }

        private void ShowList()
        {
            _token++;
            StopAudio();
            _page.RemoveAllViews();
            _page.AddView(UiKit.Fill(_list));
        }

        private void ShowPage(View page)
        {
            page.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            _page.RemoveAllViews();
            _page.AddView(page);
        }

        private View Page(Object shown, View content)
            => Page(Describe(shown), content, () => ExportRequested?.Invoke(shown));

        private View Page(string title, View content, Action export = null)
        {
            var back = UiKit.Button(_context, "◀ 列表", ShowList);
            var row = UiKit.Row(_context, back);

            // The point of browsing is to find one thing; having found it, exporting the whole tree
            // again is not what someone means. A page with nothing to export does not offer it.
            if (export != null) row.AddView(UiKit.Button(_context, "导出这个", export));
            row.AddView(UiKit.Label(_context, title, 13, bold: true));

            return UiKit.Column(_context, row, UiKit.Fill(content));
        }

        private void Show(Object o)
        {
            // Anything asynchronous checks this before touching the screen: the user can leave while
            // an audio conversion is still running, and the result would otherwise land on whatever
            // page replaced it.
            _token++;
            StopAudio();

            try
            {
                switch (o)
                {
                    case TextAsset text:
                        ShowPage(Page(o, TextBody(TextOf(text))));
                        return;
                    case Texture2D texture:
                        ShowPage(Page(o, ImageBody(TextureBitmap(texture))));
                        return;
                    case Sprite sprite:
                        ShowPage(Page(o, ImageBody(SpriteBitmap(sprite))));
                        return;
                    case AudioClip audio:
                        ShowAudio(audio);
                        return;
                    default:
                        // Anything else gets the same JSON the export's JsonDump writes. There is no
                        // reason for a row to do nothing when the dump works for every type.
                        ShowPage(Page(o, TextBody(JsonOf(o))));
                        return;
                }
            }
            catch (Exception ex)
            {
                // An unsupported texture format or a stripped object should not take the browser
                // down with it; the message is more use than a crash.
                ShowPage(Page(o, TextBody("预览失败：" + ex.Message)));
            }
        }

        /// <summary>
        /// Converting an AudioClip is FMOD decoding real data, so it runs off the UI thread and the
        /// page is swapped in when it is ready.
        /// </summary>
        private void ShowAudio(AudioClip clip)
        {
            var token = _token;
            ShowPage(Page(clip, TextBody("正在转换音频…")));

            _background(() =>
            {
                string path = null;
                string error = null;
                try
                {
                    path = AudioCache.FileFor(clip, _cacheDirectory, out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                _ui(() =>
                {
                    if (token != _token) return;
                    ShowPage(Page(clip, AudioBody(clip, path, error)));
                });
            });
        }

        private View AudioBody(AudioClip clip, string path, string error)
        {
            var info = UiKit.Caption(_context,
                $"{clip.m_Channels} 声道 · {clip.m_Frequency} Hz · {clip.m_Length:F1} 秒 · {clip.m_CompressionFormat}");

            if (path == null)
            {
                return UiKit.Column(_context, info, TextBody("无法播放：" + (error ?? "未知原因")));
            }

            var button = UiKit.Button(_context, "▶ 播放", () => { });

            // A four minute track with only a play button gives no sense of where it is. The bar is
            // a SeekBar, so it also scrubs.
            _audioBar = new SeekBar(_context) { Max = ProgressScale, Progress = 0 };
            _audioBar.ProgressChanged += (_, e) =>
            {
                if (!e.FromUser || _player == null) return;
                try
                {
                    var duration = _player.Duration;
                    if (duration > 0) _player.SeekTo((int)((long)duration * e.Progress / ProgressScale));
                }
                catch
                {
                    // Seeking on a player that is still preparing or has already gone away throws;
                    // the ticker will put the bar back where the player actually is.
                }
            };

            _audioTime = UiKit.Caption(_context, "");

            try
            {
                _player = new MediaPlayer();
                _player.SetDataSource(path);
                _player.Prepare();
                _player.Completion += (_, _) => button.Text = "▶ 播放";
                StartTicker();
            }
            catch (Exception ex)
            {
                StopAudio();
                return UiKit.Column(_context, info, TextBody("MediaPlayer 打不开：" + ex.Message));
            }

            button.Click += (_, _) =>
            {
                if (_player == null) return;
                if (_player.IsPlaying)
                {
                    _player.Pause();
                    button.Text = "▶ 继续";
                }
                else
                {
                    _player.Start();
                    button.Text = "⏸ 暂停";
                }
            };

            return UiKit.Column(_context, info, button, _audioBar, _audioTime,
                                UiKit.Caption(_context, Path.GetFileName(path)));
        }

        /// <summary>Resolution of the SeekBar; milliseconds do not fit an int bar.</summary>
        private const int ProgressScale = 1000;

        private void StartTicker()
        {
            _ticker.RemoveCallbacks(_tick);
            _ticker.PostDelayed(_tick, 250);
        }

        private void TickAudio()
        {
            if (_player == null || _audioBar == null)
            {
                return;
            }

            try
            {
                var duration = _player.Duration;
                var position = _player.CurrentPosition;
                if (duration > 0)
                {
                    _audioBar.Max = ProgressScale;
                    _audioBar.Progress = (int)Math.Min(ProgressScale, 1000L * position / duration);
                    _audioTime.Text = $"{Clock(position)} / {Clock(duration)}";
                }
            }
            catch
            {
                // Duration and CurrentPosition both throw once the player is released; the page
                // changing calls StopAudio, so losing one tick is not worth reporting.
            }

            _ticker.PostDelayed(_tick, 250);
        }

        private static string Clock(int milliseconds)
        {
            var total = milliseconds / 1000;
            return $"{total / 60}:{total % 60:D2}";
        }

        /// <summary>Also called by the owner when the panel closes.</summary>
        public void StopAudio()
        {
            _ticker.RemoveCallbacks(_tick);
            _audioBar = null;
            _audioTime = null;

            if (_player == null) return;

            try
            {
                if (_player.IsPlaying) _player.Stop();
            }
            catch
            {
                // A player that is already in an error state throws on stop; releasing it is what
                // actually matters.
            }

            _player.Release();
            _player.Dispose();
            _player = null;
        }

        private View ImageBody(Bitmap bitmap)
        {
            if (bitmap == null) return TextBody("解码失败（可能是这个贴图格式还不支持）。");

            var image = new ImageView(_context);
            image.SetImageBitmap(bitmap);
            image.SetScaleType(ImageView.ScaleType.FitCenter);
            return UiKit.Column(_context, UiKit.Fill(image),
                                UiKit.Caption(_context, $"{bitmap.Width}×{bitmap.Height}"));
        }

        private View TextBody(string text)
        {
            var view = new TextView(_context) { Text = text };
            view.SetTextSize(Android.Util.ComplexUnitType.Sp, 12);
            view.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
            view.SetTextIsSelectable(true);

            var scroll = new ScrollView(_context);
            scroll.AddView(view);
            return scroll;
        }

        /// <summary>
        /// The same two paths the export's JSON dump uses: the embedded type tree when the file has
        /// one, and reflection over the parsed fields when it does not. MonoBehaviour is the type
        /// that usually needs this, since its fields only exist as a type tree.
        /// </summary>
        private static string JsonOf(Object o)
        {
            string json;
            try
            {
                json = o.Dump() ?? o.DumpObject();
            }
            catch (Exception ex)
            {
                return "dump 失败：" + ex.Message;
            }

            if (string.IsNullOrEmpty(json)) return "(这个对象没有可 dump 的内容)";

            return json.Length > MaxTextBytes
                ? json.Substring(0, MaxTextBytes) + $"\n\n…（已截断，共 {json.Length / 1024} KB）"
                : json;
        }

        private static string TextOf(TextAsset asset)
        {
            var bytes = asset.m_Script;
            if (bytes == null || bytes.Length == 0) return "(空)";

            var truncated = bytes.Length > MaxTextBytes;
            var text = new UTF8Encoding(false).GetString(bytes, 0, Math.Min(bytes.Length, MaxTextBytes));
            if (!truncated) return text.TrimStart('\ufeff');
            return text + $"\n\n…（只显示前 {MaxTextBytes / 1024} KB，共 {bytes.Length / 1024} KB）";
        }

        private static Bitmap TextureBitmap(Texture2D texture)
        {
            // The raw path skips building an ImageSharp image; the swizzle path still needs it,
            // because that is where the unswizzle and the crop live.
            var buffer = texture.DecodeToBgraBuffer(out var width, out var height);
            if (buffer != null)
            {
                try { return ToBitmap(buffer, width, height); }
                finally { Texture2DExtensions.ReturnDecodedBuffer(buffer); }
            }

            using var image = texture.ConvertToImage(false);
            if (image == null) return null;
            var bytes = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(bytes);
            return ToBitmap(bytes, image.Width, image.Height);
        }

        private static Bitmap SpriteBitmap(Sprite sprite)
        {
            using var image = sprite.GetImage();
            if (image == null) return null;
            var bytes = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(bytes);

            // Already the right way up; see the note in ToBitmap.
            return ToBitmap(bytes, image.Width, image.Height, flipVertical: false);
        }

        /// <summary>
        /// BGRA bytes to an Android bitmap, downscaled in the same pass.
        ///
        /// Built as ARGB ints rather than by copying bytes: our decoders produce BGRA, Android's
        /// ARGB_8888 is RGBA in memory, and an int per pixel makes the channel order explicit
        /// instead of something to be discovered from a wrong-looking preview.
        /// </summary>
        private static Bitmap ToBitmap(byte[] bgra, int width, int height, bool flipVertical = true)
        {
            if (width <= 0 || height <= 0) return null;
            if ((long)width * height * 4 > bgra.Length) return null;

            var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
            var w = Math.Max(1, (int)(width * scale));
            var h = Math.Max(1, (int)(height * scale));
            var pixels = new int[w * h];

            for (var y = 0; y < h; y++)
            {
                var sy = scale >= 1.0 ? y : Math.Min(height - 1, (int)(y / scale));

                // Decoders write pixels bottom-up, in OpenGL order, and the export flips them inside
                // the PNG encoder. A preview has no encoder in the path, so a texture's flip has to
                // happen here or it is upside down.
                //
                // Sprites are the exception and must not be flipped again: SpriteHelper.CutImage
                // ends with an unconditional Flip(Vertical) on both of its return paths, so the
                // image it hands back is already the right way up. Flipping it here turned every
                // sprite upside down, which is exactly what the first version of this did.
                if (flipVertical) sy = height - 1 - sy;

                var sourceRow = sy * width;
                var targetRow = y * w;
                for (var x = 0; x < w; x++)
                {
                    var sx = scale >= 1.0 ? x : Math.Min(width - 1, (int)(x / scale));
                    var i = (sourceRow + sx) * 4;
                    pixels[targetRow + x] = (bgra[i + 3] << 24) | (bgra[i + 2] << 16)
                                          | (bgra[i + 1] << 8) | bgra[i];
                }
            }

            return Bitmap.CreateBitmap(pixels, w, h, Bitmap.Config.Argb8888);
        }
    }
}
