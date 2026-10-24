using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace StreamedTv.Services
{
    /// <summary>
    /// Remote-config client. Asks the backend (<c>GET /v1/config</c>) for
    /// the streamed.pk base URL the app should use plus any feature flags,
    /// then applies the values to <see cref="StreamedEndpoints.BaseUrl"/>
    /// and exposes the flags via <see cref="GetFlag"/>.
    ///
    /// Wire-up flow (in <c>Program.OnCreate</c>):
    ///
    ///   1. Construct (cheap, no I/O).
    ///   2. Call <see cref="LoadFromDiskAndApply"/> SYNCHRONOUSLY — restores
    ///      the last successful response from a previous launch so we have
    ///      the right BaseUrl on the very first catalog call this session.
    ///   3. Fire <see cref="StartBackgroundRefresh"/> — issues a fresh
    ///      <c>/v1/config</c> fetch in the background, then re-polls every
    ///      <see cref="DefaultRefreshInterval"/>.
    ///
    /// We do NOT touch <c>Application.Current.DirectoryInfo.Data</c> in the
    /// constructor or any field initializer — that property returns null
    /// until after <c>base.OnCreate</c>. Hence the explicit
    /// <see cref="LoadFromDiskAndApply"/> entry point.
    ///
    /// Threading: <see cref="Current"/>, <see cref="GetFlag"/> are safe to
    /// read from any thread (the snapshot reference is replaced atomically).
    /// All disk + HTTP work happens off the NUI main thread; the only
    /// continuation that touches a UI-visible thing is the
    /// <c>StreamedEndpoints.BaseUrl</c> assignment, which is a thread-safe
    /// static setter.
    /// </summary>
    public sealed class AppConfigService
    {
        public const string FileName = "app-config.json";
        public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(30);

        private readonly string _backendBaseUrl;
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;
        private AppConfig _current;
        private bool _backgroundStarted;

        /// <summary>The currently applied config snapshot. Never null after
        /// <see cref="LoadFromDiskAndApply"/> has been called — falls back
        /// to a synthesized default with the compile-time streamed.pk URL.
        /// Reads are lock-free; writers replace the reference atomically.</summary>
        public AppConfig Current { get { return Volatile.Read(ref _current); } }

        public AppConfigService(string backendBaseUrl, HttpClient http = null)
        {
            _backendBaseUrl = (backendBaseUrl ?? "").TrimEnd('/');
            _http = http ?? CreateDefaultHttpClient();
            _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            // Pre-seed Current with a hard-coded default so callers that
            // read it BEFORE LoadFromDiskAndApply (e.g. during the first
            // BuildScene tick) still get a meaningful value rather than
            // a null deref.
            _current = AppConfig.Default;
        }

        /// <summary>
        /// Loads the last successfully persisted config from disk and
        /// applies it. MUST be called from <c>Program.OnCreate</c> AFTER
        /// <c>base.OnCreate</c> — calls Tizen's <c>DirectoryInfo.Data</c>
        /// which is null before then.
        ///
        /// Silent on missing/corrupt files: this is a best-effort cache,
        /// not the source of truth. The background fetch will populate
        /// Current with the live value within seconds either way.
        /// </summary>
        public void LoadFromDiskAndApply()
        {
            try
            {
                string path = ResolvePath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Apply(AppConfig.Default);
                    return;
                }
                string raw = File.ReadAllText(path);
                AppConfig cfg = JsonSerializer.Deserialize<AppConfig>(raw, _json);
                if (cfg == null || string.IsNullOrEmpty(cfg.StreamedBaseUrl))
                {
                    Apply(AppConfig.Default);
                    return;
                }
                cfg.Source = "disk-cache";
                Apply(cfg);
            }
            catch (Exception ex)
            {
                try { Tizen.Log.Info("StreamedTv", "AppConfig load failed: " + ex.Message); } catch { }
                Apply(AppConfig.Default);
            }
        }

        /// <summary>
        /// Starts the background fetch loop: one immediate fetch, then
        /// re-poll every <paramref name="interval"/> (default 30 min).
        /// Idempotent — calling twice is a no-op. Safe to call from
        /// OnCreate; runs entirely off the main thread.
        /// </summary>
        public void StartBackgroundRefresh(CancellationToken ct, TimeSpan? interval = null)
        {
            if (_backgroundStarted) return;
            _backgroundStarted = true;
            TimeSpan wait = interval ?? DefaultRefreshInterval;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await FetchAndApplyOnceAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        try { Tizen.Log.Info("StreamedTv", "AppConfig fetch loop error: " + ex.Message); } catch { }
                    }
                    try
                    {
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, ct);
        }

        /// <summary>
        /// Performs one HTTP fetch from <c>{backend}/v1/config</c>,
        /// applies the result, and persists it to disk. Public so tests
        /// (and the app's "force refresh" diagnostic) can drive a single
        /// fetch without spinning up the loop.
        /// </summary>
        public async Task FetchAndApplyOnceAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_backendBaseUrl)) return;
            string url = _backendBaseUrl + "/v1/config";
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            AppConfig cfg = JsonSerializer.Deserialize<AppConfig>(body, _json);
            if (cfg == null || string.IsNullOrEmpty(cfg.StreamedBaseUrl)) return;
            Apply(cfg);
            TryPersist(cfg);
        }

        /// <summary>
        /// Returns the boolean feature flag value, falling back to
        /// <paramref name="defaultValue"/> when the key is absent or its
        /// value isn't a bool. Convenience for the most common use case.
        /// </summary>
        public bool GetFlag(string key, bool defaultValue = false)
        {
            AppConfig snap = Current;
            if (snap == null || snap.FeatureFlags == null) return defaultValue;
            if (!snap.FeatureFlags.TryGetValue(key, out JsonElement el)) return defaultValue;
            try
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
                if (el.ValueKind == JsonValueKind.String)
                {
                    string s = el.GetString();
                    if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                }
                if (el.ValueKind == JsonValueKind.Number)
                {
                    if (el.TryGetInt32(out int n)) return n != 0;
                }
            }
            catch
            {
                // Ignore — treat malformed values as "use default".
            }
            return defaultValue;
        }

        /// <summary>Returns a string flag value, or default when missing/non-string.</summary>
        public string GetString(string key, string defaultValue = null)
        {
            AppConfig snap = Current;
            if (snap == null || snap.FeatureFlags == null) return defaultValue;
            if (!snap.FeatureFlags.TryGetValue(key, out JsonElement el)) return defaultValue;
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            return defaultValue;
        }

        private void Apply(AppConfig cfg)
        {
            if (cfg == null) return;
            Volatile.Write(ref _current, cfg);
            // The whole point of this service. StreamedEndpoints.BaseUrl
            // is a public static — every subsequent endpoint Uri build
            // picks up the change. In-flight requests don't get retroed
            // (HttpClient already constructed the URL), but the next
            // catalog poll uses the new origin.
            if (!string.IsNullOrEmpty(cfg.StreamedBaseUrl))
            {
                StreamedEndpoints.BaseUrl = cfg.StreamedBaseUrl;
            }
        }

        private void TryPersist(AppConfig cfg)
        {
            try
            {
                string path = ResolvePath();
                if (string.IsNullOrEmpty(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string raw = JsonSerializer.Serialize(cfg);
                File.WriteAllText(path, raw);
            }
            catch (Exception ex)
            {
                try { Tizen.Log.Info("StreamedTv", "AppConfig persist failed: " + ex.Message); } catch { }
            }
        }

        private static string ResolvePath()
        {
            try
            {
                string dataDir = Tizen.Applications.Application.Current?.DirectoryInfo?.Data;
                if (string.IsNullOrEmpty(dataDir)) return null;
                return Path.Combine(dataDir, FileName);
            }
            catch
            {
                return null;
            }
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8),
            };
        }
    }

    public sealed class AppConfig
    {
        [JsonPropertyName("streamedBaseUrl")] public string StreamedBaseUrl { get; set; }
        [JsonPropertyName("featureFlags")] public Dictionary<string, JsonElement> FeatureFlags { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; }
        [JsonPropertyName("fetchedAt")] public string FetchedAt { get; set; }

        public static AppConfig Default
        {
            get
            {
                return new AppConfig
                {
                    StreamedBaseUrl = StreamedEndpoints.DefaultBaseUrl,
                    FeatureFlags = new Dictionary<string, JsonElement>(),
                    Source = "compile-default",
                    FetchedAt = "",
                };
            }
        }
    }
}
