// Command streamed-backend is a small HTTP service that enriches the
// Streamed TV app with team, league, and fixture data.
package main

import (
	"context"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"github.com/abindlish/streamed-backend/internal/appconfig"
	"github.com/abindlish/streamed-backend/internal/auth"
	"github.com/abindlish/streamed-backend/internal/cron"
	"github.com/abindlish/streamed-backend/internal/data"
	"github.com/abindlish/streamed-backend/internal/iptv"
	"github.com/abindlish/streamed-backend/internal/livescore"
	"github.com/abindlish/streamed-backend/internal/posters"
	"github.com/abindlish/streamed-backend/internal/profiles"
	"github.com/abindlish/streamed-backend/internal/server"
	"github.com/abindlish/streamed-backend/internal/streamedpk"

	"cloud.google.com/go/firestore"
)

func main() {
	logger := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(logger)

	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}

	// Always have a stub source available. It's the terminal fallback
	// for the read path on cold-start (before the cron has populated
	// Firestore) and the inner Source for the cron's writer clients.
	stub := data.NewStubSource()
	var source data.Source = stub

	// --- READ PATH ---------------------------------------------------
	// In the new architecture the request path serves ONLY from
	// Firestore. A background cron (wired below) is the sole writer.
	// Cache misses return 404; we never call upstream APIs synchronously
	// during request handling. This keeps p99 bounded (Firestore-only),
	// removes LLM dependencies, and means upstream rate limits never
	// surface to TV users.
	//
	// The OpenAI + Groq sources are intentionally NOT wired into the
	// read path. Their code lives in internal/data/{openai,groq}.go and
	// can be re-enabled by re-adding the constructor calls here.
	openAIKey := os.Getenv("OPENAI_API_KEY")
	footballDataKey := os.Getenv("FOOTBALL_DATA_KEY")
	cricKey := os.Getenv("CRIC_API_KEY")
	cricEnabled := strings.EqualFold(strings.TrimSpace(os.Getenv("CRIC_ENABLED")), "true")

	// Poster + logo storage. STORAGE_BUCKET=foo means use GCS at
	// gs://foo/posters and gs://foo/logos; unset means local disk under
	// ./data. Wired even when the cron is off so the HTTP serve path
	// can hand back any previously generated PNGs.
	rootCtx, rootCancel := context.WithCancel(context.Background())
	defer rootCancel()

	// Firestore cache. In read-only mode it returns ErrNotFound on miss
	// (no upstream calls). The same instance is handed to the sports
	// cron below — its WriteX methods bypass the readOnly gate so the
	// cron can populate this cache while requests still serve from it.
	cacheSource := buildFirestoreCacheSource(rootCtx, stub, logger)
	if cacheSource != nil {
		cacheSource.SetReadOnly(true)
		source = cacheSource
		logger.Info("firestore cache enabled (read-only request path)")
	} else {
		logger.Warn("firestore cache disabled — read path will only serve stub data")
	}

	// Fuzzy team-name fallback. Wraps the read-path source so that
	// LookupTeamByName tries the alias path first, then falls back to a
	// scored match against teamCatalog before returning 404. Threshold
	// kept low enough to catch typos ("Manchstr Utd") but high enough
	// to reject totally unrelated queries.
	source = data.NewFuzzyLookupSource(source, 0.55, logger)
	logger.Info("fuzzy lookup wrapper enabled", "minScore", 0.55)

	// --- WRITER CLIENTS ---------------------------------------------
	// Constructed independently from the read chain. The cron uses
	// these directly (LeagueStandings / LeagueNextMatchPerTeam /
	// SearchSeries / SeriesNextMatchPerTeam) and writes results to the
	// cache via the public WriteX methods.
	//
	// Each writer's `next` is the stub — they never delegate during
	// cron use, but the constructor requires a non-nil Source.
	var footballWriter *data.FootballDataSource
	if footballDataKey != "" {
		footballWriter = data.NewFootballDataSource(footballDataKey, stub, logger)
		logger.Info("football-data writer enabled")
	} else {
		logger.Info("football-data writer disabled (no FOOTBALL_DATA_KEY)")
	}
	var cricWriter *data.CricAPISource
	if cricKey != "" && cricEnabled {
		cricWriter = data.NewCricAPISource(cricKey, stub, logger)
		logger.Info("cricapi writer enabled")
	} else {
		if cricKey != "" {
			logger.Info("cricapi writer disabled (set CRIC_ENABLED=true to enable)")
		} else {
			logger.Info("cricapi writer disabled (no CRIC_API_KEY)")
		}
	}

	srv := server.New(source, logger)

	// Hand the typed cache to the server so the prebuilt-feed handlers
	// (e.g. /v1/cricket/international/upcoming) can read feeds the
	// sports cron publishes via WriteFeed. When the cache is unwired
	// (no FIRESTORE_PROJECT) those routes 503.
	if cacheSource != nil {
		srv = srv.WithFeedCache(cacheSource)
		logger.Info("feed cache wired (read path)")
	}

	posterStore, logoStore, storeKind := buildStores(rootCtx, logger)

	// Pick an image provider. Defaults to "pollinations" because it's
	// free and requires no API key — the OpenAI account this project
	// was bootstrapped against has a $0 hard limit, so the OpenAI path
	// 400s out of the gate. Set IMAGE_PROVIDER=openai (and
	// OPENAI_API_KEY) to use OpenAI Images instead.
	provider := strings.ToLower(strings.TrimSpace(os.Getenv("IMAGE_PROVIDER")))
	if provider == "" {
		provider = "pollinations"
	}
	var imgClient posters.ImageClient
	switch provider {
	case "openai":
		if openAIKey != "" {
			imgClient = posters.NewOpenAIImageClient(openAIKey, os.Getenv("OPENAI_IMAGE_MODEL"))
			logger.Info("image provider", "name", "openai", "model", firstNonEmpty(os.Getenv("OPENAI_IMAGE_MODEL"), "gpt-image-1"))
		} else {
			logger.Warn("IMAGE_PROVIDER=openai but OPENAI_API_KEY is unset; image generation disabled")
		}
	case "pollinations":
		imgClient = posters.NewPollinationsImageClient(os.Getenv("POLLINATIONS_MODEL"))
		logger.Info("image provider", "name", "pollinations", "model", firstNonEmpty(os.Getenv("POLLINATIONS_MODEL"), "flux"))
	default:
		logger.Warn("unknown IMAGE_PROVIDER, falling back to pollinations", "value", provider)
		imgClient = posters.NewPollinationsImageClient(os.Getenv("POLLINATIONS_MODEL"))
	}
	posterGen := posters.NewGeneratorWithStore(posterStore, logoStore, imgClient, logger)
	logger.Info("poster generator wired", "store", storeKind, "describe", posterGen.Describe())

	// Cron is opt-in. Without POSTER_CRON_ENABLED=true we don't tick
	// at all — but the manual refresh endpoint is still wired.
	cronJob := cron.New(cron.Config{
		Generator: posterGen,
		Interval:  cron.IntervalFromEnv(),
		Logger:    logger,
	})
	srv = srv.WithPosters(posterGen, cronJob)

	// Live cricket scores (RapidAPI). Optional — gated by RAPIDAPI_KEY.
	// Without the key the /v1/live/* routes return 503 and the TV's
	// score ticker silently no-ops. Host overridable via RAPIDAPI_HOST
	// (default: cricket-api-free-data.p.rapidapi.com).
	rapidKey := strings.TrimSpace(os.Getenv("RAPIDAPI_KEY"))
	if rapidKey != "" {
		live := livescore.New(rapidKey, os.Getenv("RAPIDAPI_HOST"), logger)
		srv = srv.WithLiveScore(live)
		logger.Info("livescore service enabled", "host", livescore.DefaultHost)
	} else {
		logger.Info("livescore service disabled (set RAPIDAPI_KEY to enable)")
	}

	// Auth (TV pairing + JWT). Wired only when ALL three are set:
	// FIRESTORE_PROJECT (the GCP project), GOOGLE_CLIENT_ID (the
	// OAuth client ID for the web pairing page), and JWT_SIGNING_KEY
	// (>= 32 bytes; the HS256 key we sign session tokens with).
	// Without all three the /v1/auth/* + /v1/me routes return 503,
	// which lets local dev work without setting up Firestore /
	// Google OAuth.
	authSvc := buildAuthService(rootCtx, logger)
	if authSvc != nil {
		srv = srv.WithAuth(authSvc)
		logger.Info("auth service enabled")
	} else {
		logger.Info("auth service disabled (set FIRESTORE_PROJECT + GOOGLE_CLIENT_ID + JWT_SIGNING_KEY to enable)")
	}

	// Profiles share Firestore with auth, so we wire them off the
	// same project. Disabled when auth is disabled — profile routes
	// would 401 without auth anyway.
	profilesSvc := buildProfilesService(rootCtx, logger)
	if profilesSvc != nil && authSvc != nil {
		srv = srv.WithProfiles(profilesSvc)
		logger.Info("profiles service enabled")
	} else if authSvc != nil {
		logger.Info("profiles service disabled (firestore client init failed)")
	}

	// Crash log ingestion. Wired only when STORAGE_BUCKET is set
	// (we don't bother with a local fallback — crash logs from a
	// remote TV are useless if they only land on the backend host
	// disk). Auth is required by the handler; if auth isn't
	// configured the route still 503s cleanly.
	crashStore := buildCrashLogStore(rootCtx, logger)
	if crashStore != nil {
		srv = srv.WithCrashLogStore(crashStore)
		logger.Info("crash-log store enabled", "describe", crashStore.Describe())
	} else {
		logger.Info("crash-log store disabled (set STORAGE_BUCKET to enable)")
	}

	// iptv-org catalog. Always wired — fetches from the public
	// GitHub Pages mirror, no auth required, no env var gates
	// since there's nothing to misconfigure. Cache + filter +
	// the HTML player wrapper all live in the iptv package.
	iptvSvc := iptv.New(nil)
	srv = srv.WithIPTV(iptvSvc)
	logger.Info("iptv-org catalog enabled")

	// streamed.pk catalog proxy. Always wired — same reasoning as
	// iptv-org: no auth, no quota, nothing to misconfigure. Lets
	// the TV point StreamedEndpoints.BaseUrl at our origin and get
	// the same /api/... surface with TTL caching + stale-on-error.
	// Override the upstream via STREAMED_PK_BASE_URL (e.g. when
	// pointing at a mirror or a local mock for tests).
	streamedPKSvc := streamedpk.New(strings.TrimSpace(os.Getenv("STREAMED_PK_BASE_URL")), nil, logger)
	srv = srv.WithStreamedPK(streamedPKSvc)
	logger.Info("streamed.pk proxy enabled")

	// Remote app config (/v1/config). Always wired — Firestore is
	// optional (without FIRESTORE_PROJECT we serve the env-var
	// default, which seeds from STREAMED_BASE_URL_FOR_APP). The TV
	// calls this on every launch; never returns 503.
	appCfgSvc := buildAppConfigService(rootCtx, logger)
	srv = srv.WithAppConfig(appCfgSvc)
	logger.Info("app config service enabled",
		"firestoreBacked", strings.TrimSpace(os.Getenv("FIRESTORE_PROJECT")) != "",
	)

	if cron.Enabled() {
		if imgClient == nil {
			logger.Warn("poster cron enabled but no image client is configured; runs will skip generation", "provider", provider)
		}
		cronJob.Start(rootCtx)
		logger.Info("poster cron enabled", "interval", cron.IntervalFromEnv())
	} else {
		logger.Info("poster cron disabled (set POSTER_CRON_ENABLED=true to enable)")
	}

	// Sports cron — league-driven pre-seeding of the Firestore cache.
	// Wired only when the cache is up AND at least one writer client is
	// configured. Order in FootballLeagues controls which league becomes
	// the team's "primary standings" doc (last write wins) — list
	// continental cups FIRST so domestic leagues stick.
	if cacheSource != nil && (footballWriter != nil || cricWriter != nil) {
		currentYear := time.Now().UTC().Year()
		sportsCron := cron.NewSports(cron.SportsConfig{
			Cache:           cacheSource,
			Football:        footballWriter,
			FootballLeagues: []string{"ucl", "epl", "laliga", "seriea", "bundesliga", "ligue1"},
			Cricket:         cricWriter,
			CricketSeriesQueries: []string{
				fmt.Sprintf("Indian Premier League %d", currentYear),
				fmt.Sprintf("Indian Premier League %d", currentYear+1),
				fmt.Sprintf("tour of India %d", currentYear),
				fmt.Sprintf("tour of Australia %d", currentYear),
				fmt.Sprintf("tour of England %d", currentYear),
				fmt.Sprintf("tour of Pakistan %d", currentYear),
			},
			Logger: logger,
		})
		sportsCron.Start(rootCtx)
		logger.Info("sports cron enabled", "interval", cron.DefaultSportsInterval)
	} else {
		logger.Info("sports cron disabled (cache or writer clients unavailable)")
	}

	httpSrv := &http.Server{
		Addr:              ":" + port,
		Handler:           srv.Handler(),
		ReadHeaderTimeout: 10 * time.Second,
	}

	// Run the HTTP server in a goroutine so we can listen for signals
	// in main.
	errCh := make(chan error, 1)
	go func() {
		logger.Info("listening", "addr", httpSrv.Addr)
		if err := httpSrv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			errCh <- err
		}
	}()

	// Block until SIGINT/SIGTERM (or a startup error).
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	select {
	case sig := <-sigCh:
		logger.Info("shutdown signal", "signal", sig.String())
	case err := <-errCh:
		logger.Error("server failed", "err", err)
	}

	rootCancel()

	if err := srv.Shutdown(context.Background(), httpSrv); err != nil {
		logger.Error("graceful shutdown failed", "err", err)
		os.Exit(1)
	}
	logger.Info("shutdown complete")
}

