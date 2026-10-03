using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Android.OS;
using Moe.Shizuku.Server;
using Rikka.Shizuku;

namespace AssetStudioMobile
{
    public enum ShizukuState
    {
        NotInstalled,
        NotRunning,
        PermissionDenied,
        Ready,
    }

    /// <summary>
    /// Runs shell-level commands through Shizuku.
    ///
    /// This exists for one reason: /sdcard/Android/data/&lt;other package&gt;/ cannot be reached by
    /// SAF (ACTION_OPEN_DOCUMENT_TREE refuses Android/data on Android 11+) nor by
    /// MANAGE_EXTERNAL_STORAGE (the platform documents that other apps' app-specific directories
    /// stay out of reach). Shizuku's service runs as uid 2000 (shell), which does have
    /// ext_data_rw, so it can read those paths.
    ///
    /// It is still not a privilege escalation of our own process: commands run in a separate
    /// shell process, so files must be staged through a copy rather than opened in place.
    ///
    /// Shizuku 13.x made Shizuku.newProcess() private, so the service is reached through
    /// IShizukuService.NewProcess via ShizukuBinderWrapper, which is the same call one layer
    /// down and is public API.
    /// </summary>
    public static class ShizukuBridge
    {
        private const string ManagerPkg = "moe.shizuku.privileged.api";

        private static IShizukuService _service;

        public static ShizukuState State
        {
            get
            {
                try
                {
                    if (!Shizuku.PingBinder()) return ShizukuState.NotRunning;
                    if (Shizuku.CheckSelfPermission() == 0) return ShizukuState.Ready;
                    return ShizukuState.PermissionDenied;
                }
                catch
                {
                    return ShizukuState.NotInstalled;
                }
            }
        }

        public static string Describe()
        {
            switch (State)
            {
                case ShizukuState.Ready:
                    return $"Shizuku: ready (uid {SafeUid()})";
                case ShizukuState.PermissionDenied:
                    return "Shizuku: running, permission not granted to this app";
                case ShizukuState.NotRunning:
                    return "Shizuku: installed but not running";
                default:
                    return "Shizuku: not installed";
            }
        }

        private static string SafeUid()
        {
            try { return Shizuku.Uid.ToString(); } catch { return "?"; }
        }

        public static void RequestPermission()
        {
            try { Shizuku.RequestPermission(0); }
            catch (Exception ex) { Android.Util.Log.Warn("ShizukuBridge", "requestPermission: " + ex.Message); }
        }

        private static IShizukuService GetService()
        {
            if (_service != null) return _service;
            if (!Shizuku.PingBinder()) throw new InvalidOperationException("Shizuku is not running");

            // ShizukuBinderWrapper substitutes the local binder implementation when the service is
            // reachable in-process; AsInterface on the raw binder would fail across processes.
            var wrapped = new ShizukuBinderWrapper(Shizuku.Binder);
            _service = IShizukuService.Stub.AsInterface(wrapped);
            if (_service == null) throw new InvalidOperationException("could not obtain IShizukuService");
            return _service;
        }

        /// <summary>
        /// Runs a command and returns its stdout. stderr is folded in when the command fails so the
        /// caller gets something actionable instead of an empty string.
        /// </summary>
        public static string Run(string command, string workingDir = null, int timeoutMs = 60000)
        {
            var stdout = Encoding.UTF8.GetString(RunRaw(command, workingDir, out var stderr, timeoutMs));
            if (stdout.Length == 0 && stderr.Length > 0) return stderr;
            return stdout;
        }

        /// <summary>Runs a command, capturing the raw stdout bytes. Used for binary payloads.</summary>
        public static byte[] RunBinary(string command, string workingDir = null, int timeoutMs = 600000)
        {
            var stdout = RunRaw(command, workingDir, out _, timeoutMs, binary: true);
            return stdout;
        }

        /// <summary>Exit code of the last Run, for callers that need to distinguish "no output" from failure.</summary>
        public static int LastExitCode { get; private set; } = -1;

        /// <summary>Failure detail from the last Run, when the bookkeeping itself failed.</summary>
        public static Exception LastError { get; private set; }

