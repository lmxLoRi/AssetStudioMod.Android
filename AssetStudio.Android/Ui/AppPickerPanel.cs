using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Views;
using Android.Widget;

namespace AssetStudioMobile.Ui
{
    /// <summary>
    /// Picks an installed app to extract from.
    ///
    /// This used to be an AlertDialog with one row per app, which was fine when the list was short
    /// and unusable once QUERY_ALL_PACKAGES made it complete: a test device reports 1074 of them, and
    /// a dialog with no search box is a list you scroll through by name-guessing. So it is a screen
    /// with a search field, matching on the label and on the package name.
    /// </summary>
    internal sealed class AppPickerPanel
    {
        public View Root { get; }

        public event Action Closed;
        public event Action<ApkImport.InstalledApp> Chosen;

        private readonly LinearLayout _page;
        private readonly ListView _list;
        private readonly ArrayAdapter<string> _adapter;
        private readonly EditText _search;
        private readonly TextView _count;

        private readonly List<ApkImport.InstalledApp> _all;
        private readonly List<ApkImport.InstalledApp> _shown = new List<ApkImport.InstalledApp>();

        public AppPickerPanel(Activity activity, IReadOnlyList<ApkImport.InstalledApp> apps)
        {
            _all = new List<ApkImport.InstalledApp>(apps);

            var back = UiKit.Button(activity, "返回导出", () => Closed?.Invoke());
            _count = UiKit.Caption(activity, "");

            _search = UiKit.Input(activity, "");
            _search.Hint = "按应用名或包名过滤";
            _search.TextChanged += (_, _) => ApplyFilter();

            _adapter = new ArrayAdapter<string>(activity, Android.Resource.Layout.SimpleListItem1);
            _list = new ListView(activity) { Adapter = _adapter };
            _list.ItemClick += (_, e) =>
            {
                var position = (int)e.Position;
                if (position >= 0 && position < _shown.Count) Chosen?.Invoke(_shown[position]);
            };

            _page = new LinearLayout(activity) { Orientation = Android.Widget.Orientation.Vertical };
            _page.AddView(UiKit.Fill(_list));

            Root = UiKit.Column(activity,
                UiKit.Row(activity, back),
                UiKit.Field(activity, "从已安装应用导入", _search),
                _count,
                UiKit.Fill(_page));
            ((LinearLayout)Root).SetPadding(UiKit.Dp(activity, 12), UiKit.Dp(activity, 12),
                                            UiKit.Dp(activity, 12), UiKit.Dp(activity, 12));

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var query = _search.Text?.Trim();

            _shown.Clear();
            foreach (var app in _all)
            {
                if (!Matches(app, query)) continue;
                _shown.Add(app);
            }

            _adapter.Clear();
            _adapter.AddAll(_shown.Select(a => a.ToString()).ToList());
            _adapter.NotifyDataSetChanged();

            _count.Text = _shown.Count == _all.Count
                ? $"共 {_all.Count} 个应用"
                : $"匹配 {_shown.Count} / 共 {_all.Count} 个应用";
        }

        private static bool Matches(ApkImport.InstalledApp app, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;

            return Contains(app.Label, query) || Contains(app.PackageName, query);
        }

        private static bool Contains(string value, string query)
            => value != null && value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
