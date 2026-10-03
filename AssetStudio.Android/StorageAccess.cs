using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Provider;

namespace AssetStudioMobile
{
    /// <summary>
    /// Two ways to get a directory AssetStudio can read.
    ///
    /// 1. All-files access (MANAGE_EXTERNAL_STORAGE). The user grants it once in Settings, then
    ///    any real path can be read and written directly -- no copying at all. This is by far the
    ///    best experience, but note what the platform documentation explicitly says it does NOT
    ///    cover: the app-specific directories of *other* apps. Unity games keep their data in
    ///    /sdcard/Android/data/&lt;game&gt;/files/, so bundles sitting there stay unreachable.
    ///
    /// 2. The Storage Access Framework. Always available, needs no permission, but only yields a
    ///    content:// URI, which AssetStudio cannot use (it needs seekable real files), so the tree
    ///    has to be copied. When all-files access happens to be granted we can usually map the URI
    ///    back to a real path and skip the copy -- see TryResolveTreePath.
    ///
    /// Google Play restricts all-files access to a short list of permitted use cases that do not
    /// include asset extraction, so an app declaring it cannot be published there. It is fine for
    /// sideloading, and the SAF path remains as the policy-clean fallback.
    /// </summary>
    public static class StorageAccess
    {
        public static bool HasAllFilesAccess()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.R) return true; // pre-11: legacy behaviour
            try
            {
                return Android.OS.Environment.IsExternalStorageManager;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Intent that takes the user to the per-app "All files access" toggle.</summary>
        public static Intent BuildAllFilesAccessIntent(Activity activity)
        {
            var intent = new Intent(
                Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                Android.Net.Uri.Parse("package:" + activity.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            return intent;
        }

        /// <summary>
        /// Best-effort mapping of a picked SAF tree back to a real filesystem path.
        ///
        /// Document ids are "<volume>:<relative path>", e.g. "primary:Download/mygame" or
        /// "&lt;uuid&gt;:Android/data/...". This is a convention rather than an API, so the result
        /// is always validated before use and callers must fall back to copying.
        /// </summary>
        public static string TryResolveTreePath(Context context, Android.Net.Uri treeUri)
        {
            if (!HasAllFilesAccess()) return null;
            if (treeUri == null) return null;

            try
            {
                var docId = DocumentsContract.GetTreeDocumentId(treeUri);
                if (string.IsNullOrEmpty(docId)) return null;

                var colon = docId.IndexOf(':');
                if (colon < 0) return null;

                var volume = docId.Substring(0, colon);
                var relative = docId.Substring(colon + 1);

                string root;
                if (volume.Equals("primary", StringComparison.OrdinalIgnoreCase))
                {
                    root = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
                }
                else
                {
                    // Removable volumes are mounted at /storage/<volume-id>.
                    root = Path.Combine("/storage", volume);
                }
                if (string.IsNullOrEmpty(root)) return null;

                var path = relative.Length == 0 ? root : Path.Combine(root, relative);

                // The mapping is a guess, so never hand back something we cannot actually use.
                if (!Directory.Exists(path)) return null;
                try
                {
                    using var probe = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                    probe.MoveNext(); // throws if access is denied
                }
                catch
                {
                    return null;
                }

                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Validates that a user-supplied directory really is readable and contains something.
        /// Returns null when usable, otherwise a human-readable reason.
        /// </summary>
        public static string ValidateReadableDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "path is empty";

            string full;
            try
            {
                full = Path.GetFullPath(path.Trim());
            }
            catch (Exception ex)
            {
                return $"bad path: {ex.Message}";
            }

            if (!Directory.Exists(full)) return "directory does not exist";
            if (!HasAllFilesAccess() && !IsUnderAppExternal(full))
                return "no all-files access; pick the folder with the button below instead";

            try
            {
                var count = 0;
                foreach (var _ in Directory.EnumerateFileSystemEntries(full))
                {
                    if (++count >= 1) break;
                }
            }
            catch (Exception ex)
            {
                return $"cannot read: {ex.Message}";
            }

            return null;
        }

        private static bool IsUnderAppExternal(string path)
        {
            // getExternalFilesDir is always writable for us, permission or not.
            var ext = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
            return !string.IsNullOrEmpty(ext) &&
                   path.StartsWith(ext + "/Android/data/", StringComparison.Ordinal);
        }
    }
}