func firstNonEmpty(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

// buildStores chooses between GCS and local-disk asset storage based
// on env vars. STORAGE_BUCKET=foo opts into GCS at gs://foo/{posters,logos};
// unset falls back to ./data/{posters,logos} on the container disk.
//
// On GCS init failure we don't fatal — fall through to the local store
// so the HTTP serve path still works (just without persistence). The
// log line tells operators what happened.
func buildStores(ctx context.Context, logger *slog.Logger) (poster, logo posters.Store, kind string) {
	bucket := strings.TrimSpace(os.Getenv("STORAGE_BUCKET"))
	if bucket == "" {
		return posters.NewLocalStore(filepath.Join("data", "posters")),
			posters.NewLocalStore(filepath.Join("data", "logos")),
			"local"
	}
	pStore, err := posters.NewGCSStore(ctx, bucket, "posters")
	if err != nil {
		logger.Error("posters: GCS poster store init failed; falling back to local disk", "bucket", bucket, "err", err)
		return posters.NewLocalStore(filepath.Join("data", "posters")),
			posters.NewLocalStore(filepath.Join("data", "logos")),
			"local-fallback"
	}
	lStore, err := posters.NewGCSStore(ctx, bucket, "logos")
	if err != nil {
		logger.Error("posters: GCS logo store init failed; falling back to local disk", "bucket", bucket, "err", err)
		return posters.NewLocalStore(filepath.Join("data", "posters")),
			posters.NewLocalStore(filepath.Join("data", "logos")),
			"local-fallback"
	}
	return pStore, lStore, "gcs"
}

// buildAuthService wires the pairing+JWT auth service when the
// required env vars are present. Returns nil when ANY required var
// is missing — the server then serves 503 on /v1/auth/* + /v1/me,
// which is exactly what we want in local dev where Firestore +
// Google OAuth aren't set up.
//
// Required env:
//   FIRESTORE_PROJECT  — GCP project ID hosting Firestore
//   GOOGLE_CLIENT_ID   — OAuth client ID for the web pairing page
//                        (used as audience check on Google ID tokens)
//   JWT_SIGNING_KEY    — HS256 signing key, >= 32 bytes
func buildAuthService(ctx context.Context, logger *slog.Logger) *auth.Service {
	project := strings.TrimSpace(os.Getenv("FIRESTORE_PROJECT"))
	clientID := strings.TrimSpace(os.Getenv("GOOGLE_CLIENT_ID"))
	signingKey := strings.TrimSpace(os.Getenv("JWT_SIGNING_KEY"))
	if project == "" || clientID == "" || signingKey == "" {
		return nil
	}
	if len(signingKey) < 32 {
		logger.Error("auth: JWT_SIGNING_KEY too short (need >=32 bytes); auth disabled", "len", len(signingKey))
		return nil
	}
	fsClient, err := firestore.NewClient(ctx, project)
	if err != nil {
		logger.Error("auth: firestore client init failed; auth disabled", "err", err)
		return nil
	}
	svc, err := auth.New(auth.Config{
		Firestore:      fsClient,
		SigningKey:     []byte(signingKey),
		GoogleClientID: clientID,
		Logger:         logger,
	})
	if err != nil {
		logger.Error("auth: service init failed; auth disabled", "err", err)
		return nil
	}
	return svc
}

// buildProfilesService wires the user-scoped profile + favourites
// store. Mirrors buildAuthService — gated on FIRESTORE_PROJECT
// being set so local dev without Firestore credentials still runs.
func buildProfilesService(ctx context.Context, logger *slog.Logger) *profiles.Service {
	project := strings.TrimSpace(os.Getenv("FIRESTORE_PROJECT"))
	if project == "" {
		return nil
	}
	fsClient, err := firestore.NewClient(ctx, project)
	if err != nil {
		logger.Error("profiles: firestore client init failed; profiles disabled", "err", err)
		return nil
	}
	return profiles.New(fsClient)
}

// buildAppConfigService wires the remote-config service. Firestore
// is OPTIONAL — when FIRESTORE_PROJECT is unset (or the client
// init fails) the service still works, it just always returns the
// env default seeded from STREAMED_BASE_URL_FOR_APP. We never
// return nil because /v1/config must always answer (the TV calls
// it before the user signs in; a missing endpoint would brick
// every TV on its compile-time fallback).
func buildAppConfigService(ctx context.Context, logger *slog.Logger) *appconfig.Service {
	envDefault := strings.TrimSpace(os.Getenv("STREAMED_BASE_URL_FOR_APP"))
	project := strings.TrimSpace(os.Getenv("FIRESTORE_PROJECT"))
	if project == "" {
		return appconfig.New(nil, envDefault, logger)
	}
	fsClient, err := firestore.NewClient(ctx, project)
	if err != nil {
		logger.Error("appconfig: firestore client init failed; using env default only", "err", err)
		return appconfig.New(nil, envDefault, logger)
	}
	return appconfig.New(fsClient, envDefault, logger)
}

// buildCrashLogStore wires the GCS-backed store the crash-log
// endpoint writes to. Returns nil when STORAGE_BUCKET is unset OR
// when the bucket init fails — we deliberately do NOT fall back to
// local disk for crash logs because a "stored" crash that only
// exists on the backend container's ephemeral filesystem is just
// theatre. The handler returns 503 when this is nil, which is what
// we want for local dev.
//
// Stored under the `crash-logs` prefix so a single bucket can host
// posters, logos, and crash logs without naming collisions:
//   gs://{bucket}/crash-logs/{userId}/{timestamp}.txt
func buildCrashLogStore(ctx context.Context, logger *slog.Logger) server.CrashLogStore {
	bucket := strings.TrimSpace(os.Getenv("STORAGE_BUCKET"))
	if bucket == "" {
		return nil
	}
	store, err := server.NewGCSCrashStore(ctx, bucket, "crash-logs")
	if err != nil {
		logger.Error("crash-logs: GCS store init failed; endpoint disabled",
			"bucket", bucket,
			"err", err,
		)
		return nil
	}
	return store
}

// buildFirestoreCacheSource wires the Firestore-backed per-team stats
// cache. Returns nil when FIRESTORE_PROJECT is unset (same gate as
// auth/profiles) or when the Firestore client init fails — the caller
// continues with the unwrapped source in both cases.
func buildFirestoreCacheSource(ctx context.Context, inner data.Source, logger *slog.Logger) *data.FirestoreCacheSource {
	project := strings.TrimSpace(os.Getenv("FIRESTORE_PROJECT"))
	if project == "" {
		return nil
	}
	fsClient, err := firestore.NewClient(ctx, project)
	if err != nil {
		logger.Error("team-cache: firestore client init failed; cache disabled", "err", err)
		return nil
	}
	return data.NewFirestoreCacheSource(fsClient, inner, logger)
}
