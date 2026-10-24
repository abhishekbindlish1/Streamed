using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Models;

namespace StreamedTv.Services
{
    /// <summary>
    /// Pre-resolves known wrapper URLs (embedsports.top, pooembed.eu) to a
    /// self-hosted hls.js wrapper page BEFORE handing them to the
    /// <see cref="Views.PlayerWebView"/>. The wrapper pages have been
    /// native-crashing chromium-efl on the TV, so we keep them out of the
    /// WebView entirely:
    ///
    ///   1. Mint a short-lived HS256 JWT (shared key with the main backend
    ///      checkBearer middleware).
    ///   2. POST <c>/v1/extract?url=&lt;embed&gt;</c> to the chromedp-backed
    ///      extractor service. Response carries a proxied m3u8 URL.
    ///   3. Wrap that m3u8 in our own backend's hls.js page via
    ///      <c>{backend}/v1/iptv/play?url=&lt;m3u8&gt;&amp;title=&lt;title&gt;</c>.
    ///   4. Return a CLONE of the original <see cref="StreamOption"/> with
    ///      its <c>EmbedUrl</c> rewritten to the wrapper URL.
    ///
    /// Behaviour contract (do not break — host code relies on it):
    ///   - When <c>useBackendStreamProxy</c> is false, returns the original
    ///     unchanged (byte-identical to the pre-feature path).
    ///   - When the host doesn't match the wrapper allowlist, returns the
    ///     original unchanged.
    ///   - When extraction or wrapping fails for ANY reason (network, JWT,
    ///     empty <c>resolved</c>, exception), logs via <see cref="Diagnostics"/>
    ///     and returns the original unchanged. NEVER throws.
    /// </summary>
    public sealed class StreamExtractorService
    {
        // The chromedp extractor lives on its own Cloud Run service in the
        // same project. Hardcoded for now — there's no AppConfig knob for it
        // yet. Add one if/when we need to point staging TVs at a different
        // build of the extractor.
        private const string ExtractorBaseUrl = "https://streamed-extractor-6ubsltvqyq-uc.a.run.app";

        // The archive used a client-embedded backend signing key. Keep this
        // optional path disabled. Future integration must use server-issued
        // authorization; never populate this field with a backend secret.
        private static readonly string SharedJwtKey = "";

        // Extraction is a chromedp boot + page eval, which runs ~8–20 s on
        // a cold instance and up to ~30 s on the slow path (admin source
        // class, where the extractor's chromedp ceiling is 30 s itself).
        // 40 s here gives the backend ~10 s of headroom to RETURN a clean
        // skip-extract-failed response after its own internal timeout
        // fires, instead of the TV giving up first and treating it as a
        // network error.
        private static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(40);

        // Hosts we know wrap an upstream m3u8 in a Cloudflare-protected
        // page that crashes chromium-efl on Tizen. Match case-insensitively
        // against the URI host (no port, no path). Anything not on this
        // list is passed through unchanged so we don't accidentally route
        // working streams through the extractor on its slow path.
        private static readonly HashSet<string> WrapperHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "embedsports.top",
            "www.embedsports.top",
            "pooembed.eu",
            "www.pooembed.eu",
        };

        private readonly string _backendBaseUrl;
        private readonly AppConfigService _appConfig;
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;

        public StreamExtractorService(string backendBaseUrl, AppConfigService appConfig, HttpClient http = null)
        {
            _backendBaseUrl = (backendBaseUrl ?? "").TrimEnd('/');
            _appConfig = appConfig;
            _http = http ?? CreateDefaultHttpClient();
            _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        }

        /// <summary>
        /// Returns either a NEW <see cref="StreamOption"/> with rewritten
        /// <c>EmbedUrl</c> pointing at our hls.js wrapper, or
        /// <paramref name="original"/> unchanged. Never throws — every
        /// failure path falls back to the original so the player flow
        /// continues uninterrupted.
        /// </summary>
        /// <param name="original">The user-selected stream option.</param>
        /// <param name="title">Match title for the wrapper page chrome
        /// (shown in the hls.js page header). Optional — empty string is
        /// fine. Slight deviation from the brief's two-arg signature; added
        /// because the wrapper URL needs the title for UX.</param>
        /// <param name="ct">Caller's cancellation token, typically
        /// <c>_shutdown.Token</c> from Program.</param>
        public async Task<StreamOption> ResolveOrPassThroughAsync(StreamOption original, string title, CancellationToken ct)
        {
            if (original == null) return original;

            // Default-false: if AppConfig hasn't loaded yet (network down on
            // first launch), or the flag is explicitly off, we keep the
            // legacy direct-to-wrapper-page path. The user can always be
            // bumped onto the new path by toggling the Firestore flag.
            bool enabled = false;
            try
            {
                enabled = _appConfig != null && _appConfig.GetFlag("useBackendStreamProxy", false);
            }
            catch
            {
                enabled = false;
            }
            // Sanitized import: never embed a backend signing secret in a client.
            // Keep this optional path disabled until server-issued auth is wired.
            if (!enabled || string.IsNullOrEmpty(SharedJwtKey)) return original;

            string embed = original.EmbedUrl ?? "";
            if (string.IsNullOrEmpty(embed)) return original;
            if (!IsWrapperHost(embed)) return original;
            if (string.IsNullOrEmpty(_backendBaseUrl)) return original;

            try
            {
                Diagnostics.Log("StreamExtractor: attempting extract for " + embed);
                string resolved = await ExtractAsync(embed, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(resolved))
                {
                    Diagnostics.Track("stream_extract_empty", new Dictionary<string, string>
                    {
                        ["embed"] = embed,
                    });
                    return original;
                }

                string wrapped = BuildWrapperUrl(resolved, title);
                if (string.IsNullOrEmpty(wrapped)) return original;

                Diagnostics.Track("stream_extract_ok", new Dictionary<string, string>
                {
                    ["embed"] = embed,
                    ["wrapped"] = wrapped,
                });

                // Clone — never mutate the caller's StreamOption (it lives
                // in _cachedStreams and would poison the cache for the
                // next pick of the same match).
                return new StreamOption
                {
                    Id = original.Id,
                    StreamNo = original.StreamNo,
                    Language = original.Language,
                    Hd = original.Hd,
                    EmbedUrl = wrapped,
                    Source = original.Source,
                    // Surface the RAW proxied m3u8 URL alongside the wrapped
                    // page URL so the host (Program.ShowPlayerView) can hand
                    // it directly to the native NUI VideoView player instead
                    // of round-tripping through the hls.js wrapper page.
                    // EmbedUrl stays the wrapper URL so the existing
                    // PlayerWebView fallback path is byte-identical.
                    ResolvedHlsUrl = resolved,
                };
            }
            catch (OperationCanceledException)
            {
                // Caller (player exit, shutdown) cancelled — propagate the
                // original so the host can either play it or drop it
                // alongside the rest of the cancelled work.
                return original;
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("StreamExtractorService.ResolveOrPassThroughAsync", ex);
                Diagnostics.Track("stream_extract_error", new Dictionary<string, string>
                {
                    ["embed"] = embed,
                    ["err"] = ex.GetType().Name + ":" + (ex.Message ?? ""),
                });
                return original;
            }
        }

