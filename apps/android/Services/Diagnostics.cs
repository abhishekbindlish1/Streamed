using StreamedTv.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StreamedTv.Services
{
    /// <summary>Writes a parallel log file to a pullable location so we can `sdb pull /tmp/streamed-crash.log`
    /// when device dlog is restricted (which is common on Samsung TVs).</summary>
    public static class Diagnostics
    {
        private static readonly object Lock = new object();
        private static string _path;

        public static void Init()
        {
            // /tmp is one of the only directories sdb can pull from on a sandboxed Samsung TV.
            string[] candidates = new[]
            {
                "/tmp/streamed-crash.log",
                SafeDataDir() + "/crash.log",
            };
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                try
                {
                    File.WriteAllText(candidate, "[" + DateTimeOffset.Now.ToString("o") + "] === session start (path=" + candidate + ") ===\n");
                    _path = candidate;
                    AppLog.Info("StreamedTv", "Diagnostics writing to " + candidate);
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Error("StreamedTv", "Diagnostics could not open " + candidate + ": " + ex.Message);
                }
            }
            _path = null;
        }

        private static string SafeDataDir()
        {
            try
            {
                return AppData.DataDirectory;
            }
            catch
            {
                return null;
            }
        }

        public static void Log(string message)
        {
            try
            {
                AppLog.Info("StreamedTv", message);
            }
            catch
            {
            }
            try
            {
                if (_path == null) return;
                lock (Lock)
                {
                    File.AppendAllText(_path, "[" + DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "] " + message + "\n");
                }
            }
            catch
            {
            }
        }

        public static void LogError(string context, Exception ex)
        {
            string body = context + ": " + (ex == null ? "(null)" : ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
            Log("ERROR " + body);
            // Mirror to the crash reporter so explicit error sites
            // (every catch block sprinkled through the auth + profile
            // flow) become shippable telemetry — no extra wiring at
            // each call site.
            try { CrashReporter.Capture(context, ex); } catch { }
        }

        /// <summary>Emit a structured non-crash telemetry event. Same
        /// shipping pipeline as <see cref="LogError"/> (the next launch
        /// POSTs the buffered events to <c>/v1/crash-logs</c>) — used
        /// for things like "stream attempted but video stayed 0×0",
        /// where there's nothing to throw an exception about but we
        /// still want to see the trace from a remote TV.
        ///
        /// Format on the wire: one line per event, key=value pairs.
        /// Spaces in values are replaced with '_' so a downstream
        /// awk/grep pipeline doesn't need to deal with quoting.
        ///
        /// <example>
        ///   Diagnostics.Track("stream_attempt",
        ///       new Dictionary&lt;string,string&gt; { ["url"] = streamUrl });
        ///   Diagnostics.Track("video_state",
        ///       new Dictionary&lt;string,string&gt; { ["w"] = "0", ["h"] = "0" });
        /// </example></summary>
        public static void Track(string ev, IDictionary<string, string> props = null)
        {
            if (string.IsNullOrEmpty(ev)) return;
            try
            {
                StringBuilder sb = new StringBuilder(64);
                sb.Append("TRACK ").Append(ev);
                if (props != null)
                {
                    foreach (KeyValuePair<string, string> kv in props)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        string v = kv.Value ?? "";
                        // Single-line, no whitespace in values (replaced
                        // with '_') so each event remains one greppable
                        // line in the GCS blob.
                        v = v.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');
                        sb.Append(' ').Append(kv.Key).Append('=').Append(v);
                    }
                }
                string line = sb.ToString();
                Log(line);
                try { CrashReporter.CaptureTelemetry(line); } catch { }
            }
            catch { /* telemetry must never crash */ }
        }
    }
}
