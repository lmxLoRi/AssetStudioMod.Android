using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Android.Content;
using Android.Database;
using Android.Provider;

namespace AssetStudioMobile
{
    /// <summary>
    /// Copies a SAF tree URI into app-private storage.
    ///
    /// AssetStudio addresses every file by real filesystem path and
    /// Directory.GetFiles(..., SearchOption.AllDirectories) has no content:// equivalent, so a
    /// picked document tree has to be materialised on disk before the loader can see it.
    ///
    /// Note the binding names: the column constants live on DocumentsContract.Document
    /// (ColumnDocumentId / ColumnMimeType / ColumnDisplayName), not on DocumentsContract itself.
    /// </summary>
    public static class ImportUtils
    {
        public static int CopyTree(
            Context context,
            Android.Net.Uri treeUri,
            string destRoot,
            Action<string> log,
            Action<int, int> progress)
        {
            var rootDoc = DocumentsContract.BuildDocumentUriUsingTree(
                treeUri, DocumentsContract.GetTreeDocumentId(treeUri));

            Directory.CreateDirectory(destRoot);

            var pending = new Stack<Android.Net.Uri>();
            pending.Push(rootDoc);

            var copied = 0;
            var seen = 0;

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!IsDirectory(context, current))
                {
                    var name = DisplayName(context, current) ?? LeafName(current);
                    if (name == null) continue;

                    var target = Path.Combine(destRoot, Sanitize(name));
                    try
                    {
                        CopyFile(context, current, target);
                        copied++;
                        if (copied % 20 == 0) log?.Invoke($"copied {copied} file(s)...");
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"skip {name}: {ex.GetType().Name}: {ex.Message}");
                    }

                    progress?.Invoke(copied, ++seen);
                    continue;
                }

                var dirName = DisplayName(context, current) ?? LeafName(current);
                var sub = dirName == null
                    ? destRoot
                    : Path.Combine(destRoot, Sanitize(dirName));

                try
                {
                    Directory.CreateDirectory(sub);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"skip dir {dirName}: {ex.Message}");
                    continue;
                }

                EnqueueChildren(context, current, sub, pending, log);
                progress?.Invoke(copied, ++seen);
            }

            progress?.Invoke(copied, seen);
            return copied;
        }

        private static void EnqueueChildren(
            Context context,
            Android.Net.Uri dirUri,
            string destDir,
            Stack<Android.Net.Uri> pending,
            Action<string> log)
        {
            var childBase = DocumentsContract.BuildChildDocumentsUriUsingTree(
                dirUri, DocumentsContract.GetDocumentId(dirUri));

            ICursor cursor;
            try
            {
                cursor = context.ContentResolver.Query(
                    childBase,
                    new[]
                    {
                        DocumentsContract.Document.ColumnDocumentId,
                        DocumentsContract.Document.ColumnMimeType,
                        DocumentsContract.Document.ColumnDisplayName,
                    },
                    null, null, null);
            }
            catch (Exception ex)
            {
                log?.Invoke($"cannot list {destDir}: {ex.Message}");
                return;
            }

            if (cursor == null) return;

            using (cursor)
            {
                var idIdx = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDocumentId);
                while (cursor.MoveToNext())
                {
                    if (idIdx < 0) break;
                    var docId = cursor.GetString(idIdx);
                    if (string.IsNullOrEmpty(docId)) continue;

                    try
                    {
                        pending.Push(DocumentsContract.BuildDocumentUriUsingTree(dirUri, docId));
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"bad document id '{docId}': {ex.Message}");
                    }
                }
            }
        }

        private static bool IsDirectory(Context context, Android.Net.Uri uri)
        {
            try
            {
                return string.Equals(
                    context.ContentResolver.GetType(uri),
                    DocumentsContract.Document.MimeTypeDir,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string DisplayName(Context context, Android.Net.Uri uri)
        {
            try
            {
                using var cursor = context.ContentResolver.Query(
                    uri, new[] { DocumentsContract.Document.ColumnDisplayName }, null, null, null);
                if (cursor != null && cursor.MoveToFirst())
                {
                    var idx = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
                    if (idx >= 0) return cursor.GetString(idx);
                }
            }
            catch
            {
                // fall through to the document-id leaf
            }
            return null;
        }

        private static string LeafName(Android.Net.Uri uri)
        {
            try
            {
                var id = DocumentsContract.GetDocumentId(uri);
                if (string.IsNullOrEmpty(id)) return null;
                var slash = id.LastIndexOf('/');
                return slash >= 0 ? id.Substring(slash + 1) : id;
            }
            catch
            {
                return null;
            }
        }

        private static void CopyFile(Context context, Android.Net.Uri src, string dest)
        {
            using var input = context.ContentResolver.OpenInputStream(src);
            if (input == null) throw new IOException("OpenInputStream returned null");
            using var output = File.Create(dest);
            input.CopyTo(output);
        }

        internal static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
            }
            var s = sb.ToString().Trim().TrimEnd('.');
            return s.Length == 0 ? "_" : s;
        }
    }
}
