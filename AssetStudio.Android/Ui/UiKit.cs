using System;
using Android.Content;
using Android.Views;
using Android.Widget;

namespace AssetStudioMobile.Ui
{
    /// <summary>
    /// The view-building helpers the screens share.
    ///
    /// MainActivity built its controls inline, which was fine for eight of them. Now that there is a
    /// browser with a list, a preview pane and two pages, the repeated padding and the repeated
    /// label styling are worth naming once, and the screens are worth being separate classes rather
    /// than another four hundred lines in an OnCreate.
    /// </summary>
    internal static class UiKit
    {
        public static int Dp(Context context, double value)
            => (int)Math.Round(value * context.Resources!.DisplayMetrics!.Density);

        public static TextView Label(Context context, string text, double size = 14, bool bold = false)
        {
            var view = new TextView(context) { Text = text };
            view.SetTextSize(Android.Util.ComplexUnitType.Sp, (float)size);
            if (bold) view.SetTypeface(view.Typeface, Android.Graphics.TypefaceStyle.Bold);
            return view;
        }

        /// <summary>A dimmer, smaller label for the second line of a row or a hint under a control.</summary>
        public static TextView Caption(Context context, string text)
        {
            var view = Label(context, text, 12);

            // Dimmed black, not dimmed white. The app runs on the platform's light theme, so the
            // white version was invisible: the browser's hint row rendered as a faint smudge on a
            // near-white background.
            view.SetTextColor(Android.Graphics.Color.Argb(150, 0, 0, 0));
            return view;
        }

        public static Button Button(Context context, string text, Action click)
        {
            var view = new Button(context) { Text = text };
            view.Click += (_, _) => click();
            return view;
        }

        public static EditText Input(Context context, string value)
        {
            var view = new EditText(context) { Text = value };
            view.SetSingleLine(true);
            return view;
        }

        public static LinearLayout Row(Context context, params View[] children)
        {
            var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            foreach (var child in children) row.AddView(child);
            return row;
        }

        public static LinearLayout Column(Context context, params View[] children)
        {
            var column = new LinearLayout(context) { Orientation = Orientation.Vertical };
            foreach (var child in children) column.AddView(child);
            return column;
        }

        /// <summary>A labelled row: caption above, control below, which is most of what a form is.</summary>
        public static LinearLayout Field(Context context, string caption, View control)
        {
            var field = Column(context, Caption(context, caption), control);
            field.SetPadding(0, Dp(context, 6), 0, Dp(context, 6));
            return field;
        }

        public static View Gap(Context context, double dp)
            => new Space(context) { LayoutParameters = new LinearLayout.LayoutParams(1, Dp(context, dp)) };

        /// <summary>Lets a child take the rest of the vertical space, for list pages.</summary>
        public static T Fill<T>(T view) where T : View
        {
            view.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f);
            return view;
        }

        public static void Margin(View view, Context context, double left, double top, double right, double bottom)
        {
            if (view.LayoutParameters is ViewGroup.MarginLayoutParams margins)
            {
                margins.SetMargins(Dp(context, left), Dp(context, top), Dp(context, right), Dp(context, bottom));
                view.LayoutParameters = margins;
            }
        }
    }
}
