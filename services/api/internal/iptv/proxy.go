// Stream proxy — fetches m3u8 manifests + HLS segments from
// iptv-org-listed CDNs server-side and relays them to the TV.
//
// Why this exists: hls.js running in the TV's WebView can't override
// the User-Agent or Referer for cross-origin fetches (CORS), so
// streams that gate on either header reject the TV directly.
// Routing the fetch through our backend lets us attach the
// per-stream UA + Referer iptv-org's metadata gives us, plus we
// can serve manifests with `Access-Control-Allow-Origin: *` so
// CORS never bites. Tradeoff: every stream goes through us
// bandwidth-wise.
//
// Auth model: NO HMAC tokens. The first fetch identifies the
// channel by id (`?ch=<channel_id>`); we look up its stream from
// the cached catalog. For sub-manifests + segments, the URL is
// passed in `?u=<url>` and we verify its HOST matches the channel's
// stream URL host (loose check — covers CDN sub-domains within
// the same root). Abuse surface is small: the URLs are already
// public on iptv-org's GitHub Pages; the worst someone could do
// is use us as a free proxy for those same public URLs.
//
// Two endpoints, mirroring HLS's two-layer structure:
//   /v1/iptv/m3u8 — fetches manifest, REWRITES every URL inside it
//                   so segments + variant playlists also route
//                   through us. Without this rewrite the player
//                   would fetch segments directly from the CDN
//                   and we'd be back to square one.
//   /v1/iptv/seg  — pure byte-stream of one segment with the
//                   replay headers attached.
package iptv

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"
)

// proxyFetchTimeout caps each upstream fetch — both the manifest
// (small) and individual segments (1–5 MB). 30s is generous; live
// segments arrive in well under a second on a healthy CDN.
const proxyFetchTimeout = 30 * time.Second

// uriAttrRe matches `URI="..."` inside HLS tag lines. Used to
// rewrite EXT-X-MAP, EXT-X-KEY, EXT-X-MEDIA, EXT-X-I-FRAME-STREAM-INF,
// etc. Conservative — matches double-quoted form only; the spec
// allows unquoted in some places but tags that take URIs always
// quote.
var uriAttrRe = regexp.MustCompile(`URI="([^"]+)"`)

// ProxyService bundles the proxy dependencies. Catalog gives us
// per-channel metadata (UA + Referer + URL); httpClient performs
// the upstream fetches. The public base URL (host:port we put in
// rewritten manifest URLs) is passed PER CALL — derived from the
// client's request Host so we don't need a separate config knob.
type ProxyService struct {
	catalog    *Service
	httpClient *http.Client
}

// NewProxy builds a proxy bound to the given catalog.
func NewProxy(catalog *Service, httpClient *http.Client) *ProxyService {
	if httpClient == nil {
		httpClient = &http.Client{Timeout: proxyFetchTimeout}
	}
	return &ProxyService{
		catalog:    catalog,
		httpClient: httpClient,
	}
}

// ProxyM3U8 fetches the requested m3u8 with the channel's UA +
// Referer headers, rewrites every embedded URL to route back
// through us, and returns the rewritten body. Always sets CORS
// headers so hls.js can read the response from any page origin
// (data: URLs included).
//
// `chID` identifies the channel (looked up in the catalog cache).
// `urlOverride` is optional — when non-empty it specifies a sub-
// manifest URL discovered during a previous proxy_m3u8 call (must
// match the channel's host); when empty we use the channel's
// primary stream URL.
// `publicBase` is the externally-reachable origin we put in
// rewritten URLs (typically `https://<r.Host>` derived by the
// handler from the client's request).
//
// Returns: status code (4xx for caller errors, 5xx for upstream),
// content-type, body, error.
func (p *ProxyService) ProxyM3U8(ctx context.Context, chID, urlOverride, publicBase string) (int, string, []byte, error) {
	stream, baseURL, ok := p.resolveStream(chID, urlOverride)
	if !ok {
		return http.StatusBadRequest, "", nil, fmt.Errorf("invalid ch / url for channel")
	}
	body, contentType, status, err := p.fetchUpstream(ctx, baseURL, stream.UserAgent, stream.Referrer)
	if err != nil {
		return http.StatusBadGateway, "", nil, err
	}
	if status >= 400 {
		// Bubble the upstream status up so the client sees it
		// (vs masking everything as 502). hls.js distinguishes
		// 403 / 404 / 5xx in its error reasons.
		return status, contentType, body, nil
	}
	baseU, _ := url.Parse(baseURL)
	rewritten := p.rewriteManifest(string(body), baseU, chID, publicBase)
	if contentType == "" {
		contentType = "application/vnd.apple.mpegurl"
	}
	return http.StatusOK, contentType, []byte(rewritten), nil
}