        private static byte[] RunRaw(string command, string workingDir, out string stderr, int timeoutMs, bool binary = false)
        {
            stderr = string.Empty;
            LastExitCode = -1;
            LastError = null;

            var service = GetService();
            var remote = service.NewProcess(new[] { "/system/bin/sh", "-c", command }, null, workingDir);
            if (remote == null) throw new InvalidOperationException("NewProcess returned null");

            using (remote)
            {
                var stdoutBytes = ReadAll(remote.InputStream);
                var stderrBytes = ReadAll(remote.ErrorStream);

                stderr = Encoding.UTF8.GetString(stderrBytes);

                try
                {
                    // The unit is the TimeUnit enum NAME, not an abbreviation: passing "ms" throws
                    // IllegalArgumentException: No enum constant java.util.concurrent.TimeUnit.ms.
                    if (!remote.WaitForTimeout(timeoutMs, "MILLISECONDS"))
                    {
                        remote.Destroy();
                        throw new TimeoutException(
                            $"command timed out after {timeoutMs} ms: {command}" +
                            (stderr.Length > 0 ? $"\n{stderr}" : ""));
                    }
                    LastExitCode = remote.ExitValue();
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Keep whatever output arrived; the command may well have succeeded and only the
                    // bookkeeping call failed. Surface the reason in LastError for the caller.
                    LastError = ex;
                    LastExitCode = -1;
                }

                return stdoutBytes;
            }
        }

        private static byte[] ReadAll(ParcelFileDescriptor pfd)
        {
            if (pfd == null) return Array.Empty<byte>();

            using (pfd)
            {
                // ParcelFileDescriptor.AutoCloseInputStream derives from java.io.FileInputStream,
                // which this binding does not surface as System.IO.Stream, so read through the
                // Java InputStream API directly rather than relying on the conversion.
                var input = new ParcelFileDescriptor.AutoCloseInputStream(pfd);
                using var ms = new MemoryStream();
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Copies a directory tree into app-private staging using shell, then returns the staged
        /// path. This is the only way to get at Android/data content into AssetStudio's
        /// path-based loader.
        /// </summary>
        public static string StageDirectory(
            string sourceDir,
            string stagingRoot,
            Action<string> log,
            Action<int, int> progress,
            CancellationTokenSource cancel = null)
        {
            var src = ShellQuote(sourceDir);
            var dst = ShellQuote(stagingRoot);

            // Flatten to a single folder named after the source; AssetStudio then scans it normally.
            var leaf = Path.GetFileName(sourceDir.TrimEnd('/'));
            if (string.IsNullOrEmpty(leaf)) leaf = "staged";
            var target = Path.Combine(stagingRoot, leaf);
            Directory.CreateDirectory(stagingRoot);
            if (Directory.Exists(target)) Directory.Delete(target, true);

            log?.Invoke($"shizuku: copying {sourceDir} -> {target}");

            var sw = Stopwatch.StartNew();
            // cp -r preserves the tree; -f avoids interactive prompts on odd files.
            RunRaw($"cp -rf {src} {dst}", null, out var copyErr, 30 * 60 * 1000);
            sw.Stop();

            if (LastExitCode != 0 || !Directory.Exists(target))
            {
                var why = new StringBuilder();
                why.Append($"shizuku copy failed (exit {LastExitCode}) for {sourceDir}");
                if (LastError != null) why.Append(": ").Append(LastError.GetType().Name).Append(": ").Append(LastError.Message);
                if (!string.IsNullOrWhiteSpace(copyErr)) why.Append(" | stderr: ").Append(copyErr.Trim());
                throw new IOException(why.ToString());
            }

            var count = Directory.GetFiles(target, "*.*", SearchOption.AllDirectories).Length;
            log?.Invoke($"shizuku: staged {count} file(s) in {sw.Elapsed.TotalSeconds:F1}s");
            progress?.Invoke(count, count);
            return target;
        }

        /// <summary>Lists immediate subdirectories, so the app can offer a game package picker.</summary>
        public static List<string> ListDirectories(string parent)
        {
            var result = new List<string>();
            var outp = Run($"ls -1 {ShellQuote(parent)} 2>/dev/null");
            foreach (var line in outp.Split('\n'))
            {
                var n = line.Trim();
                if (n.Length == 0) continue;
                if (n.StartsWith("ls:", StringComparison.Ordinal)) continue; // error text
                result.Add(n);
            }
            return result;
        }

        /// <summary>Single-quotes a path for /system/bin/sh, escaping any embedded quote.</summary>
        internal static string ShellQuote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "''";
            return "'" + s.Replace("'", "'\\''") + "'";
        }
    }
}
