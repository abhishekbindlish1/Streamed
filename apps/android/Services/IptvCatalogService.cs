using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Models;
using StreamedTv.Platform;

namespace StreamedTv.Services
{
    /// <summary>
    /// Thin HTTP client for the backend's iptv-org endpoints. Caches
    /// the channel list in memory for the session — backend already
    /// caches for 6h, no point re-fetching across the same session
    /// unless the user explicitly refreshes.
    ///
    /// Why a service vs inlining the HttpClient call: keeps the
    /// nav-pill / channel-row code unaware of HTTP, gives us one
    /// place to add retry / cache-bust / per-category filtering
    /// later without reaching into UI code.
    /// </summary>
    public sealed class IptvCatalogService
    {
        private const string LogTag = "StreamedTv";
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly JsonSerializerOptions _json;

        // In-process cache. _channels is what callers see; we only
        // refetch when null OR the cache TTL has elapsed.
        private List<IptvChannel> _channels;
        private DateTime _fetchedAt = DateTime.MinValue;
        // 1 hour matches StreamedCatalogService's TTL — without parity the
        // user perceives IPTV as "uncached" when navigating between Live TV
        // and a sport tab (sport stays cached, IPTV evicts after 15 min and
        // re-fetches). Backend caches the upstream for 6 h anyway, so the
        // longer TTL just means fewer round trips with the same answer.
        private static readonly TimeSpan SessionCacheTtl = TimeSpan.FromHours(1);

        public IptvCatalogService(HttpClient http = null, string baseUrl = null)
        {
            _http = http ?? CreateDefaultHttpClient();
            _baseUrl = (string.IsNullOrEmpty(baseUrl) ? SportsDataService.DefaultBaseUrl : baseUrl).TrimEnd('/');
            _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        }

        /// <summary>
        /// True when the in-memory cache has at least one channel AND the
        /// last fetch is still within <see cref="SessionCacheTtl"/>. Lets
        /// the caller skip the visible "Loading channels…" status when a
        /// subsequent <see cref="GetSportsChannelsAsync"/> call will return
        /// instantly — without this, every Live TV pill nav showed a
        /// loading flash even though no network call was happening.
        /// Lock-free read: caller is single-threaded UI thread.
        /// </summary>
        public bool HasFreshCache
        {
            get
            {
                return _channels != null
                    && _channels.Count > 0
                    && (DateTime.UtcNow - _fetchedAt) < SessionCacheTtl;
            }
        }

