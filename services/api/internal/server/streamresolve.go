// Stream URL resolver endpoint — bypass cross-origin iframe wrappers.
//
// Tizen's HW VideoHole compositor can punch a hole through the
// chromium-efl layer tree to a hardware video plane, but ONLY when
// the <video> element lives in the top frame. Cross-origin iframes
// are a separate composited layer the HW plane can't reach. The
// observable failure mode is "audio plays, video stays black."
//
// embedsports.top is one such wrapper: it's a tiny page whose only
// real content is an iframe pointing at the actual player host
// (pooembed.eu, etc.). If we can extract that inner iframe URL and
// hand IT to the WebView instead of the wrapper, the player ends up
// in the top frame and the compositor can see its video.
//
// This endpoint does the extraction server-side so the TV doesn't
// need an HTML parser. Auth-required to keep it from being abused
// as a generic open-fetch proxy. SSRF-protected by an allowlist of
// wrapper hosts — only those URLs are fetched server-side.
package server

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

const (
	streamResolveTimeout  = 1500 * time.Millisecond
	streamResolveMaxBytes = 64 * 1024
	streamResolveUA       = "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko)"
)

// allowedWrapperHosts: only resolve URLs whose host is in this set.
// Server-side fetch of arbitrary client-supplied URLs is the canonical
// SSRF vector — locking down to known wrapper hosts removes the
// abuse surface. Add hosts here as we encounter new wrappers.
var allowedWrapperHosts = map[string]bool{
	"embedsports.top":     true,
	"www.embedsports.top": true,
}

// noBypassInnerHosts are hosts whose inner pages SELF-ABORT when
// loaded as the top frame (anti-hotlink check via
// `window.self !== window.top`). Bypassing the wrapper for these
// makes the page go BLANK rather than fixing it — chromium-efl
// marks `window.top` non-configurable, so we can't JS-spoof past
// the check from the WebView side.
//
// For these wrappers we still extract the iframe (so the resolver
// reports the architecture), but return the ORIGINAL URL so the
// TV loads the wrapper as before. The user gets back to the
// pre-resolver state for these specific streams (audio plays,
// video frame stays 0x0 due to cross-origin HW VideoHole
// compositor blindness — that's a separate, harder problem
// requiring either a backend proxy or native MediaPlayer
// extraction of the m3u8).
var noBypassInnerHosts = map[string]bool{
	"pooembed.eu":     true,
	"www.pooembed.eu": true,
}

// iframeSrcRe extracts the first iframe `src` attribute we encounter.
// Tolerates the common attribute orderings + quote styles wrappers
// emit (single-quoted, double-quoted, attributes before src). The
// match is intentionally non-greedy on src content so we don't slurp
// across attribute boundaries.
var iframeSrcRe = regexp.MustCompile(`(?i)<iframe[^>]*\bsrc\s*=\s*["']([^"']+)["']`)

// streamResolveResp is the JSON shape returned to the TV. Always
// returns 200 (even on extraction failure) with a self-describing
// `strategy` field — TV uses that to decide whether to use the
// resolved URL or fall back to its original.
type streamResolveResp struct {
	Resolved string `json:"resolved"`
	Strategy string `json:"strategy"`
	Target   string `json:"target,omitempty"`
	Error    string `json:"error,omitempty"`
}

