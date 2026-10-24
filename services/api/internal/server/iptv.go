package server

import (
	"context"
	"net/http"
	"net/url"
	"strings"

	"github.com/abindlish/streamed-backend/internal/iptv"
)

// WithIPTV wires the iptv-org catalog service. When unset the
// /v1/iptv/* routes return 503 — useful in local dev where we
// don't want to hit the iptv-org GitHub Pages mirror.
//
// Also constructs the stream proxy bound to the same catalog
// (lazy-init in iptvProxy() so we don't pay for it when iptv is
// disabled).
func (s *Server) WithIPTV(svc *iptv.Service) *Server {
	s.iptv = svc
	return s
}

// iptvProxy returns the lazily-built proxy. Returns nil when iptv
// catalog isn't configured — handlers should 503 in that case.
//
// Why lazy: the proxy is only needed when the TV actually plays
// an IPTV stream; building it eagerly would couple route
// registration to construction order.
func (s *Server) iptvProxy() *iptv.ProxyService {
	if s.iptv == nil {
		return nil
	}
	if s._iptvProxy == nil {
		s._iptvProxy = iptv.NewProxy(s.iptv, nil)
	}
	return s._iptvProxy
}

// handleIPTVChannels — GET /v1/iptv/channels?category=sports&country=US
//
// Returns the cached channel list (auto-refreshed every 6h).
// Filters are optional; with no filters returns ALL playable
// channels (~14k). The TV defaults to category=sports.
func (s *Server) handleIPTVChannels(w http.ResponseWriter, r *http.Request) {
	if s.iptv == nil {
		writeError(w, http.StatusServiceUnavailable, "iptv catalog not configured")
		return
	}
	cat := r.URL.Query().Get("category")
	cc := r.URL.Query().Get("country")
	channels, err := s.iptv.ListChannels(r.Context(), cat, cc)
	if err != nil {
		s.logger.Warn("iptv list failed", "err", err)
		writeError(w, http.StatusBadGateway, "iptv catalog fetch failed")
		return
	}
	w.Header().Set("Cache-Control", "public, max-age=600")
	writeJSON(w, http.StatusOK, map[string]interface{}{
		"channels": channels,
		"count":    len(channels),
	})
}

// handleIPTVM3U8 — GET /v1/iptv/m3u8?ch=<channel_id>[&u=<url>]
//
// Fetches the channel's primary stream's m3u8 (or `u`, when given,
// must share the channel's host) with the per-stream UA + Referer
// from iptv-org's metadata. Rewrites every segment + sub-manifest
// URL inside to point back at this proxy so the player never
// fetches anything from the upstream CDN directly.
//
// Always sets `Access-Control-Allow-Origin: *` so hls.js can read
// the response from any page origin (including data: URLs which
// have origin = null).
//
// Why this exists: hls.js running in the WebView can't override
// Referer for cross-origin XHR. Streams that gate on Referer (a
// significant fraction of iptv-org's catalog) reject direct
// fetches. Routing through us lets us attach whatever Referer the
// upstream needs.
func (s *Server) handleIPTVM3U8(w http.ResponseWriter, r *http.Request) {
	if s.iptv == nil {
		writeError(w, http.StatusServiceUnavailable, "iptv catalog not configured")
		return
	}
	proxy := s.iptvProxy()
	if proxy == nil {
		writeError(w, http.StatusServiceUnavailable, "iptv proxy not configured")
		return
	}
	chID := r.URL.Query().Get("ch")
	urlOverride := r.URL.Query().Get("u")
	publicBase := requestPublicBase(r)

	status, contentType, body, err := proxy.ProxyM3U8(r.Context(), chID, urlOverride, publicBase)
	if err != nil {
		s.logger.Warn("iptv proxy m3u8 failed", "ch", chID, "u", urlOverride, "err", err)
		// Body may carry the upstream's own error explanation.
		// Pass it through verbatim so the client trace shows what
		// the CDN actually said (rate-limit, geo-block, etc.).
		if status == 0 {
			status = http.StatusBadGateway
		}
	}
	if contentType != "" {
		w.Header().Set("Content-Type", contentType)
	}
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_, _ = w.Write(body)
}

