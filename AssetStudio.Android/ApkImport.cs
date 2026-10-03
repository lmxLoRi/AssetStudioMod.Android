using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace AssetStudioMobile
{
    /// <summary>
    /// Brings an installed app's APK, or a picked .apk, somewhere the loader can read it.
    ///
    /// AssetStudio already knows what a ZIP is: FileReader.CheckFileType sees the PK signature and
    /// routes it to LoadZipFile, and the scan's magic-number sniff accepts that same signature, so
    /// an APK dropped in as a candidate is loaded with nothing else changed. Everything here is
    /// about getting the bytes into a readable directory.
    /// </summary>
    internal static class ApkImport
    {
        internal sealed class InstalledApp
        {
            public string PackageName;
            public string Label;
            public string[] Apks = Array.Empty<string>();
            public bool System;

            public override string ToString() => $"{Label}  ({PackageName})";
        }

        /// <summary>
        /// Every installed app that has an APK on disk. QUERY_ALL_PACKAGES is what makes the others
        /// visible; without it this only sees this app and whatever the platform whitelists.
        /// </summary>
        public static List<InstalledApp> ListInstalled(Context context, Action<string> log)
        {
            var result = new List<InstalledApp>();
            var pm = context?.PackageManager;
            if (pm == null) return result;

            IList<ApplicationInfo> apps;
            try
            {
                apps = Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                    ? pm.GetInstalledApplications(PackageInfoFlags.MatchAll)
                    : pm.GetInstalledApplications(0);
            }
            catch (Exception ex)
            {
                log?.Invoke($"cannot list packages: {ex.GetType().Name}: {ex.Message}");
                return result;
            }

            foreach (var app in apps)
            {
                if (app == null || string.IsNullOrEmpty(app.SourceDir)) continue;

                var paths = new List<string> { app.SourceDir };
                var splits = app.SplitSourceDirs;
                if (splits != null) paths.AddRange(splits);

                string label;
                try { label = app.LoadLabel(pm)?.ToString() ?? app.PackageName; }
                catch { label = app.PackageName; }

                result.Add(new InstalledApp
                {
                    PackageName = app.PackageName,
                    Label = label,
                    Apks = paths.ToArray(),
                    System = (app.Flags & ApplicationInfoFlags.System) != 0,
                });
            }

            result.Sort((a, b) =>
            {
                if (a.System != b.System) return a.System ? 1 : -1; // user apps first
                return string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase);
            });

            log?.Invoke($"found {result.Count} installed app(s)");
            return result;
        }

        /// <summary>
        /// Copies an app's APKs into <paramref name="stagingRoot"/>/apk/&lt;package&gt; and returns that
        /// directory, or null if nothing could be read.
        ///
        /// Tried directly first. /data/app is not shared storage, so this may well fail -- an APK
        /// extractor app relies on it working, but that is a claim worth testing rather than
        /// assuming, and the failure is reported with its reason. Shizuku is the fallback: it runs
        /// as shell, which can read /data/app, and it already has a documented route for getting
        /// bytes out of somewhere this app cannot reach.
        /// </summary>
        public static string StageApks(Context context, InstalledApp app, string stagingRoot,
                                       Action<string> log)
        {
            var dest = Path.Combine(stagingRoot, "apk", Sanitize(app.PackageName));
            Directory.CreateDirectory(dest);

            var copied = 0;
            foreach (var src in app.Apks)
            {
                var target = Path.Combine(dest, Path.GetFileName(src));
                if (File.Exists(target) && new FileInfo(target).Length > 0)
                {
                    copied++;
                    continue;
                }

                if (TryCopyDirect(src, target, out var why))
                {
                    copied++;
                    continue;
                }

                log?.Invoke($"direct read failed ({why}); trying Shizuku");

                if (ShizukuBridge.State != ShizukuState.Ready)
                {
                    log?.Invoke($"ERROR: cannot read {src}, and Shizuku is not usable ({ShizukuBridge.State})");
                    continue;
                }

                ShizukuBridge.Run($"cp -f {ShizukuBridge.ShellQuote(src)} {ShizukuBridge.ShellQuote(target)} 2>&1");
                if (File.Exists(target) && new FileInfo(target).Length > 0) copied++;
                else log?.Invoke($"ERROR: shizuku could not copy {Path.GetFileName(src)}");
            }

            if (copied == 0) return null;

            var total = 0L;
            foreach (var f in Directory.GetFiles(dest)) total += new FileInfo(f).Length;
            log?.Invoke($"staged {copied} APK file(s), {total / 1048576} MB, into {dest}");
            return dest;
        }

        /// <summary>
        /// Copies one APK out of a picked document URI. Unlike the Shizuku route this reads through
        /// the provider, which is slower, but it needs no permission at all and the picked file is
        /// one file rather than a tree.
        /// </summary>
        public static string StagePickedApk(Context context, Android.Net.Uri uri, string stagingRoot,
                                            Action<string> log)
        {
            var name = Sanitize(ImportUtils.LeafNameOf(uri) ?? "picked.apk");
            var dest = Path.Combine(stagingRoot, "apk", "picked");
            Directory.CreateDirectory(dest);
            var target = Path.Combine(dest, name);

            try
            {
                using var input = context.ContentResolver.OpenInputStream(uri);
                if (input == null) { log?.Invoke("ERROR: the picker returned no stream"); return null; }
                using var output = File.Create(target);
                input.CopyTo(output);
            }
            catch (Exception ex)
            {
                log?.Invoke($"ERROR: cannot copy the picked APK: {ex.GetType().Name}: {ex.Message}");
                return null;
            }

            log?.Invoke($"staged {name}, {new FileInfo(target).Length / 1048576} MB");
            return dest;
        }

        private static bool TryCopyDirect(string src, string dest, out string why)
        {
            why = null;
            try
            {
                using var input = File.OpenRead(src);
                using var output = File.Create(dest);
                input.CopyTo(output);
                return true;
            }
            catch (Exception ex)
            {
                why = $"{ex.GetType().Name}: {ex.Message}";
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                return false;
            }
        }

        internal static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "app";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
            }
            var s = sb.ToString().Trim().TrimEnd('.');
            return s.Length == 0 ? "app" : s;
        }
    }
}
