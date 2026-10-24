// Package streamedpk is a thin caching proxy in front of the public
// streamed.pk catalog (https://streamed.pk/api/...). The TV app
// already speaks the streamed.pk wire format end-to-end, so we don't
// transform anything — body bytes go in, body bytes come out, with a
// per-path TTL in between.
//
// Why proxy at all when the TV could just hit streamed.pk directly:
//   - streamed.pk is occasionally flaky (timeouts, 5xx, empty arrays).
//     Caching the last known-good body lets us serve stale instead of
//     showing the user an empty grid.
//   - Origin Cloudflare gates can rate-limit a noisy TV. One backend
//     fan-in is friendlier than N TVs hammering the same endpoint.
//   - Gives us an observability + drop-in-replacement seam for later.
//
// The cache is intentionally per-process (no Redis, no Firestore).
// streamed.pk's payloads are small, the working set fits trivially in
// memory, and a Cloud Run instance restart just means one cold round
// trip — no consistency story to worry about.
package streamedpk

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strings"
	"sync"
	"time"
)

// DefaultBaseURL is the canonical streamed.pk origin. Override via
// New(baseURL=...) only if you're pointing at a mirror.
const DefaultBaseURL = "https://streamed.pk"

// Default body cap on upstream responses. The fattest payload we've
// seen (matches/all) is ~600 KB; 8 MB is generous-but-bounded.
const maxBodyBytes = 8 * 1024 * 1024

// Service is a tiny GET-only proxy with a per-path in-memory cache.
// Concurrent-safe; share one instance across handlers.
type Service struct {
	httpClient *http.Client
	baseURL    string
	logger     *slog.Logger

	mu    sync.Mutex
	cache map[string]cachedEntry
}

type cachedEntry struct {
	body        []byte
	contentType string
	status      int
	storedAt    time.Time
	ttl         time.Duration
}

// fresh reports whether the entry is still inside its TTL window.
func (e cachedEntry) fresh() bool {
	return time.Since(e.storedAt) < e.ttl
}

// Response is what the handler writes back to the TV. We pass status
// through so a deliberate upstream 404 (e.g. unknown sport) doesn't
// get rewritten to 200.
type Response struct {
	Status      int
	ContentType string
	Body        []byte
	// CachedAge is "0s" on a fresh fetch and grows as we serve from
	// cache. Useful in logs / response headers to see how stale the
	// payload is.
	CachedAge time.Duration
}

// New builds a Service. logger may be nil (defaults to slog.Default).
// baseURL defaults to DefaultBaseURL when empty. Pass a pre-wired
// httpClient for tests; nil yields a sensible default with a 12s
// per-request timeout (streamed.pk's slowest endpoint we've measured
// is matches/all at ~3-4s; 12s leaves headroom without leaking
// goroutines forever).
func New(baseURL string, httpClient *http.Client, logger *slog.Logger) *Service {
	if baseURL == "" {
		baseURL = DefaultBaseURL
	}
	if httpClient == nil {
		httpClient = &http.Client{Timeout: 12 * time.Second}
	}
	if logger == nil {
		logger = slog.Default()
	}
	return &Service{
		httpClient: httpClient,
		baseURL:    strings.TrimRight(baseURL, "/"),
		logger:     logger,
		cache:      make(map[string]cachedEntry),
	}
}

// Get fetches `path` from the upstream catalog with TTL-based caching.
// `path` MUST start with "/api/" — anything else is a programming
// error (we don't proxy arbitrary paths and there's no escape hatch).
//
// On upstream failure: if a stale cached entry exists, serve it and
// log a warning — the TV's grid keeps working through transient
// outages. With no cache to fall back to, return the error.
func (s *Service) Get(ctx context.Context, path string) (Response, error) {
	if !strings.HasPrefix(path, "/api/") {
		return Response{}, fmt.Errorf("streamedpk: refused non-/api/ path %q", path)
	}

	ttl := ttlFor(path)
	cacheKey := path

	s.mu.Lock()
	hit, ok := s.cache[cacheKey]
	s.mu.Unlock()
	if ok && hit.fresh() {
		return Response{
			Status:      hit.status,
			ContentType: hit.contentType,
			Body:        hit.body,
			CachedAge:   time.Since(hit.storedAt),
		}, nil
	}

	body, ct, status, err := s.fetch(ctx, path)
	if err != nil {
		if ok {
			s.logger.Warn("streamedpk upstream failed; serving stale",
				"path", path,
				"ageSec", int(time.Since(hit.storedAt).Seconds()),
				"err", err,
			)
			return Response{
				Status:      hit.status,
				ContentType: hit.contentType,
				Body:        hit.body,
				CachedAge:   time.Since(hit.storedAt),
			}, nil
		}
		return Response{}, err
	}

	// Don't cache 5xx — a transient upstream blip shouldn't pin a
	// bad response for `ttl` seconds. 4xx IS cached because the TV
	// should see consistent "this sport doesn't exist" answers
	// instead of waves of upstream load.
	if status < 500 {
		s.mu.Lock()
		s.cache[cacheKey] = cachedEntry{
			body:        body,
			contentType: ct,
			status:      status,
			storedAt:    time.Now(),
			ttl:         ttl,
		}
		s.mu.Unlock()
	}

	return Response{
		Status:      status,
		ContentType: ct,
		Body:        body,
		CachedAge:   0,
	}, nil
}