// handleIPTVSeg — GET /v1/iptv/seg?ch=<channel_id>&u=<segment_url>
//
// Streams one HLS segment (or init segment, or encryption key)
// through with the channel's UA + Referer attached. Forwards
// Range header for player seeking and echoes upstream
// Content-Type / Content-Length / Content-Range / Accept-Ranges
// so hls.js can preallocate buffers and handle byte-range fetches
// correctly.
func (s *Server) handleIPTVSeg(w http.ResponseWriter, r *http.Request) {
	if s.iptv == nil {
		writeError(w, http.StatusServiceUnavailable, "iptv catalog not configured")
		return
	}
	proxy := s.iptvProxy()
	if proxy == nil {
		writeError(w, http.StatusServiceUnavailable, "iptv proxy not configured")
		return
	}
	chID := r.URL.Query().Get("ch")
	segURL := r.URL.Query().Get("u")
	if err := proxy.ProxySeg(r.Context(), w, r, chID, segURL); err != nil {
		// ProxySeg has already written headers + status by the
		// time it returns an error (mid-copy disconnect, etc.).
		// Just log; can't change the response now.
		s.logger.Info("iptv proxy seg interrupted", "ch", chID, "err", err)
	}
}

// requestPublicBase derives the externally-reachable origin from
// the incoming request. We can't hardcode this (varies between
// local dev, staging, prod) and don't want a separate env var
// (one less knob to misconfigure). The proxy uses this to mint
// rewritten URLs inside the m3u8 response.
//
// Cloud Run sets X-Forwarded-Proto correctly. For direct local
// HTTP we fall back to "http" when neither indicator says HTTPS.
func requestPublicBase(r *http.Request) string {
	scheme := "https"
	if r.TLS == nil && !strings.EqualFold(r.Header.Get("X-Forwarded-Proto"), "https") {
		scheme = "http"
	}
	host := r.Host
	if h := r.Header.Get("X-Forwarded-Host"); h != "" {
		host = h
	}
	return scheme + "://" + host
}

// handleIPTVPlay — GET /v1/iptv/play?url=<m3u8>&ua=...&ref=...
//
// Returns a tiny self-contained HTML page that loads hls.js + a
// `<video>` element, wires the m3u8 into it, and starts playback.
// Used by the TV's PlayerWebView so we can reuse all of its
// existing UI (overlays, key handling, exit) for iptv-org streams
// without writing a parallel native-player view.
//
// Why server-side render this instead of just navigating the
// WebView at the m3u8 URL: chromium-efl shows m3u8 as raw text
// (no built-in HLS playback). A `<video>` + hls.js wrapper is the
// minimal way to actually play the stream.
//
// Why fetch hls.js from a CDN inside the served HTML rather than
// inlining: keeps the response small (1KB vs 600KB), and lets us
// pin a known-good hls.js version that we control without
// updating the backend binary.
func (s *Server) handleIPTVPlay(w http.ResponseWriter, r *http.Request) {
	raw := strings.TrimSpace(r.URL.Query().Get("url"))
	if raw == "" {
		http.Error(w, "missing url", http.StatusBadRequest)
		return
	}
	parsed, err := url.Parse(raw)
	if err != nil || (parsed.Scheme != "https" && parsed.Scheme != "http") {
		http.Error(w, "url must be http/https", http.StatusBadRequest)
		return
	}
	// Optional per-stream replay headers from iptv-org's metadata.
	// We can't actually FORCE these as request headers from a
	// browser <video> element (CORS-restricted), but for streams
	// that need a Referer the only chance is a meta-referrer
	// policy — we set "unsafe-url" so the iframe sends the page's
	// host as Referer. Worst case the upstream just returns 403
	// and the user sees a load failure, which is better than
	// silently broken.
	ua := strings.TrimSpace(r.URL.Query().Get("ua"))
	_ = ua // hls.js can pass xhrSetup-set headers but only same-origin; cross-origin segment fetches inherit browser UA. No reliable way to force.

	// Title is just for the page's own tab — not visible inside
	// our WebView player.
	title := strings.TrimSpace(r.URL.Query().Get("title"))
	if title == "" {
		title = "Live"
	}

	html := buildIPTVPlayerHTML(raw, title)
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	_, _ = w.Write([]byte(html))
}

