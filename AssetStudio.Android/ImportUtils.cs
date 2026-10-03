using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Database;
using Android.Provider;

namespace AssetStudioMobile
{
    /// <summary>
    /// Copies a SAF tree URI into app storage.
    ///
    /// AssetStudio addresses every file by real filesystem path and
    /// Directory.GetFiles(..., SearchOption.AllDirectories) has no content:// equivalent, so a
    /// picked document tree has to be materialised on disk before the loader can see it.
    ///
    /// This is the slow path by nature: every byte crosses a binder into the document provider's
    /// user-space process, unlike the Shizuku route where cp runs in the kernel. It used to be much
    /// slower than that, for reasons that had nothing to do with the bytes -- see Children().
    ///
    /// Note the binding names: the column constants live on DocumentsContract.Document
    /// (ColumnDocumentId / ColumnMimeType / ColumnDisplayName), not on DocumentsContract itself.
    /// </summary>
    public static class ImportUtils
    {
        /// <summary>One entry straight out of a directory listing.</summary>
        private readonly struct Child
        {
            public readonly Android.Net.Uri Uri;
            public readonly string Name;
            public readonly bool IsDirectory;

            public Child(Android.Net.Uri uri, string name, bool isDirectory)
            {
                Uri = uri;
                Name = name;
                IsDirectory = isDirectory;
            }
        }

        /// <summary>How many files are read from the provider at once. The copies are independent.</summary>
        private static readonly int CopyThreads = Math.Max(2, Environment.ProcessorCount / 2);

        public static int CopyTree(
            Context context,
            Android.Net.Uri treeUri,
            string destRoot,
            Action<string> log,
            Action<int, int> progress)
        {
            var clock = Stopwatch.StartNew();

            // GetTreeDocumentId, not GetDocumentId: this is a tree URI
            // (.../tree/primary%3AAndroid%2Fdata%2F...) and GetDocumentId only accepts a document
            // one, so it throws IllegalArgumentException("Invalid URI") before anything is copied.
            Android.Net.Uri rootDoc;
            try
            {
                rootDoc = DocumentsContract.BuildDocumentUriUsingTree(
                    treeUri, DocumentsContract.GetTreeDocumentId(treeUri));
            }
            catch (Exception ex)
            {
                log?.Invoke($"ERROR: cannot read the picked folder ({ex.GetType().Name}: {ex.Message})");
                return 0;
            }

            Directory.CreateDirectory(destRoot);

            // Phase 1: walk, creating the destination directories and collecting the files. One
            // provider query per directory, and each entry's name and type come out of that same
            // cursor.
            var files = new List<KeyValuePair<Android.Net.Uri, string>>();
            var dirs = new Stack<KeyValuePair<Android.Net.Uri, string>>();
            dirs.Push(new KeyValuePair<Android.Net.Uri, string>(rootDoc, destRoot));

            var seen = 0;
            while (dirs.Count > 0)
            {
                var dir = dirs.Pop();
                foreach (var child in Children(context, dir.Key, log))
                {
                    var name = Sanitize(child.Name ?? LeafName(child.Uri) ?? "_");
                    var dest = Path.Combine(dir.Value, name);

                    if (child.IsDirectory)
                    {
                        try
                        {
                            Directory.CreateDirectory(dest);
                            dirs.Push(new KeyValuePair<Android.Net.Uri, string>(child.Uri, dest));
                        }
                        catch (Exception ex)
                        {
                            log?.Invoke($"skip dir {name}: {ex.Message}");
                            continue;
                        }
                    }
                    else
                    {
                        files.Add(new KeyValuePair<Android.Net.Uri, string>(child.Uri, dest));
                    }

                    progress?.Invoke(0, ++seen);
                }
            }

            // Phase 2: copy. Serialising this was the other half of the cost -- every read is a
            // binder call, so the waiting is per call and there is nothing to serialise about it.
            var copied = 0;
            var bytes = 0L;
            var reported = 0;

            Parallel.ForEach(files,
                new ParallelOptions { MaxDegreeOfParallelism = CopyThreads },
                file =>
                {
                    try
                    {
                        var written = CopyFile(context, file.Key, file.Value);
                        Interlocked.Increment(ref copied);
                        Interlocked.Add(ref bytes, written);
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"skip {Path.GetFileName(file.Value)}: {ex.GetType().Name}: {ex.Message}");
                    }

                    var done = Interlocked.Increment(ref reported);
                    if (done % 200 == 0) log?.Invoke($"copied {done}/{files.Count} file(s)...");
                    progress?.Invoke(Volatile.Read(ref copied), files.Count);
                });

            var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            var mb = bytes / 1048576.0;
            log?.Invoke($"copied {copied}/{files.Count} file(s), {mb:F0} MB in {seconds:F1}s " +
                        $"({copied / seconds:F0} files/s, {mb / seconds:F1} MB/s, {CopyThreads} threads)");

            // The caller scans the destination, so it needs the layout to exist even when empty.
            return copied;
        }

        /// <summary>
        /// Lists one directory, reading the document id, mime type and display name from a single
        /// cursor.
        ///
        /// The previous version asked the provider three times per entry: once here for the id, and
        /// then GetType() and another Query() per child to work out what it was and what it was
        /// called -- with the mime type and display name already sitting unread in this cursor.
        /// That is two extra binder round trips for every one of the ~10k files in a game cache.
        /// </summary>
        private static List<Child> Children(Context context, Android.Net.Uri dirUri, Action<string> log)
        {
            var result = new List<Child>();

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
                log?.Invoke($"cannot list {LeafName(dirUri)}: {ex.Message}");
                return result;
            }

            if (cursor == null) return result;

            using (cursor)
            {
                var idIdx = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDocumentId);
                var mimeIdx = cursor.GetColumnIndex(DocumentsContract.Document.ColumnMimeType);
                var nameIdx = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
                if (idIdx < 0) return result;

                while (cursor.MoveToNext())
                {
                    var docId = cursor.GetString(idIdx);
                    if (string.IsNullOrEmpty(docId)) continue;

                    try
                    {
                        var uri = DocumentsContract.BuildDocumentUriUsingTree(dirUri, docId);
                        var mime = mimeIdx >= 0 ? cursor.GetString(mimeIdx) : null;
                        var name = nameIdx >= 0 ? cursor.GetString(nameIdx) : null;

                        var isDir = string.Equals(mime, DocumentsContract.Document.MimeTypeDir,
                                                  StringComparison.OrdinalIgnoreCase);
                        result.Add(new Child(uri, name, isDir));
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"bad document id '{docId}': {ex.Message}");
                    }
                }
            }

            return result;
        }

        /// <summary>The last path segment of a document URI's id, for naming a copy of it.</summary>
        internal static string LeafNameOf(Android.Net.Uri uri) => LeafName(uri);

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

        /// <summary>Copies one document out. Returns the number of bytes written.</summary>
        private static long CopyFile(Context context, Android.Net.Uri src, string dest)
        {
            using var input = context.ContentResolver.OpenInputStream(src);
            if (input == null) throw new IOException("OpenInputStream returned null");
            using var output = File.Create(dest);
            input.CopyTo(output);
            return output.Length;
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
