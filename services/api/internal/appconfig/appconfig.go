// Package appconfig serves a tiny remote-config blob to the TV app.
//
// The TV asks /v1/config at launch (and periodically) and uses the
// response to pick which streamed.pk origin to talk to and which
// feature flags are on. That lets us flip the TV between "talk
// straight to streamed.pk" and "go through our /api proxy" — and
// turn experimental UI on/off — without shipping a new TV build.
//
// Source of truth: a single Firestore doc at config/app. Env var
// STREAMED_BASE_URL_FOR_APP seeds the default the FIRST time we
// boot (and serves as the fallback when Firestore is unreachable
// or the doc doesn't exist yet). Once the doc is created the
// Firestore value wins.
//
// Cache: 60s in-process. Firestore is fast but the TV polls this
// every 30 min — N TVs × every 30 min still adds up at scale, and
// we want config writes to propagate "within a minute" not "in
// real time". 60s is the sweet spot.
package appconfig

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"sync"
	"time"

	"cloud.google.com/go/firestore"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// FirestoreCollection + FirestoreDoc identify the single config doc.
// Constants instead of args because there's exactly one doc and
// "configure the location of your configuration" is yak shaving.
const (
	FirestoreCollection = "config"
	FirestoreDoc        = "app"
)

// DefaultStreamedBaseURL matches what StreamedEndpoints.cs ships
// with — used when neither the env var nor Firestore provides one.
// Keeping the default in BOTH places (TV + backend) is intentional:
// the TV needs SOMETHING to fall back to when /v1/config is
// unreachable, and the backend needs SOMETHING to return when
// Firestore + env var are both missing.
const DefaultStreamedBaseURL = "https://streamed.pk"

// cacheTTL governs how long a fetched Config is reused before we
// re-read Firestore. See package docstring for why 60s.
const cacheTTL = 60 * time.Second

// Config is the JSON shape returned to the TV. Stable: every field
// has a stable JSON name and the TV is permissive about extra
// future fields it doesn't recognize.
//
// FeatureFlags is map[string]any so we can add bool/int/string
// toggles without a schema change. The TV reads keys it knows
// about and ignores the rest.
type Config struct {
	StreamedBaseURL string         `json:"streamedBaseUrl" firestore:"streamedBaseUrl"`
	FeatureFlags    map[string]any `json:"featureFlags,omitempty" firestore:"featureFlags,omitempty"`

	// Source tells operators (and the TV's debug overlay) where
	// this config came from this round trip — "firestore",
	// "env-default", or "stale-cache" when we served a cached
	// value past TTL because Firestore was unreachable.
	Source string `json:"source" firestore:"-"`

	// FetchedAt is RFC3339. Lets the TV's debug overlay show
	// "config from 2026-05-10T12:34:56Z, refreshed 2 min ago".
	FetchedAt string `json:"fetchedAt" firestore:"-"`
}

// Service serves cached Configs. Concurrency-safe; share one across
// HTTP handlers.
type Service struct {
	fs         *firestore.Client
	envDefault string
	logger     *slog.Logger

	mu    sync.Mutex
	cache cachedConfig
}

type cachedConfig struct {
	value    Config
	storedAt time.Time
	hasValue bool
}

// New constructs a Service. fs may be nil — the service still
// works (it just always returns the env-var default). envDefault
// is the seed URL (typically os.Getenv("STREAMED_BASE_URL_FOR_APP")).
// Empty envDefault falls all the way back to DefaultStreamedBaseURL.
func New(fs *firestore.Client, envDefault string, logger *slog.Logger) *Service {
	if logger == nil {
		logger = slog.Default()
	}
	if envDefault == "" {
		envDefault = DefaultStreamedBaseURL
	}
	return &Service{
		fs:         fs,
		envDefault: envDefault,
		logger:     logger,
	}
}

// Get returns the current config. Hits the in-process cache first;
// on miss/stale, reads Firestore; on Firestore error, returns the
// env-var default (or the previously cached value if we have one,
// flagged as stale-cache).
func (s *Service) Get(ctx context.Context) Config {
	s.mu.Lock()
	hit := s.cache
	s.mu.Unlock()
	if hit.hasValue && time.Since(hit.storedAt) < cacheTTL {
		return hit.value
	}

	cfg, err := s.fetchFromFirestore(ctx)
	if err != nil {
		// Stale cache beats env default — the previous Firestore
		// value is "the latest known truth". Mark Source so the TV
		// can show that it's stale.
		if hit.hasValue {
			s.logger.Warn("appconfig: firestore read failed; serving stale cache",
				"ageSec", int(time.Since(hit.storedAt).Seconds()),
				"err", err,
			)
			out := hit.value
			out.Source = "stale-cache"
			out.FetchedAt = time.Now().UTC().Format(time.RFC3339)
			return out
		}
		// No cache, no Firestore — env default. NOT cached so that
		// the next request retries Firestore (the alternative is
		// pinning the env default for cacheTTL after a single
		// transient Firestore error).
		s.logger.Warn("appconfig: firestore read failed; serving env default",
			"err", err,
		)
		return s.envDefaultConfig()
	}

	s.mu.Lock()
	s.cache = cachedConfig{value: cfg, storedAt: time.Now(), hasValue: true}
	s.mu.Unlock()
	return cfg
}

// fetchFromFirestore reads config/app. NotFound is NOT an error —
// it means "no override yet, use env default", which is the
// expected first-deploy state.
func (s *Service) fetchFromFirestore(ctx context.Context) (Config, error) {
	if s.fs == nil {
		return s.envDefaultConfig(), nil
	}
	doc, err := s.fs.Collection(FirestoreCollection).Doc(FirestoreDoc).Get(ctx)
	if err != nil {
		// NotFound means the operator hasn't created config/app yet
		// — return the env default and DO NOT bubble the error up,
		// so the cache logic stamps "env-default" on it.
		if status.Code(err) == codes.NotFound {
			return s.envDefaultConfig(), nil
		}
		return Config{}, fmt.Errorf("appconfig: firestore get: %w", err)
	}

	var cfg Config
	if err := doc.DataTo(&cfg); err != nil {
		return Config{}, fmt.Errorf("appconfig: decode firestore doc: %w", err)
	}
	// Doc may have nil baseUrl — fall back to env default field
	// rather than serving an empty string the TV will choke on.
	if cfg.StreamedBaseURL == "" {
		cfg.StreamedBaseURL = s.envDefault
	}
	cfg.Source = "firestore"
	cfg.FetchedAt = time.Now().UTC().Format(time.RFC3339)
	return cfg, nil
}

func (s *Service) envDefaultConfig() Config {
	return Config{
		StreamedBaseURL: s.envDefault,
		FeatureFlags:    map[string]any{},
		Source:          "env-default",
		FetchedAt:       time.Now().UTC().Format(time.RFC3339),
	}
}

// guard against unused-import noise in the package's exported surface
var _ = errors.New