// buildIPTVPlayerHTML returns the inline HTML for the player page.
// The HTML is intentionally tiny + dependency-light: one <video>,
// one hls.js script tag, ~30 lines of JS to wire them together.
// The TV's existing PlayerWebView treats this exactly like any
// other stream wrapper page.
func buildIPTVPlayerHTML(m3u8URL, title string) string {
	// JSON-encode the URL to safely embed in JS (handles quoting
	// + backslashes; no XSS via crafted query string).
	srcJSON := jsonString(m3u8URL)
	return `<!DOCTYPE html>
<html lang="en"><head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="referrer" content="unsafe-url">
<title>` + htmlEscape(title) + `</title>
<style>
html,body{margin:0;padding:0;height:100%;background:#000;overflow:hidden;font-family:sans-serif}
#v{width:100vw;height:100vh;background:#000;object-fit:contain}
#status{position:fixed;left:24px;bottom:24px;padding:8px 12px;background:rgba(0,0,0,.7);color:#fff;font-size:14px;border-radius:6px;display:none}
</style>
</head><body>
<video id="v" autoplay playsinline muted></video>
<div id="status"></div>
<script src="https://cdn.jsdelivr.net/npm/hls.js@1.5.20/dist/hls.min.js"></script>
<script>
(function(){
  var src = ` + srcJSON + `;
  var v = document.getElementById('v');
  var statusEl = document.getElementById('status');
  function status(s, isError){
    statusEl.textContent = s;
    statusEl.style.display = s ? 'block' : 'none';
    statusEl.style.background = isError ? 'rgba(180,40,40,.85)' : 'rgba(0,0,0,.7)';
  }
  function tryAutoplay(){ v.play().catch(function(e){ /* user gesture needed; muted autoplay should work */ }); }
  if (window.Hls && Hls.isSupported()) {
    status('Loading…');
    var h = new Hls({
      // Tizen TVs run a weaker CPU than desktops — generous buffer.
      maxBufferLength: 60,
      maxBufferSize: 120 * 1024 * 1024,
      capLevelToPlayerSize: true,
      lowLatencyMode: false,
      fragLoadingTimeOut: 30000,
      manifestLoadingTimeOut: 20000,
      enableWorker: true
    });
    h.loadSource(src);
    h.attachMedia(v);
    h.on(Hls.Events.MANIFEST_PARSED, function(){ status(''); tryAutoplay(); });
    h.on(Hls.Events.ERROR, function(_, data){
      if (!data || !data.fatal) return;
      var msg = (data.type || '?') + ': ' + (data.details || '?');
      status('Stream error — ' + msg, true);
    });
  } else if (v.canPlayType('application/vnd.apple.mpegurl')) {
    // Safari / iOS native HLS path — irrelevant on Tizen but free.
    v.src = src;
    v.addEventListener('canplay', function(){ status(''); tryAutoplay(); });
    v.addEventListener('error', function(){ status('Stream error', true); });
  } else {
    status('HLS not supported in this browser', true);
  }
})();
</script>
</body></html>
`
}

// jsonString returns a safely JSON-quoted JS literal for s.
// Inlined rather than depending on encoding/json for one string.
func jsonString(s string) string {
	var b strings.Builder
	b.WriteByte('"')
	for _, r := range s {
		switch r {
		case '"':
			b.WriteString(`\"`)
		case '\\':
			b.WriteString(`\\`)
		case '\n':
			b.WriteString(`\n`)
		case '\r':
			b.WriteString(`\r`)
		case '\t':
			b.WriteString(`\t`)
		case '<':
			// Avoid </script> closing the surrounding script tag.
			b.WriteString(`<`)
		default:
			if r < 0x20 {
				b.WriteString("\\u00")
				const hex = "0123456789abcdef"
				b.WriteByte(hex[r>>4])
				b.WriteByte(hex[r&0xf])
			} else {
				b.WriteRune(r)
			}
		}
	}
	b.WriteByte('"')
	return b.String()
}

// htmlEscape — minimal escape for the page <title>.
func htmlEscape(s string) string {
	r := strings.NewReplacer("&", "&amp;", "<", "&lt;", ">", "&gt;", `"`, "&quot;", "'", "&#39;")
	return r.Replace(s)
}

// Compile-time guard so the iptv import isn't dropped if the
// handlers ever get refactored down to no direct iptv use.
var _ context.Context