        private static bool IsWrapperHost(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                Uri u = new Uri(url, UriKind.Absolute);
                return WrapperHosts.Contains(u.Host);
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> ExtractAsync(string embedUrl, CancellationToken ct)
        {
            string jwt = MintJwt();
            string url = ExtractorBaseUrl + "/v1/extract?url=" + Uri.EscapeDataString(embedUrl);

            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
            req.Headers.Accept.ParseAdd("application/json");

            using HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Diagnostics.Log("StreamExtractor: HTTP " + (int)resp.StatusCode + " body=" + Truncate(body, 200));
                return null;
            }

            ExtractResponse parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ExtractResponse>(body, _json);
            }
            catch (Exception ex)
            {
                Diagnostics.LogError("StreamExtractor.ParseResponse", ex);
                return null;
            }
            if (parsed == null) return null;
            if (!string.IsNullOrEmpty(parsed.Error))
            {
                Diagnostics.Log("StreamExtractor: backend error '" + parsed.Error + "' strategy=" + parsed.Strategy);
                // Empty resolved on a structured error → fall back. (If the
                // backend returns BOTH a resolved URL and a non-fatal error
                // it's still usable; we only treat empty resolved as fatal.)
            }
            return parsed.Resolved ?? "";
        }

        private string BuildWrapperUrl(string m3u8, string title)
        {
            if (string.IsNullOrEmpty(_backendBaseUrl)) return null;
            StringBuilder sb = new StringBuilder();
            sb.Append(_backendBaseUrl);
            sb.Append("/v1/iptv/play?url=");
            sb.Append(Uri.EscapeDataString(m3u8));
            sb.Append("&title=");
            sb.Append(Uri.EscapeDataString(title ?? ""));
            return sb.ToString();
        }

        // ---- JWT minting ---------------------------------------------------

        /// <summary>HS256 JWT with a 1-hour expiry. The extractor's
        /// checkBearer only validates the signature, so the body is
        /// minimal. Hand-rolled rather than pulling a NuGet package because
        /// the dependency surface of this project is intentionally tiny —
        /// chromium-efl is finicky about transitive native deps.</summary>
        private static string MintJwt()
        {
            string headerJson = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
            long iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long exp = iat + 3600;
            string payloadJson = "{\"sub\":\"tv\",\"iat\":" + iat + ",\"exp\":" + exp + "}";

            string header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
            string payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
            string signingInput = header + "." + payload;

            byte[] keyBytes = Encoding.UTF8.GetBytes(SharedJwtKey);
            using HMACSHA256 hmac = new HMACSHA256(keyBytes);
            byte[] sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
            string sigB64 = Base64UrlEncode(sig);

            return signingInput + "." + sigB64;
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            string s = Convert.ToBase64String(bytes);
            // RFC 7515: base64url = base64 with +/= replaced and padding stripped.
            s = s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
            return s;
        }

        // ---- Misc ----------------------------------------------------------

        private static HttpClient CreateDefaultHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
                // Match the rest of the project's services. Tizen's
                // chromium-net stack ships its own root store and the
                // managed BCL fights with it; bypassing the managed-side
                // chain check is the path of least resistance.
            };
            return new HttpClient(handler)
            {
                Timeout = ExtractTimeout,
            };
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= n) return s ?? "";
            return s.Substring(0, n) + "…";
        }

        // ---- DTO -----------------------------------------------------------

        private sealed class ExtractResponse
        {
            [JsonPropertyName("resolved")] public string Resolved { get; set; }
            [JsonPropertyName("raw_resolved")] public string RawResolved { get; set; }
            [JsonPropertyName("strategy")] public string Strategy { get; set; }
            [JsonPropertyName("error")] public string Error { get; set; }
            [JsonPropertyName("elapsed")] public string Elapsed { get; set; }
        }
    }
}
