// Streaming proxy handlers — the actual byte-pump that lets the
// TV's native MediaPlayer fetch a CDN-protected m3u8 + segments
// without ever talking to the CDN directly.
//
// Why this exists: CDNs gating playlist URLs by signed-token-bound-
// to-IP. The chromium that extracted the URL got a token bound to
// Cloud Run's IP; the TV has a different IP; so the TV can't replay.
// Solution: TV asks us, we fetch from CDN (matching IP), we relay
// the bytes.
//
// Two endpoints because HLS has a manifest layer + a segment layer:
//   /v1/proxy/m3u8 — fetches manifest, REWRITES every URL inside it
//                    so segment URLs also point at our proxy. Without
//                    this rewrite the TV would try to fetch segments
//                    directly from the CDN (and get 403).
//   /v1/proxy/seg  — straight byte-stream of one segment.
//
// Both endpoints carry one signed token in `?t=`. The token holds
// the upstream URL + the headers (Referer/UA/Cookie) we need to
// recreate the chromium request. Self-contained — no shared cache
// between Cloud Run instances.
package main

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"
)

// proxyTokenTTL is how long a signed proxy URL stays valid. Long
// enough for a multi-hour live stream session; short enough that a
// leaked token doesn't get used months later.
const proxyTokenTTL = 6 * time.Hour

// proxyFetchTimeout caps each upstream fetch — both the manifest
// (small) and individual segments (1-5 MB). Most segments arrive in
// under a second on a healthy CDN; 30s is a generous outer bound
// before we surface a gateway error to the player.
const proxyFetchTimeout = 30 * time.Second

// uriAttrRe matches `URI="..."` inside HLS tag lines. Used to rewrite
// EXT-X-MAP, EXT-X-KEY, EXT-X-MEDIA, EXT-X-I-FRAME-STREAM-INF, etc.
// Conservative: matches double-quoted form only; the spec allows
// unquoted in some places but tags that take URIs always quote.
var uriAttrRe = regexp.MustCompile(`URI="([^"]+)"`)

// handleProxyM3U8 serves /v1/proxy/m3u8?t=<token>.
//
// 1. Verify token. Decode → upstream URL, headers, expiry.
// 2. Fetch upstream with the recreated browser headers.
// 3. Rewrite every URL inside the manifest:
//      - bare URI lines (segment URIs in media playlists, variant
//        URIs in master playlists) → become full proxy URLs.
//      - URI="..." attributes inside tag lines → same.
//    URIs ending in `.m3u8` are routed back through this same
//    endpoint (variant playlists in a master playlist need the
//    same rewriting recursively when the player fetches them).
//    Other URIs go through /v1/proxy/seg.
// 4. Return the rewritten manifest with HLS content-type.
func (s *proxySvc) handleProxyM3U8(w http.ResponseWriter, r *http.Request) {
	tok, err := verifyProxyToken(r.URL.Query().Get("t"), s.signingKey)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "bad token: "+err.Error())
		return
	}

	body, contentType, status, err := s.fetchUpstream(r.Context(), tok)
	if err != nil {
		s.logger.Warn("proxy m3u8 fetch failed", "url", tok.URL, "err", err)
		writeError(w, http.StatusBadGateway, "upstream fetch failed")
		return
	}
	if status >= 400 {
		// Log the upstream body too — CDNs often explain WHY in
		// the body (rate-limit, geo-block, TLS-fingerprint
		// mismatch, etc.). Truncated to keep the line readable.
		bodyStr := string(body)
		if len(bodyStr) > 300 {
			bodyStr = bodyStr[:300] + "…"
		}
		s.logger.Warn("proxy m3u8 upstream non-2xx",
			"url", tok.URL,
			"status", status,
			"body", bodyStr,
			"upstream_ct", contentType,
		)
		http.Error(w, "upstream "+fmt.Sprint(status), http.StatusBadGateway)
		return
	}

	// Resolve relative segment URIs against the upstream m3u8 URL.
	baseU, _ := url.Parse(tok.URL)
	rewritten := s.rewriteManifest(string(body), baseU, tok)

	if contentType == "" {
		contentType = "application/vnd.apple.mpegurl"
	}
	w.Header().Set("Content-Type", contentType)
	// HLS players poll the manifest aggressively — disable caching
	// so they always see the freshest segment list.
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write([]byte(rewritten))
}

