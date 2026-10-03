using System;
using System.IO;
using Android.Provider;

namespace AssetStudioMobile
{
    /// <summary>
    /// Turns a picked SAF tree URI back into a real filesystem path, when there is one.
    ///
    /// Everything else in this app works on real paths -- AssetStudio's loader has no content://
    /// equivalent -- so resolving the URI is what lets a picked folder take the same routes as a
    /// typed-in one: read where it lies, or staged with Shizuku when it belongs to another app.
    /// Without this the pick could only ever mean "copy every byte through the provider".
    /// </summary>
    internal static class SafPaths
    {
        private const string ExternalStorageAuthority = "com.android.externalstorage.documents";

        /// <summary>
        /// The path a tree URI names, or null when it does not name one.
        ///
        /// Only the external-storage provider is handled, and deliberately so: it is the one whose
        /// document ids are defined as "&lt;volume&gt;:&lt;path&gt;" ("primary:Download/foo" is
        /// /storage/emulated/0/Download/foo). A downloads or cloud provider has no path at all, and
        /// that is what the copy-through-the-provider path is still there for.
        /// </summary>
        public static string ToFileSystemPath(Android.Net.Uri treeUri)
        {
            if (treeUri == null) return null;

            if (!string.Equals(treeUri.Authority, ExternalStorageAuthority, StringComparison.OrdinalIgnoreCase))
                return null;

            // A picked folder gives a tree URI and a picked file gives a document URI, and the two
            // answer to different calls. Try both rather than making the caller care.
            string documentId = null;
            try { documentId = DocumentsContract.GetTreeDocumentId(treeUri); }
            catch { /* not a tree URI */ }

            if (string.IsNullOrEmpty(documentId))
            {
                try { documentId = DocumentsContract.GetDocumentId(treeUri); }
                catch { return null; }
            }

            if (string.IsNullOrEmpty(documentId)) return null;

            var colon = documentId.IndexOf(':');
            if (colon <= 0) return null;

            var volume = documentId.Substring(0, colon);
            var relative = documentId.Substring(colon + 1);

            var root = volume.Equals("primary", StringComparison.OrdinalIgnoreCase)
                ? PrimaryStorage()
                : "/storage/" + volume;

            if (string.IsNullOrEmpty(root)) return null;

            return relative.Length == 0 ? root : Path.Combine(root, relative);
        }

        private static string PrimaryStorage()
        {
            try
            {
                var dir = Android.OS.Environment.ExternalStorageDirectory;
                if (dir != null && !string.IsNullOrEmpty(dir.AbsolutePath)) return dir.AbsolutePath;
            }
            catch
            {
                // deprecated but still the only thing that answers for the primary volume
            }
            return "/storage/emulated/0";
        }
    }
}
