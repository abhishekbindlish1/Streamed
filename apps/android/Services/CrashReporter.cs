using StreamedTv.Platform;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamedTv.Services
{
    /// <summary>
    /// Best-effort crash + error reporter. Captures unhandled exceptions
    /// (AppDomain + Task) and explicit <see cref="Diagnostics.LogError"/>
    /// calls to a local file at <c>{data}/pending-crashes.log</c>. On the
    /// NEXT successful launch we POST that file to the backend
    /// (<c>POST /v1/crash-logs</c>) and delete it on success — same
    /// "post-mortem on the next run" pattern Crashlytics + most
    /// crash-reporters use.
    ///
    /// Why this matters: Tizen TVs are remote devices we can't dlog on
    /// demand. Without a ship-on-restart channel we'd be stuck asking
    /// the user to plug in a USB cable + run sdb every time something
    /// crashes. With it, every survivable crash becomes a GCS object
    /// we can `gsutil cat` from a laptop.
    ///
    /// Storage layout (local):
    ///   {data}/pending-crashes.log    — append-only text log. Each
    ///                                   entry separated by a banner
    ///                                   line so multiple crashes
    ///                                   between launches batch into
    ///                                   one upload.
    ///
    /// Format (plain text, NOT JSON — easier to read in `gsutil cat`):
    ///   === CRASH 2026-05-09T14:32:10Z ===
    ///   Context: OnLoginCompleted
    ///   App: 1.13.33
    ///   Type: System.NullReferenceException
    ///   Message: Object reference not set to an instance of an object.
    ///   Stack:
    ///     at Foo.Bar() in Foo.cs:line 42
    ///     ...
    ///
    /// Caveats:
    ///   * If the process is killed mid-write the partial entry stays
    ///     in the file — backend doesn't try to parse it, just stores
    ///     the blob.
    ///   * Async/unobserved task exceptions are best-effort: if Tizen
    ///     dies before the handler runs we lose them. C# unhandled
    ///     exceptions DO fire AppDomain.UnhandledException reliably.
    ///   * Local file capped at <see cref="MaxLocalFileBytes"/>; older
    ///     entries are truncated FIFO so a crash loop can't fill the
    ///     data partition.
    /// </summary>
    public static class CrashReporter
    {
        private const string FileName = "pending-crashes.log";
        private const string LogTag = "StreamedTv";
        // 64KB local cap — generous for hundreds of typical .NET stack
        // traces, small enough that a runaway loop can't OOM the data
        // partition.
        private const int MaxLocalFileBytes = 64 * 1024;
        // Don't ship more than this in a single POST. Backend has its
        // own cap; this keeps the wire round-trip predictable.
        private const int MaxShipBytes = 64 * 1024;

        private static readonly object _writeLock = new object();
        private static bool _initialized;

        /// <summary>Wire AppDomain + TaskScheduler exception handlers.
        /// Idempotent — call from Program.OnCreate. Safe to call
        /// before <see cref="Tizen.Applications.Application.Current"/>
        /// is wired (we just won't have a path to write to until it
        /// is, in which case capture silently no-ops).</summary>
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Exception ex = e.ExceptionObject as Exception;
                    if (ex != null) Capture("AppDomain.UnhandledException", ex);
                };
                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    Capture("TaskScheduler.UnobservedTaskException", e.Exception);
                    e.SetObserved();  // don't crash the process — we've recorded it
                };
            }
            catch (Exception ex)
            {
                try { AppLog.Error(LogTag, "CrashReporter.Init failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>Append a crash entry to the local pending file.
        /// Called automatically from the AppDomain + Task handlers AND
        /// from <see cref="Diagnostics.LogError"/>. Safe to call from
        /// any thread; serializes via a global lock. Silently no-ops
        /// when the data dir isn't resolvable (early bootstrap, no
        /// app context yet).</summary>
        public static void Capture(string context, Exception ex)
        {
            if (ex == null) return;
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                sb.Append("=== CRASH ").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append(" ===\n");
                sb.Append("Context: ").Append(context ?? "").Append('\n');
                sb.Append("App: ").Append(GetAppVersion()).Append('\n');
                sb.Append("Type: ").Append(ex.GetType().FullName).Append('\n');
                sb.Append("Message: ").Append(ex.Message ?? "").Append('\n');
                if (ex.StackTrace != null)
                {
                    sb.Append("Stack:\n").Append(ex.StackTrace).Append('\n');
                }
                else
                {
                    // ex.StackTrace is null for NREs raised from native
                    // callback boundaries (e.g. chromium-efl's
                    // CheckVideoPlayingAsynchronously delegate, NUI
                    // animation completion). ex.ToString() sometimes
                    // carries usable frame info even when StackTrace
                    // doesn't — better than logging nothing.
                    sb.Append("ToString:\n").Append(ex.ToString() ?? "(null)").Append('\n');
                }
                Exception inner = ex.InnerException;
                int innerDepth = 0;
                while (inner != null && innerDepth < 5)
                {
                    sb.Append("--- Inner #").Append(innerDepth).Append(": ").Append(inner.GetType().FullName).Append(" ---\n");
                    sb.Append(inner.Message ?? "").Append('\n');
                    if (inner.StackTrace != null) sb.Append(inner.StackTrace).Append('\n');
                    inner = inner.InnerException;
                    innerDepth++;
                }
                sb.Append('\n');
                AppendCappedToFile(path, sb.ToString());
            }
            catch (Exception inner)
            {
                try { AppLog.Error(LogTag, "CrashReporter.Capture failed: " + inner.Message); } catch { }
            }
        }

        /// <summary>Read the pending crash file (if any) and POST it to
        /// the backend. Deletes the local file on a 2xx response so we
        /// don't double-ship. Bounded timeout so a slow backend doesn't
        /// stall the auth gate.
        ///
        /// Fire-and-forget from the host: failure just leaves the file
        /// for the next launch to retry. Returns silently when there's
        /// nothing to ship or no auth.</summary>
        public static async Task ShipPendingAsync(BackendAuthService auth, CancellationToken ct)
        {
            if (auth == null || !auth.IsAuthenticated) return;
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            byte[] body;
            try
            {
                body = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                try { AppLog.Error(LogTag, "CrashReporter.Ship read failed: " + ex.Message); } catch { }
                return;
            }
            if (body.Length == 0)
            {
                try { File.Delete(path); } catch { }
                return;
            }
            // Truncate from the FRONT if oversized — newest crashes are
            // the ones we want to ship; older entries lose first.
            if (body.Length > MaxShipBytes)
            {
                int keep = MaxShipBytes;
                byte[] tail = new byte[keep];
                Array.Copy(body, body.Length - keep, tail, 0, keep);
                body = tail;
            }

            try
            {
                // Reuse BackendAuthService's HttpClient (same TLS
                // validation callback as the rest of the auth flow).
                // The fresh `new HttpClient(...)` we used here
                // originally produced 401 responses against the
                // /v1/crash-logs endpoint while /v1/profiles using
                // the same JWT worked fine — Tizen's chromium-net
                // stack appears to handle short-lived clients
                // differently for some reason. Reusing the long-lived
                // client matches what BackendProfilesClient does.
                HttpClient http = auth.SharedHttpClient();
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, auth.BaseUrl + "/v1/crash-logs");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.CurrentJwt);
                req.Content = new ByteArrayContent(body);
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                req.Content.Headers.ContentType.CharSet = "utf-8";
                req.Headers.Add("X-App-Version", GetAppVersion());
                using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    try { File.Delete(path); } catch { }
                    try { AppLog.Info(LogTag, "CrashReporter shipped " + body.Length + " bytes"); } catch { }
                }
                else
                {
                    try { AppLog.Warn(LogTag, "CrashReporter ship returned " + (int)resp.StatusCode); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { AppLog.Warn(LogTag, "CrashReporter ship failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>Append a single-line telemetry event to the same
        /// pending file that crashes write to. Called from
        /// <see cref="Diagnostics.Track"/> for non-crash signals
        /// (stream attempts, video state samples, etc.) — these ride
        /// the same ship-on-next-launch pipeline as crash dumps so we
        /// get one channel for everything observable on a remote TV.
        ///
        /// Format: a single line prefixed with "@ " so a server-side
        /// reader can `grep ^@ ` to filter telemetry from the crash
        /// banners. No newlines or spaces are appended — caller
        /// guarantees the line is single-line.</summary>
        public static void CaptureTelemetry(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                AppendCappedToFile(path, "@ " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + line + "\n");
            }
            catch (Exception ex)
            {
                try { AppLog.Error(LogTag, "CrashReporter.CaptureTelemetry failed: " + ex.Message); } catch { }
            }
        }

        // ---- Helpers ---------------------------------------------------------

        private static void AppendCappedToFile(string path, string text)
        {
            lock (_writeLock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(path, text);
                    long len = new FileInfo(path).Length;
                    if (len > MaxLocalFileBytes)
                    {
                        // Truncate from the FRONT — oldest entries lose
                        // first so the freshest crash always wins
                        // shipping priority.
                        byte[] all = File.ReadAllBytes(path);
                        byte[] keep = new byte[MaxLocalFileBytes];
                        Array.Copy(all, all.Length - MaxLocalFileBytes, keep, 0, MaxLocalFileBytes);
                        File.WriteAllBytes(path, keep);
                    }
                }
                catch { /* swallow — crash reporting must never crash */ }
            }
        }

        private static string ResolvePath()
        {
            try
            {
                string dataDir = AppData.DataDirectory;
                if (string.IsNullOrEmpty(dataDir)) return null;
                return Path.Combine(dataDir, FileName);
            }
            catch
            {
                return null;
            }
        }

        private static string GetAppVersion()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly();
                if (asm == null) return "unknown";
                return asm.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