// handleProxySeg serves /v1/proxy/seg?t=<token>. Pure byte pump:
// fetch upstream with the right headers, stream the response body
// straight back. No body inspection, no rewriting.
//
// We use io.Copy without buffering on our side — chunked transfer
// encoding lets the player start decoding before the full segment
// has landed.
func (s *proxySvc) handleProxySeg(w http.ResponseWriter, r *http.Request) {
	tok, err := verifyProxyToken(r.URL.Query().Get("t"), s.signingKey)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "bad token: "+err.Error())
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), proxyFetchTimeout)
	defer cancel()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, tok.URL, nil)
	if err != nil {
		writeError(w, http.StatusBadRequest, "bad upstream url")
		return
	}
	applyProxyHeaders(req, tok)
	// Forward Range header if the player asks for a partial — Tizen
	// MediaPlayer occasionally byte-range-requests for seeking.
	if rng := r.Header.Get("Range"); rng != "" {
		req.Header.Set("Range", rng)
	}

	resp, err := s.httpClient.Do(req)
	if err != nil {
		s.logger.Warn("proxy seg fetch failed", "url", tok.URL, "err", err)
		writeError(w, http.StatusBadGateway, "upstream fetch failed")
		return
	}
	defer resp.Body.Close()

	// Forward the upstream content-type + length so the player can
	// preallocate its buffer / set up its demuxer.
	for _, h := range []string{"Content-Type", "Content-Length", "Content-Range", "Accept-Ranges"} {
		if v := resp.Header.Get(h); v != "" {
			w.Header().Set(h, v)
		}
	}
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(resp.StatusCode)

	if _, err := io.Copy(w, resp.Body); err != nil {
		// Common: client disconnected mid-stream when scrubbing or
		// switching streams. Not actionable; INFO-level.
		s.logger.Info("proxy seg copy interrupted", "err", err)
	}
}

// rewriteManifest is the heart of the m3u8 proxy. Iterates the
// manifest line-by-line; URIs become `<proxyBase>/v1/proxy/{m3u8|seg}?t=...`.
//
// Why line-based instead of a real HLS parser: the manifest is
// well-formed text per spec, the rewrite is local (URI replacement
// only), and we don't want to ever drop or reorder lines (some
// players are picky about #EXT-X-VERSION, custom tags, etc.).
// Pass-through everything we don't explicitly need to rewrite.
func (s *proxySvc) rewriteManifest(body string, base *url.URL, parentTok *proxyToken) string {
	exp := time.Now().Add(proxyTokenTTL).Unix()
	var out strings.Builder
	out.Grow(len(body) * 2) // rough — rewriting roughly doubles per-URL size

	lines := strings.Split(body, "\n")
	for _, line := range lines {
		trimmed := strings.TrimSpace(line)
		switch {
		case trimmed == "":
			out.WriteString(line)
			out.WriteString("\n")
		case strings.HasPrefix(trimmed, "#"):
			// Tag line. May contain URI="..." we need to rewrite.
			if strings.Contains(line, `URI="`) {
				line = uriAttrRe.ReplaceAllStringFunc(line, func(match string) string {
					inner := strings.TrimPrefix(strings.TrimSuffix(match, `"`), `URI="`)
					proxied := s.proxyForURI(inner, base, parentTok, exp)
					return `URI="` + proxied + `"`
				})
			}
			out.WriteString(line)
			out.WriteString("\n")
		default:
			// Bare URI line — segment or variant playlist.
			proxied := s.proxyForURI(trimmed, base, parentTok, exp)
			out.WriteString(proxied)
			out.WriteString("\n")
		}
	}
	return out.String()
}

