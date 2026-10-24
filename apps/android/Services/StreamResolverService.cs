using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace StreamedTv.Services
{
    /// <summary>
    /// Resolves wrapper-style stream URLs (e.g. embedsports.top) to
    /// their inner iframe target so the WebView can load the actual
    /// player as the top frame instead of inheriting the wrapper's
    /// cross-origin iframe.
    ///
    /// Why this exists: Tizen's HW VideoHole compositor can punch
    /// through the chromium-efl layer tree to a hardware video plane
    /// only when the &lt;video&gt; element is in the TOP frame.
    /// Cross-origin iframes are a separate composited layer the HW
    /// plane can't reach — observable as "audio plays, video stays
    /// black." Bypassing the wrapper hands the inner player URL
    /// directly to the WebView, putting the &lt;video&gt; back in
    /// the top frame.
    ///
    /// Behaviour contract:
    ///   * <see cref="ResolveAsync"/> NEVER throws and NEVER returns
    ///     null/empty when given a non-empty input — failure modes
    ///     return the input unchanged so the caller can pass the
    ///     result straight through to <c>WebView.Url</c>.
    ///   * Known-wrapper detection is local (host substring match)
    ///     to avoid a backend round-trip for streams that don't need
    ///     resolution. Add hosts here AND on the backend's
    ///     allowedWrapperHosts when introducing new wrappers.
    ///   * Bounded timeout via the caller's CancellationToken;
    ///     resolver is opportunistic, not load-bearing.
    /// </summary>
    public sealed class StreamResolverService
    {
        private readonly BackendAuthService _auth;
        private readonly JsonSerializerOptions _json;

        public StreamResolverService(BackendAuthService auth)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        }

        /// <summary>True iff this URL is for a wrapper host known to
        /// hide the actual player behind a cross-origin iframe. Used
        /// by the player to skip the resolver round-trip for streams
        /// that don't need it.</summary>
        public static bool IsKnownWrapper(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            // Substring match keeps this cheap and tolerant of subdomains.
            // Mirrors backend's allowedWrapperHosts in spirit; if we
            // grow that list, mirror it here too.
            return url.IndexOf("embedsports.top", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Best-effort: returns the resolved inner URL when
        /// extraction succeeds, the original URL on any failure
        /// (auth missing, network error, JSON parse error, backend
        /// returns "skip-*" strategy). Caller can blindly use the
        /// return value.</summary>
        public async Task<string> ResolveAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (!_auth.IsAuthenticated) return url;
            if (!IsKnownWrapper(url)) return url;

            try
            {
                HttpClient http = _auth.SharedHttpClient();
                string requestUrl = _auth.BaseUrl + "/v1/stream-resolve?url=" + Uri.EscapeDataString(url);
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _auth.CurrentJwt);
                using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(true);
                if (!resp.IsSuccessStatusCode) return url;
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
                ResolveResponse parsed = JsonSerializer.Deserialize<ResolveResponse>(body, _json);
                string resolved = parsed?.Resolved;
                if (string.IsNullOrEmpty(resolved)) return url;
                return resolved;
            }
            catch
            {
                // Resolver is opportunistic — never let a failure
                // prevent the user from at least attempting to play
                // the original URL.
                return url;
            }
        }

        /// <summary>Same as <see cref="ResolveAsync"/> but also
        /// surfaces the strategy + telemetry-friendly fields so the
        /// caller can record what the resolver decided. Use this when
        /// you want the trace to show whether resolution happened
        /// vs was skipped vs failed.</summary>
        public async Task<ResolveOutcome> ResolveDetailedAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url))
                return new ResolveOutcome { Url = url, Strategy = "skip-empty-input" };
            if (!_auth.IsAuthenticated)
                return new ResolveOutcome { Url = url, Strategy = "skip-not-authed" };
            if (!IsKnownWrapper(url))
                return new ResolveOutcome { Url = url, Strategy = "skip-not-wrapper-local" };

            try
            {
                HttpClient http = _auth.SharedHttpClient();
                string requestUrl = _auth.BaseUrl + "/v1/stream-resolve?url=" + Uri.EscapeDataString(url);
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _auth.CurrentJwt);
                using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(true);
                if (!resp.IsSuccessStatusCode)
                {
                    return new ResolveOutcome
                    {
                        Url = url,
                        Strategy = "skip-http-" + (int)resp.StatusCode,
                    };
                }
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
                ResolveResponse parsed = JsonSerializer.Deserialize<ResolveResponse>(body, _json);
                if (parsed == null || string.IsNullOrEmpty(parsed.Resolved))
                {
                    return new ResolveOutcome { Url = url, Strategy = "skip-empty-response" };
                }
                return new ResolveOutcome
                {
                    Url = parsed.Resolved,
                    Strategy = parsed.Strategy ?? "unknown",
                    Target = parsed.Target,
                };
            }
            catch (Exception ex)
            {
                return new ResolveOutcome
                {
                    Url = url,
                    Strategy = "skip-exception",
                    Error = ex.GetType().Name,
                };
            }
        }

        public sealed class ResolveOutcome
        {
            public string Url { get; set; }
            public string Strategy { get; set; }
            public string Target { get; set; }
            public string Error { get; set; }
            public bool Changed(string original)
            {
                return !string.IsNullOrEmpty(Url) && !string.Equals(Url, original, StringComparison.Ordinal);
            }
        }

        private sealed class ResolveResponse
        {
            [JsonPropertyName("resolved")] public string Resolved { get; set; }
            [JsonPropertyName("strategy")] public string Strategy { get; set; }
            [JsonPropertyName("target")] public string Target { get; set; }
        }
    }
}
