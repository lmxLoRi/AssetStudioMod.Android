using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        /// The type filter. Added after driving the browser on a real game: a batch of 10706 objects
        /// contained 4204 MonoBehaviours, mostly unnamed, and every Sprite in it was unreachable
        /// behind them. Counting them is useful; having to scroll past them is not.
        /// </summary>
        private static readonly string[] Filters = { "全部", "贴图", "Sprite", "文本", "音频", "脚本" };

        private readonly List<Object> _all = new List<Object>();
        private readonly List<Object> _items = new List<Object>();
        private readonly Spinner _filter;
        private int _batch;
        private int _token;
        private MediaPlayer _player;

        public PreviewPanel(Context context, int batchCount, Action<Action> background, Action<Action> ui)
        {
            _context = context;
            _batchCount = Math.Max(1, batchCount);
            _background = background ?? (work => work());
            _ui = ui ?? (work => work());
            _cacheDirectory = context.CacheDir?.AbsolutePath
                              ?? Path.Combine(Path.GetTempPath(), "assetstudio-preview");

            _title = UiKit.Label(context, "", 14, bold: true);
            _previous = UiKit.Button(context, "◀ 上一批", () => BatchRequested?.Invoke(_batch - 1));
            _next = UiKit.Button(context, "下一批 ▶", () => BatchRequested?.Invoke(_batch + 1));
            var close = UiKit.Button(context, "返回导出", () => Closed?.Invoke());

            _filter = new Spinner(context);
            _filter.Adapter = new ArrayAdapter<string>(context, Android.Resource.Layout.SimpleSpinnerDropDownItem, Filters);
            _filter.ItemSelected += (_, e) => ApplyFilter((int)e.Position);

            _adapter = new ArrayAdapter<string>(context, Android.Resource.Layout.SimpleListItem1);
            _list = new ListView(context) { Adapter = _adapter };
            _list.ItemClick += (_, e) =>
            {
                var position = (int)e.Position;
                if (position >= 0 && position < _items.Count) Show(_items[position]);
            };

            _page = new LinearLayout(context) { Orientation = Android.Widget.Orientation.Vertical };
            _page.AddView(UiKit.Fill(_list));

            Root = UiKit.Column(context,
                UiKit.Field(context, "浏览 · 一批一次加载，避免整棵资源树占用内存", _title),
                UiKit.Row(context, close, _previous, _next, _filter),
                UiKit.Fill(_page));
            ((LinearLayout)Root).SetPadding(UiKit.Dp(context, 12), UiKit.Dp(context, 12),
                                           UiKit.Dp(context, 12), UiKit.Dp(context, 12));
        }

        /// <summary>Only these can be shown; everything else would be a dead row that does nothing.</summary>
        public static bool IsPreviewable(Object o)
            => o is Texture2D || o is Sprite || o is TextAsset || o is AudioClip || o is MonoBehaviour;

        public void SetBatch(int index, IReadOnlyList<Object> objects)
        {
            _batch = index;
            _all.Clear();
            _all.AddRange(objects);

            ShowList();
            ApplyFilter(_filter.SelectedItemPosition);
            _previous.Enabled = index > 0;
            _next.Enabled = index < _batchCount - 1;
        }

        private void ApplyFilter(int filter)
        {
            _items.Clear();
            _items.AddRange(_all.Where(o => Matches(o, filter)));

            _adapter.Clear();
            _adapter.AddAll(_items.Select(Describe).ToList());
            _adapter.NotifyDataSetChanged();

            _title.Text = $"第 {_batch + 1}/{_batchCount} 批 · {Filters[filter]} {_items.Count} / 共 {_all.Count} 个对象";
        }

        private static bool Matches(Object o, int filter) => filter switch
        {
            1 => o is Texture2D,
            2 => o is Sprite,
            3 => o is TextAsset,
            4 => o is AudioClip,
            5 => o is MonoBehaviour,
            _ => IsPreviewable(o),
        };

        public void SetBusy(string message)
        {
            _title.Text = message;
            ShowList();
        }

        private static string Describe(Object o)
        {
            var name = (o as NamedObject)?.m_Name;
            if (string.IsNullOrEmpty(name)) name = $"pathID {o.m_PathID}";
            return $"{o.type}  ·  {name}";
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

        private View Page(string title, View content)
        {
            var back = UiKit.Button(_context, "◀ 列表", ShowList);
            return UiKit.Column(_context,
                UiKit.Row(_context, back, UiKit.Label(_context, title, 13, bold: true)),
                UiKit.Fill(content));
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
                        ShowPage(Page(Describe(o), TextBody(TextOf(text))));
                        return;
                    case Texture2D texture:
                        ShowPage(Page(Describe(o), ImageBody(TextureBitmap(texture))));
                        return;
                    case Sprite sprite:
                        ShowPage(Page(Describe(o), ImageBody(SpriteBitmap(sprite))));
                        return;
                    case AudioClip audio:
                        ShowAudio(audio);
                        return;
                    default:
                        // Anything else gets the same JSON the export's JsonDump writes. There is no
                        // reason for a row to do nothing when the dump works for every type.
                        ShowPage(Page(Describe(o), TextBody(JsonOf(o))));
                        return;
                }
            }
            catch (Exception ex)
            {
                // An unsupported texture format or a stripped object should not take the browser
                // down with it; the message is more use than a crash.
                ShowPage(Page(Describe(o), TextBody("预览失败：" + ex.Message)));
            }
        }

        /// <summary>
        /// Converting an AudioClip is FMOD decoding real data, so it runs off the UI thread and the
        /// page is swapped in when it is ready.
        /// </summary>
        private void ShowAudio(AudioClip clip)
        {
            var token = _token;
            var title = Describe(clip);
            ShowPage(Page(title, TextBody("正在转换音频…")));

            _background(() =>
            {
                string path = null;
                string error = null;
                try
                {
                    path = AudioCache.WavFor(clip, _cacheDirectory, out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                _ui(() =>
                {
                    if (token != _token) return;
                    ShowPage(Page(title, AudioBody(clip, path, error)));
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
            try
            {
                _player = new MediaPlayer();
                _player.SetDataSource(path);
                _player.Prepare();
                _player.Completion += (_, _) => button.Text = "▶ 播放";
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

            return UiKit.Column(_context, info, button,
                                UiKit.Caption(_context, Path.GetFileName(path)));
        }

        /// <summary>Also called by the owner when the panel closes.</summary>
        public void StopAudio()
        {
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
            return ToBitmap(bytes, image.Width, image.Height);
        }

        /// <summary>
        /// BGRA bytes to an Android bitmap, downscaled in the same pass.
        ///
        /// Built as ARGB ints rather than by copying bytes: our decoders produce BGRA, Android's
        /// ARGB_8888 is RGBA in memory, and an int per pixel makes the channel order explicit
        /// instead of something to be discovered from a wrong-looking preview.
        /// </summary>
        private static Bitmap ToBitmap(byte[] bgra, int width, int height)
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

                // Decoders write pixels bottom-up, in OpenGL order, and the export flips them
                // inside the PNG encoder. A preview has no encoder in the path, so the flip has to
                // happen here -- without it every image is upside down. Sprites come out of
                // SpriteHelper the same way up (it calls ConvertToImage(flip: false) too), so one
                // flip covers both.
                sy = height - 1 - sy;

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
