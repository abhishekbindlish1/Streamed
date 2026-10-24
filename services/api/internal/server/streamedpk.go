package server

import (
	"net/http"
	"strconv"

	"github.com/abindlish/streamed-backend/internal/streamedpk"
)

// WithStreamedPK wires the streamed.pk catalog proxy. When unset all
// /api/* routes return 503 — useful in local dev where you want to
// run the rest of the backend without the upstream dependency.
func (s *Server) WithStreamedPK(svc *streamedpk.Service) *Server {
	s.streamedPK = svc
	return s
}

// handleStreamedPKProxy is the single handler that backs every
// /api/... route we mirror from streamed.pk. We forward the EXACT
// path (and query string, when present — streamed.pk doesn't use
// query params on these endpoints today, but appending them costs
// nothing and future-proofs the proxy) and write the upstream's
// status, body, and content-type back unchanged.
//
// The grand point of this handler is "make the TV's existing client
// code work against our origin without any TV-side changes". The
// only divergence from a literal byte-for-byte passthrough is the
// X-Cache-Age-Seconds header, which lets the TV (and operators
// poking with curl -i) see how stale a response is.
func (s *Server) handleStreamedPKProxy(w http.ResponseWriter, r *http.Request) {
	if s.streamedPK == nil {
		writeError(w, http.StatusServiceUnavailable, "streamed.pk proxy disabled")
		return
	}
	upstreamPath := r.URL.Path
	if r.URL.RawQuery != "" {
		upstreamPath += "?" + r.URL.RawQuery
	}

	resp, err := s.streamedPK.Get(r.Context(), upstreamPath)
	if err != nil {
		s.logger.Warn("streamed.pk proxy fetch failed",
			"path", upstreamPath,
			"err", err,
		)
		writeError(w, http.StatusBadGateway, "upstream failure")
		return
	}

	// Echo the upstream content-type unchanged. For JSON this is
	// "application/json"; for image/webp this is "image/webp" —
	// the TV's WebP decoders rely on this.
	if resp.ContentType != "" {
		w.Header().Set("Content-Type", resp.ContentType)
	}
	// Long-cache the immutable image responses on the public CDN/
	// browser side. The catalog endpoints are intentionally NOT
	// cached at the HTTP layer — our in-process cache already
	// handles that and we want a fresh render whenever the TV asks.
	if isImagePath(upstreamPath) {
		w.Header().Set("Cache-Control", "public, max-age=86400")
	}
	w.Header().Set("X-Cache-Age-Seconds", strconv.Itoa(int(resp.CachedAge.Seconds())))

	status := resp.Status
	if status == 0 {
		status = http.StatusOK
	}
	w.WriteHeader(status)
	_, _ = w.Write(resp.Body)
}

// isImagePath checks for /api/images/ — the only endpoint family
// where a long Cache-Control is appropriate.
func isImagePath(p string) bool {
	return len(p) >= len("/api/images/") && p[:len("/api/images/")] == "/api/images/"
}
