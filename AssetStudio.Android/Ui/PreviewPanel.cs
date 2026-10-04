using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AssetStudio;
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
        private readonly LinearLayout _page;
        private readonly TextView _title;
        private readonly Button _previous;
        private readonly Button _next;
        private readonly ListView _list;
        private readonly ArrayAdapter<string> _adapter;

        private readonly List<Object> _items = new List<Object>();
        private int _batch;

        public PreviewPanel(Context context, int batchCount)
        {
            _context = context;
            _batchCount = Math.Max(1, batchCount);

            _title = UiKit.Label(context, "", 14, bold: true);
            _previous = UiKit.Button(context, "◀ 上一批", () => BatchRequested?.Invoke(_batch - 1));
            _next = UiKit.Button(context, "下一批 ▶", () => BatchRequested?.Invoke(_batch + 1));
            var close = UiKit.Button(context, "返回导出", () => Closed?.Invoke());

            _adapter = new ArrayAdapter<string>(context, Android.Resource.Layout.SimpleListItem1);
            _list = new ListView(context) { Adapter = _adapter };
            _list.ItemClick += (_, e) =>
            {
                var position = (int)e.Position;
                if (position >= 0 && position < _items.Count) Show(_items[position]);
            };

            _page = new LinearLayout(context) { Orientation = Orientation.Vertical };
            _page.AddView(UiKit.Fill(_list));

            Root = UiKit.Column(context,
                UiKit.Field(context, "浏览 · 一批一次加载，避免整棵资源树占用内存", _title),
                UiKit.Row(context, close, _previous, _next),
                UiKit.Fill(_page));
            ((LinearLayout)Root).SetPadding(UiKit.Dp(context, 12), UiKit.Dp(context, 12),
                                           UiKit.Dp(context, 12), UiKit.Dp(context, 12));
        }

        /// <summary>Only these can be shown; everything else would be a dead row that does nothing.</summary>
        public static bool IsPreviewable(Object o) => o is Texture2D || o is Sprite || o is TextAsset;

        public void SetBatch(int index, IReadOnlyList<Object> objects)
        {
            _batch = index;
            _items.Clear();
            _items.AddRange(objects.Where(IsPreviewable));

            _adapter.Clear();
            _adapter.AddAll(_items.Select(Describe).ToList());
            _adapter.NotifyDataSetChanged();

            ShowList();
            _title.Text = $"第 {index + 1}/{_batchCount} 批 · 可预览 {_items.Count} / 共 {objects.Count} 个对象";
            _previous.Enabled = index > 0;
            _next.Enabled = index < _batchCount - 1;
        }

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
                    default:
                        ShowPage(Page(Describe(o), TextBody("这个类型还没有预览。")));
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