// proxyForURI absolutizes a URI against the manifest's base, mints
// a token wrapping it, and returns the full proxy URL the player
// should fetch instead. URIs ending in `.m3u8` (variant playlists)
// route back through the m3u8 endpoint so the recursion handles
// nested rewriting; everything else is a segment.
func (s *proxySvc) proxyForURI(rawURI string, base *url.URL, parentTok *proxyToken, exp int64) string {
	if rawURI == "" {
		return rawURI
	}
	// Already absolute? Use as-is. Otherwise resolve against base.
	abs := rawURI
	if u, err := base.Parse(rawURI); err == nil && u != nil {
		abs = u.String()
	}

	tok := proxyToken{
		URL:     abs,
		Referer: parentTok.Referer,
		UA:      parentTok.UA,
		Cookie:  parentTok.Cookie,
		Expiry:  exp,
	}
	signed, err := signProxyToken(tok, s.signingKey)
	if err != nil {
		// Mint failure is essentially impossible (HMAC + JSON of
		// known-good values), but be defensive — return the
		// original URI so the player at least fails with a clear
		// upstream error rather than a malformed proxy URL.
		return rawURI
	}
	endpoint := "/v1/proxy/seg"
	low := strings.ToLower(abs)
	// Strip query string before suffix check — common pattern is
	// `index.m3u8?token=abc`.
	if i := strings.IndexByte(low, '?'); i >= 0 {
		low = low[:i]
	}
	if strings.HasSuffix(low, ".m3u8") {
		endpoint = "/v1/proxy/m3u8"
	}
	return s.publicBaseURL + endpoint + "?t=" + signed
}

// fetchUpstream pulls the URL with the recreated browser headers.
// Returns body + content-type + status. Used by the m3u8 endpoint
// (segment endpoint streams instead of buffering, so it doesn't
// share this code).
func (s *proxySvc) fetchUpstream(parent context.Context, tok *proxyToken) ([]byte, string, int, error) {
	ctx, cancel := context.WithTimeout(parent, proxyFetchTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, tok.URL, nil)
	if err != nil {
		return nil, "", 0, err
	}
	applyProxyHeaders(req, tok)
	resp, err := s.httpClient.Do(req)
	if err != nil {
		return nil, "", 0, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 5*1024*1024)) // 5MB cap on a manifest is generous
	if err != nil {
		return nil, "", resp.StatusCode, err
	}
	return body, resp.Header.Get("Content-Type"), resp.StatusCode, nil
}

// applyProxyHeaders attaches the upstream-replay headers we
// captured during extraction. Conservative — we only set the ones
// we actually captured. Notably we do NOT echo every chromium
// client-hint; only Referer/UA/Cookie which are what real CDNs
// gate on.
func applyProxyHeaders(req *http.Request, tok *proxyToken) {
	if tok.Referer != "" {
		req.Header.Set("Referer", tok.Referer)
	}
	if tok.UA != "" {
		req.Header.Set("User-Agent", tok.UA)
	}
	if tok.Cookie != "" {
		req.Header.Set("Cookie", tok.Cookie)
	}
	// Origin tracks Referer's host — some CDNs check both.
	if tok.Referer != "" {
		if u, err := url.Parse(tok.Referer); err == nil {
			req.Header.Set("Origin", u.Scheme+"://"+u.Host)
		}
	}
	req.Header.Set("Accept", "*/*")
}

// proxySvc bundles the proxy-handler dependencies: HTTP client,
// HMAC signing key, public-facing base URL (for rewriting URIs
// inside the m3u8 to point back at us), logger.
type proxySvc struct {
	httpClient    *http.Client
	signingKey    []byte
	publicBaseURL string
	logger        *slog.Logger
}

func newProxySvc(signingKey []byte, publicBaseURL string, logger *slog.Logger) *proxySvc {
	return &proxySvc{
		// uTLS-fingerprinted client. The streamed CDN class
		// 500s on a vanilla Go http.Client even from the same
		// IP that just succeeded with chromium — JA3 bot
		// detection. Mimicking Chrome's ClientHello shape
		// gets us through. See utls_client.go.
		httpClient:    newUTLSHTTPClient(proxyFetchTimeout),
		signingKey:    signingKey,
		publicBaseURL: strings.TrimRight(publicBaseURL, "/"),
		logger:        logger,
	}
}