// fetch does the raw upstream GET and reads the body. Returns the
// body bytes, the upstream Content-Type (so the proxy can echo it
// back unchanged — JSON for catalog endpoints, image/webp for
// /api/images/*), and the upstream HTTP status.
func (s *Service) fetch(ctx context.Context, path string) ([]byte, string, int, error) {
	url := s.baseURL + path
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, "", 0, fmt.Errorf("streamedpk: build req: %w", err)
	}
	// Browser-shaped UA — streamed.pk's CDN edge has been observed
	// returning empty arrays to "go-http-client/..." UAs. Keep this
	// in lockstep with what the TV sends so we don't get a different
	// Cloudflare bot-score class.
	req.Header.Set("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36")
	req.Header.Set("Accept", acceptFor(path))
	req.Header.Set("Referer", s.baseURL+"/")

	resp, err := s.httpClient.Do(req)
	if err != nil {
		return nil, "", 0, fmt.Errorf("streamedpk: http %s: %w", path, err)
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(io.LimitReader(resp.Body, maxBodyBytes))
	if err != nil {
		return nil, "", 0, fmt.Errorf("streamedpk: read %s: %w", path, err)
	}
	ct := resp.Header.Get("Content-Type")
	if ct == "" {
		ct = defaultContentTypeFor(path)
	}
	return body, ct, resp.StatusCode, nil
}

// ttlFor picks a cache lifetime for the given upstream path. Tuned
// for "TV refresh feels live without hammering streamed.pk":
//
//	/api/matches/live*       — 15s   (in-progress fixtures, ticking)
//	/api/matches/all-today*  — 60s   (today's slate, status flips)
//	/api/matches/all*        — 60s   (catalog index, popularity drifts)
//	/api/matches/{sport}*    — 60s   (per-sport listing)
//	/api/sports              — 1h    (sport list barely changes)
//	/api/stream/{src}/{id}   — 20s   (signed manifest URLs expire fast)
//	/api/images/*            — 24h   (badges/posters are immutable)
//
// Unknown /api/ paths get 30s — a safe middle ground that won't pin
// a bad response forever but won't pass through every request either.
func ttlFor(path string) time.Duration {
	switch {
	case strings.HasPrefix(path, "/api/matches/live"):
		return 15 * time.Second
	case strings.HasPrefix(path, "/api/matches/all-today"):
		return 60 * time.Second
	case strings.HasPrefix(path, "/api/matches/all"):
		return 60 * time.Second
	case strings.HasPrefix(path, "/api/matches/"):
		return 60 * time.Second
	case path == "/api/sports":
		return 1 * time.Hour
	case strings.HasPrefix(path, "/api/stream/"):
		return 20 * time.Second
	case strings.HasPrefix(path, "/api/images/"):
		return 24 * time.Hour
	}
	return 30 * time.Second
}

// acceptFor sets a sensible Accept header — JSON for catalog,
// image/webp for image paths. Mostly cosmetic but keeps logs clean
// when streamed.pk's edge sniffs Accept for content negotiation.
func acceptFor(path string) string {
	if strings.HasPrefix(path, "/api/images/") {
		return "image/webp,image/*;q=0.8,*/*;q=0.5"
	}
	return "application/json"
}

// defaultContentTypeFor is the fallback when upstream doesn't send a
// Content-Type. Should never trigger in practice, but keeps the
// browser from guessing wrong if it does.
func defaultContentTypeFor(path string) string {
	if strings.HasPrefix(path, "/api/images/") {
		return "image/webp"
	}
	return "application/json"
}

// guards against unused-import noise when the package is imported
// for its constructor only.
var _ = errors.New