        /// <summary>Fetch sports channels (default category). Returns
        /// the cached list when fresh; refetches when stale or empty.
        /// On network error returns whatever's cached (possibly
        /// empty) rather than throwing — caller treats empty list
        /// as "iptv unavailable".</summary>
        public async Task<IReadOnlyList<IptvChannel>> GetSportsChannelsAsync(CancellationToken ct)
        {
            if (_channels != null && (DateTime.UtcNow - _fetchedAt) < SessionCacheTtl)
            {
                return _channels;
            }
            try
            {
                string url = _baseUrl + "/v1/iptv/channels?category=sports";
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
                using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(true);
                if (!resp.IsSuccessStatusCode) return _channels ?? new List<IptvChannel>();
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
                ChannelsResponse parsed = JsonSerializer.Deserialize<ChannelsResponse>(body, _json);
                if (parsed?.Channels != null)
                {
                    _channels = parsed.Channels;
                    _fetchedAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                try { AppLog.Warn(LogTag, "IptvCatalog fetch failed: " + ex.Message); } catch { }
            }
            return _channels ?? new List<IptvChannel>();
        }

        /// <summary>Build the play URL for a channel. Returns a
        /// <c>data:text/html;base64,…</c> URL containing a tiny
        /// self-contained player page (hls.js + a <video> element)
        /// whose m3u8 source points at the backend's PROXY endpoint
        /// (<c>/v1/iptv/m3u8?ch=&lt;id&gt;</c>) — NOT the raw upstream
        /// CDN URL.
        ///
        /// Why route through the proxy instead of fetching the m3u8
        /// directly: hls.js running in the WebView can't override
        /// Referer for cross-origin XHR. A meaningful fraction of
        /// iptv-org's catalog gates on Referer (or User-Agent), so
        /// direct fetches return 403 / hang and surface as
        /// `manifestLoadError`. The backend proxy fetches with the
        /// per-stream UA + Referer iptv-org's metadata gives us,
        /// rewrites segment URIs to also go through the proxy, and
        /// returns with `Access-Control-Allow-Origin: *` so this
        /// data: URL page (origin = null) can read the response.
        ///
        /// The page wrapper itself stays a data: URL — only the
        /// HLS playlist + segments go through the backend, so we
        /// avoid the cold-start latency of also fetching the
        /// player HTML server-side.
        ///
        /// Returns null when the channel id is missing — the
        /// proxy endpoint can't resolve a stream without an id.</summary>
        public string BuildPlayUrl(IptvChannel channel)
        {
            if (channel == null || string.IsNullOrWhiteSpace(channel.Id)) return null;
            if (channel.Streams == null || channel.Streams.Count == 0) return null;
            string proxyUrl = _baseUrl + "/v1/iptv/m3u8?ch=" + Uri.EscapeDataString(channel.Id);
            string title = channel.Name ?? "Live";
            string html = BuildIptvPlayerHtml(proxyUrl, title, _baseUrl);
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
            return "data:text/html;base64," + base64;
        }

        // Mirrors backend/internal/server/iptv.go:buildIPTVPlayerHTML.
        // Kept in sync by hand — the two are intentionally simple
        // enough that drift is easy to spot. If we ever change the
        // hls.js config (buffer sizes / timeouts), update both.
        //
        // <link rel="preconnect"> warms the TCP+TLS handshakes for
        // both hosts during page-parse time so they're ready before
        // hls.js actually issues the fetches. Without these the user
        // pays a fresh handshake to cdn.jsdelivr.net (for hls.min.js)
        // AND to the backend (for the m3u8 + every segment) on every
        // first stream open of a session — which is exactly the
        // "first-time buffer, second-time fast" pattern the user
        // reported. Subsequent opens are fast naturally because
        // chromium caches hls.min.js + the DNS records, but the
        // first open of a session has none of that.
        private static string BuildIptvPlayerHtml(string m3u8, string title, string backendOrigin)
        {
            string srcJson = JsEscape(m3u8);
            string esc = HtmlEscape(title);
            // Verbatim string + double-braces for CSS / JS so { } is
            // literal. Pinned hls.js version matches the backend.
            return $@"<!DOCTYPE html>
<html lang=""en""><head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<meta name=""referrer"" content=""unsafe-url"">
<title>{esc}</title>
<link rel=""preconnect"" href=""https://cdn.jsdelivr.net"" crossorigin>
<link rel=""preconnect"" href=""{backendOrigin}"" crossorigin>
<link rel=""dns-prefetch"" href=""{backendOrigin}"">
<style>
html,body{{margin:0;padding:0;height:100%;background:#000;overflow:hidden;font-family:sans-serif}}
#v{{width:100vw;height:100vh;background:#000;object-fit:contain}}
#status{{position:fixed;left:24px;bottom:24px;padding:8px 12px;background:rgba(0,0,0,.7);color:#fff;font-size:14px;border-radius:6px;display:none}}
</style>
</head><body>
<video id=""v"" autoplay playsinline muted></video>
<div id=""status""></div>
<script src=""https://cdn.jsdelivr.net/npm/hls.js@1.5.20/dist/hls.min.js""></script>
<script>
(function(){{
  var src = ""{srcJson}"";
  var v = document.getElementById('v');
  var statusEl = document.getElementById('status');
  function status(s, isError){{
    statusEl.textContent = s;
    statusEl.style.display = s ? 'block' : 'none';
    statusEl.style.background = isError ? 'rgba(180,40,40,.85)' : 'rgba(0,0,0,.7)';
  }}
  function tryAutoplay(){{ v.play().catch(function(e){{}}); }}
  if (window.Hls && Hls.isSupported()) {{
    status('Loading…');
    var h = new Hls({{
      maxBufferLength: 60,
      maxBufferSize: 120 * 1024 * 1024,
      capLevelToPlayerSize: true,
      lowLatencyMode: false,
      fragLoadingTimeOut: 30000,
      manifestLoadingTimeOut: 20000,
      enableWorker: true
    }});
    h.loadSource(src);
    h.attachMedia(v);
    h.on(Hls.Events.MANIFEST_PARSED, function(){{ status(''); tryAutoplay(); }});
    h.on(Hls.Events.ERROR, function(_, data){{
      if (!data || !data.fatal) return;
      var msg = (data.type || '?') + ': ' + (data.details || '?');
      status('Stream error — ' + msg, true);
    }});
  }} else if (v.canPlayType('application/vnd.apple.mpegurl')) {{
    v.src = src;
    v.addEventListener('canplay', function(){{ status(''); tryAutoplay(); }});
    v.addEventListener('error', function(){{ status('Stream error', true); }});
  }} else {{
    status('HLS not supported', true);
  }}
}})();
</script>
</body></html>";
        }

        private static string JsEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    // Avoid </script> closing the surrounding script tag.
                    case '<': b.Append("\\u003c"); break;
                    default:
                        if (c < 0x20) b.AppendFormat("\\u{0:x4}", (int)c);
                        else b.Append(c);
                        break;
                }
            }
            return b.ToString();
        }

        private static string HtmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&#39;");
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        private sealed class ChannelsResponse
        {
            [JsonPropertyName("channels")]
            public List<IptvChannel> Channels { get; set; }

            [JsonPropertyName("count")]
            public int Count { get; set; }
        }
    }
}