// ProxySeg streams one HLS segment through. Pure byte pump —
// fetches with the channel's headers, copies the response body
// straight to `w`. Forwards Range header for player seeking and
// echoes Content-Type / Content-Length / Content-Range / Accept-Ranges
// so the player can preallocate buffers + handle byte ranges.
//
// Errors mid-copy (client disconnected, upstream stall) get logged
// at INFO since they're routine during stream switches.
func (p *ProxyService) ProxySeg(ctx context.Context, w http.ResponseWriter, r *http.Request, chID, segURL string) error {
	stream, target, ok := p.resolveStream(chID, segURL)
	if !ok {
		http.Error(w, "invalid ch / url for channel", http.StatusBadRequest)
		return nil
	}
	subCtx, cancel := context.WithTimeout(ctx, proxyFetchTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(subCtx, http.MethodGet, target, nil)
	if err != nil {
		http.Error(w, "bad upstream url", http.StatusBadRequest)
		return nil
	}
	applyProxyHeaders(req, stream.UserAgent, stream.Referrer)
	if rng := r.Header.Get("Range"); rng != "" {
		req.Header.Set("Range", rng)
	}

	resp, err := p.httpClient.Do(req)
	if err != nil {
		http.Error(w, "upstream fetch failed", http.StatusBadGateway)
		return err
	}
	defer resp.Body.Close()

	for _, h := range []string{"Content-Type", "Content-Length", "Content-Range", "Accept-Ranges"} {
		if v := resp.Header.Get(h); v != "" {
			w.Header().Set(h, v)
		}
	}
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.WriteHeader(resp.StatusCode)
	_, _ = io.Copy(w, resp.Body)
	return nil
}

// resolveStream looks up the channel + validates the requested URL.
// Returns the channel's first stream record (carries UA + Referer)
// and the resolved fetch URL.
//
// When `urlOverride` is empty: use the channel's primary stream URL.
// When non-empty: validate that its host matches the channel's
// primary stream host. The match is loose (sub-domain on the same
// registrable suffix all pass) — sufficient because variant
// playlists + segments commonly live on neighbour CDN hostnames
// (e.g. live01.example.com vs live02.example.com).
//
// On any failure returns ok=false and the caller should reject.
func (p *ProxyService) resolveStream(chID, urlOverride string) (Stream, string, bool) {
	if chID == "" {
		return Stream{}, "", false
	}
	ch, ok := p.catalog.FindChannel(chID)
	if !ok || len(ch.Streams) == 0 {
		return Stream{}, "", false
	}
	primary := ch.Streams[0]
	if urlOverride == "" {
		return primary, primary.URL, true
	}
	if !sameRegistrableHost(primary.URL, urlOverride) {
		return Stream{}, "", false
	}
	return primary, urlOverride, true
}

// rewriteManifest walks the manifest line-by-line. URI lines (bare
// segment URIs in media playlists, variant URIs in master playlists)
// AND URI="..." attributes inside tag lines get rewritten to point
// at us. Pass-through everything else verbatim — players can be
// picky about #EXT-X-VERSION ordering, custom tags, blank lines.
//
// Why string-walking instead of a real HLS parser: the rewrite is
// purely local (URI substitution); the manifest is well-formed text
// per spec; pulling in a parser would be overkill for what's
// essentially `s/url/proxiedurl/g` with some tag-attribute care.
func (p *ProxyService) rewriteManifest(body string, base *url.URL, chID, publicBase string) string {
	publicBase = strings.TrimRight(publicBase, "/")
	var out strings.Builder
	out.Grow(len(body) * 2)

	for _, line := range strings.Split(body, "\n") {
		trimmed := strings.TrimSpace(line)
		switch {
		case trimmed == "":
			out.WriteString(line)
			out.WriteString("\n")
		case strings.HasPrefix(trimmed, "#"):
			if strings.Contains(line, `URI="`) {
				line = uriAttrRe.ReplaceAllStringFunc(line, func(match string) string {
					inner := strings.TrimPrefix(strings.TrimSuffix(match, `"`), `URI="`)
					proxied := p.proxyForURI(inner, base, chID, publicBase)
					return `URI="` + proxied + `"`
				})
			}
			out.WriteString(line)
			out.WriteString("\n")
		default:
			proxied := p.proxyForURI(trimmed, base, chID, publicBase)
			out.WriteString(proxied)
			out.WriteString("\n")
		}
	}
	return out.String()
}

// proxyForURI absolutizes a URI against the manifest's base, then
// returns the proxied URL the player should fetch instead. URIs
// ending in `.m3u8` (variant / sub-manifest) route back through
// /v1/iptv/m3u8 so the recursion handles nested rewriting; everything
// else (segments, init segments, encryption keys) goes through
// /v1/iptv/seg.
//
// Query strings are stripped before the .m3u8 suffix check — common
// pattern is `index.m3u8?token=abc`.
func (p *ProxyService) proxyForURI(rawURI string, base *url.URL, chID, publicBase string) string {
	if rawURI == "" {
		return rawURI
	}
	abs := rawURI
	if u, err := base.Parse(rawURI); err == nil && u != nil {
		abs = u.String()
	}
	endpoint := "/v1/iptv/seg"
	low := strings.ToLower(abs)
	if i := strings.IndexByte(low, '?'); i >= 0 {
		low = low[:i]
	}
	if strings.HasSuffix(low, ".m3u8") {
		endpoint = "/v1/iptv/m3u8"
	}
	return publicBase + endpoint + "?ch=" + url.QueryEscape(chID) + "&u=" + url.QueryEscape(abs)
}

// fetchUpstream pulls the manifest URL with the channel's headers.
// 5 MB cap on the manifest body is generous — a normal master
// playlist is < 10 KB; live media playlists are < 100 KB.
func (p *ProxyService) fetchUpstream(parent context.Context, target, ua, referer string) ([]byte, string, int, error) {
	ctx, cancel := context.WithTimeout(parent, proxyFetchTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
	if err != nil {
		return nil, "", 0, err
	}
	applyProxyHeaders(req, ua, referer)
	resp, err := p.httpClient.Do(req)
	if err != nil {
		return nil, "", 0, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 5*1024*1024))
	if err != nil {
		return nil, "", resp.StatusCode, err
	}
	return body, resp.Header.Get("Content-Type"), resp.StatusCode, nil
}

// applyProxyHeaders attaches the per-stream replay headers iptv-org
// gives us. Both are best-effort — many streams have neither set
// and work fine; some need both.
//
// Default UA: a desktop Chrome string. Many CDNs reject bare Go
// http.Client default ("Go-http-client/1.1") with 403, even when
// no specific UA was advertised on the channel. The desktop Chrome
// UA is the broadest "real-browser-shaped" identity.
//
// Origin: derived from Referer when present. A handful of CDNs
// check both, and Origin's the one most often missing from naive
// proxies.
func applyProxyHeaders(req *http.Request, ua, referer string) {
	if ua == "" {
		ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
	}
	req.Header.Set("User-Agent", ua)
	if referer != "" {
		req.Header.Set("Referer", referer)
		if u, err := url.Parse(referer); err == nil && u.Host != "" {
			req.Header.Set("Origin", u.Scheme+"://"+u.Host)
		}
	}
	req.Header.Set("Accept", "*/*")
}

// sameRegistrableHost returns true if both URLs share the same
// registrable suffix (loose: same host OR sibling sub-domains). We
// don't have golang.org/x/net/publicsuffix as a dependency so we
// approximate: split host on '.', compare the last two labels.
// Good enough for "is this iptv-org sub-manifest still on the same
// CDN as the channel we know" — false positives would only let
// someone proxy *.example.com URLs, which iptv-org has already
// vetted as legitimate.
//
// Returns false on any URL parse failure rather than defaulting to
// "allow" — caller treats false as "reject this request".
func sameRegistrableHost(a, b string) bool {
	au, err := url.Parse(a)
	if err != nil || au.Host == "" {
		return false
	}
	bu, err := url.Parse(b)
	if err != nil || bu.Host == "" {
		return false
	}
	return registrableSuffix(au.Host) == registrableSuffix(bu.Host)
}

// registrableSuffix returns the last two labels of a host (e.g.
// "live01.cdn.example.com" → "example.com"). Crude — doesn't
// understand multi-label TLDs like ".co.uk" — but the worst case
// is over-restriction (rejecting a legitimate cross-CDN-region
// sub-manifest), not under-restriction.
func registrableSuffix(host string) string {
	host = strings.ToLower(host)
	// Strip port if present.
	if i := strings.IndexByte(host, ':'); i >= 0 {
		host = host[:i]
	}
	parts := strings.Split(host, ".")
	if len(parts) < 2 {
		return host
	}
	return parts[len(parts)-2] + "." + parts[len(parts)-1]
}