func (s *Server) handleStreamResolve(w http.ResponseWriter, r *http.Request) {
	if s.auth == nil {
		writeError(w, http.StatusServiceUnavailable, "auth not configured")
		return
	}
	if _, err := s.auth.AuthenticateBearer(r.Context(), r.Header.Get("Authorization")); err != nil {
		writeError(w, http.StatusUnauthorized, "invalid or missing token")
		return
	}

	raw := strings.TrimSpace(r.URL.Query().Get("url"))
	if raw == "" {
		writeError(w, http.StatusBadRequest, "missing url")
		return
	}
	parsed, err := url.Parse(raw)
	if err != nil || parsed.Scheme != "https" {
		writeError(w, http.StatusBadRequest, "url must be https")
		return
	}
	if !allowedWrapperHosts[strings.ToLower(parsed.Host)] {
		// Host not on allowlist — return original unchanged. NOT an
		// error: TV calls this opportunistically for every stream
		// load and skipping unknown hosts is the expected path.
		writeJSON(w, http.StatusOK, streamResolveResp{
			Resolved: raw,
			Strategy: "skip-not-wrapper",
		})
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), streamResolveTimeout)
	defer cancel()

	body, fetchErr := fetchWrapperHTML(ctx, raw)
	if fetchErr != nil {
		s.logger.Warn("stream-resolve fetch failed", "url", raw, "err", fetchErr)
		writeJSON(w, http.StatusOK, streamResolveResp{
			Resolved: raw,
			Strategy: "skip-fetch-error",
			Error:    fetchErr.Error(),
		})
		return
	}

	// Find ALL iframes and pick the first whose RESOLVED host
	// differs from the wrapper host. The wrapper page often contains
	// MULTIPLE iframes — the actual player (cross-origin, e.g.
	// pooembed.eu) and one or more wrapper-hosted ad/close-button
	// iframes (e.g. /ad.html, /close.html). Source order isn't
	// stable across stream pages: for some streams the ad iframe
	// comes first. Picking by "host differs from wrapper" is the
	// robust signal — the WHOLE POINT of resolution is to bypass a
	// CROSS-ORIGIN iframe, so a same-host iframe is by definition
	// the wrong target.
	matches := iframeSrcRe.FindAllStringSubmatch(body, -1)
	if len(matches) == 0 {
		writeJSON(w, http.StatusOK, streamResolveResp{
			Resolved: raw,
			Strategy: "skip-no-iframe",
		})
		return
	}

	wrapperHost := strings.ToLower(parsed.Host)
	var chosen *url.URL
	var skipReason string
	for _, m := range matches {
		extracted := strings.TrimSpace(m[1])
		if extracted == "" {
			continue
		}
		extractedURL, perr := parsed.Parse(extracted)
		if perr != nil || extractedURL == nil {
			continue
		}
		// Skip non-https targets — chromium-efl on Tizen mixes-content-
		// blocks http content under https origins, and we don't want to
		// silently downgrade.
		if extractedURL.Scheme != "https" {
			skipReason = "non-https"
			continue
		}
		// Skip same-host (almost always ads or close-buttons hosted
		// on the wrapper itself — we don't want to load an ad page
		// AS the stream).
		if strings.EqualFold(extractedURL.Host, wrapperHost) {
			skipReason = "same-host-as-wrapper"
			continue
		}
		// Skip URLs that smell like ads even when cross-origin
		// (defence in depth — most ad networks live on different
		// hosts). Conservative pattern, just blocks the obvious /ad
		// path; we keep the same-host check as the primary defence.
		if strings.Contains(strings.ToLower(extractedURL.Path), "/ad.html") {
			skipReason = "ad-path"
			continue
		}
		chosen = extractedURL
		break
	}

	if chosen == nil {
		writeJSON(w, http.StatusOK, streamResolveResp{
			Resolved: raw,
			Strategy: "skip-no-cross-origin-iframe-" + skipReason,
		})
		return
	}

	resolved := chosen.String()
	if resolved == raw {
		// Nothing actually changed — extraction yielded the same URL.
		writeJSON(w, http.StatusOK, streamResolveResp{
			Resolved: raw,
			Strategy: "skip-unchanged",
		})
		return
	}

	// Inner host has the self !== top anti-hotlink check —
	// bypassing makes it blank instead of fixing it. Return the
	// original URL with a self-describing strategy so the TV
	// loads the wrapper unchanged AND telemetry shows we
	// recognised the case.
	chosenHost := strings.ToLower(chosen.Host)
	if noBypassInnerHosts[chosenHost] {
		writeJSON(w, http.StatusOK, streamResolveResp{
			Resolved: raw,
			Strategy: "skip-inner-self-abort",
			Target:   chosenHost,
		})
		return
	}

	writeJSON(w, http.StatusOK, streamResolveResp{
		Resolved: resolved,
		Strategy: "iframe-extract",
		Target:   chosenHost,
	})
}

func fetchWrapperHTML(ctx context.Context, raw string) (string, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, raw, nil)
	if err != nil {
		return "", err
	}
	// Mimic a Tizen TV WebView so wrappers serve us the same HTML
	// they'd serve the actual user agent. Some wrappers UA-sniff and
	// route mobile/desktop/TV to different inner iframes.
	req.Header.Set("User-Agent", streamResolveUA)
	req.Header.Set("Accept", "text/html")

	client := &http.Client{
		Timeout: streamResolveTimeout,
		// Allow up to 3 redirects but no more — wrappers often
		// 30x to the canonical URL once. Break loops aggressively.
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= 3 {
				return fmt.Errorf("too many redirects (%d)", len(via))
			}
			return nil
		},
	}
	resp, err := client.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 400 {
		return "", fmt.Errorf("upstream %d", resp.StatusCode)
	}

	body, err := io.ReadAll(io.LimitReader(resp.Body, streamResolveMaxBytes))
	if err != nil {
		return "", err
	}
	return string(body), nil
}
