// Package server wires the HTTP router, middleware and handlers.
package server

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"strings"
	"time"

	"github.com/abindlish/streamed-backend/internal/api"
	"github.com/abindlish/streamed-backend/internal/appconfig"
	"github.com/abindlish/streamed-backend/internal/auth"
	"github.com/abindlish/streamed-backend/internal/data"
	"github.com/abindlish/streamed-backend/internal/iptv"
	"github.com/abindlish/streamed-backend/internal/livescore"
	"github.com/abindlish/streamed-backend/internal/posters"
	"github.com/abindlish/streamed-backend/internal/profiles"
	"github.com/abindlish/streamed-backend/internal/streamedpk"
)

// Feed IDs are the document keys used inside the Firestore `feeds`
// collection. Centralised here so the cron writer and the HTTP reader
// can't drift apart.
const (
	feedIDIntlCricketUpcoming = "intl-cricket-upcoming"
)

// PosterTrigger is implemented by anything that can kick off a poster
// run on demand. Wired to *cron.Cron in main.go; kept as an interface
// here so the server package doesn't depend on cron (and tests can
// inject a no-op).
type PosterTrigger interface {
	Trigger() bool
}

// Server holds dependencies and exposes the configured http.Handler.
type Server struct {
	source        data.Source
	logger        *slog.Logger
	startedAt     time.Time
	posterGen     *posters.Generator
	posterTrigger PosterTrigger
	live          *livescore.Service
	auth          *auth.Service
	profiles      *profiles.Service
	crashLogStore crashLogStore
	iptv          *iptv.Service
	_iptvProxy    *iptv.ProxyService // lazy-built; see iptvProxy()
	streamedPK    *streamedpk.Service
	appConfig     *appconfig.Service
	cache         *data.FirestoreCacheSource
}

func New(src data.Source, logger *slog.Logger) *Server {
	if logger == nil {
		logger = slog.Default()
	}
	return &Server{source: src, logger: logger, startedAt: time.Now()}
}

// WithPosters wires the poster generator and an optional trigger
// (typically *cron.Cron). Safe to call zero or one times after New.
// When unset the /v1/posters routes return 503.
func (s *Server) WithPosters(g *posters.Generator, t PosterTrigger) *Server {
	s.posterGen = g
	s.posterTrigger = t
	return s
}

// WithLiveScore wires the RapidAPI-backed livescore service. When
// unset the /v1/live/* routes return 503 so the TV's score-ticker can
// silently no-op instead of erroring.
func (s *Server) WithLiveScore(svc *livescore.Service) *Server {
	s.live = svc
	return s
}

// WithAuth wires the auth service (pairing + JWT). When unset the
// /v1/auth/* and /v1/me routes return 503 — useful in local dev
// where Firestore + Google OAuth aren't configured.
func (s *Server) WithAuth(a *auth.Service) *Server {
	s.auth = a
	return s
}

// WithProfiles wires the profiles service. Profile routes return
// 503 when this isn't set. Requires WithAuth too — every profile
// route is auth-required.
func (s *Server) WithProfiles(p *profiles.Service) *Server {
	s.profiles = p
	return s
}

// WithFeedCache wires the typed Firestore cache used to read prebuilt
// feeds (e.g. top-N upcoming international cricket matches the cron
// publishes). When unset the /v1/cricket/international/upcoming route
// returns 503. Distinct from `source data.Source` because the feed
// methods aren't part of that interface — they're typed off the
// concrete *data.FirestoreCacheSource.
func (s *Server) WithFeedCache(cache *data.FirestoreCacheSource) *Server {
	s.cache = cache
	return s
}

// Handler returns the root http.Handler with CORS, logging and routes.
func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/health", s.handleHealth)
	// Remote config bundle. Public + always-on — the TV calls this
	// before any auth so a 503 here would brick every TV on its
	// compile-time defaults. See internal/appconfig.
	mux.HandleFunc("GET /v1/config", s.handleAppConfig)
	mux.HandleFunc("GET /v1/teams/lookup", s.handleTeamLookup)
	mux.HandleFunc("GET /v1/teams/{id}/standings", s.handleTeamStandings)
	mux.HandleFunc("GET /v1/teams/{id}/next-match", s.handleNextMatch)
	mux.HandleFunc("GET /v1/teams/{id}/leagues", s.handleAllTeamLeagues)
	mux.HandleFunc("GET /v1/leagues/{id}/standings", s.handleLeagueStandings)

	// Poster routes. If posters are not wired (no OpenAI key, cron
	// disabled) these handlers respond with 503 — the router still
	// answers so curl smoke tests don't get a vague 404.
	mux.HandleFunc("GET /v1/posters", s.handleAssetsManifest)
	mux.HandleFunc("POST /v1/posters/refresh", s.handlePostersRefresh)
	mux.HandleFunc("GET /v1/posters/{matchId}", s.handlePosterImage)
	mux.HandleFunc("GET /v1/teams/{teamSlug}/logo.png", s.handleTeamLogo)

	// Live cricket score routes (RapidAPI-backed). When RAPIDAPI_KEY is
	// unset these return 503; the TV polls them but treats 503 as
	// "score ticker disabled".
	mux.HandleFunc("GET /v1/live/cricket", s.handleLiveCricket)
	mux.HandleFunc("GET /v1/live/cricket/schedule", s.handleCricketSchedule)
	mux.HandleFunc("GET /v1/live/cricket/match/{matchId}", s.handleCricketMatch)

	// Prebuilt cricket feeds. Reads-only from Firestore (the cron
	// populates them); 503 when the feed cache isn't wired, 404 when
	// the cron hasn't published yet.
	mux.HandleFunc("GET /v1/cricket/international/upcoming", s.handleCricketIntlUpcoming)

	// Auth + pairing. /init starts a pair flow (TV calls this);
	// /claim finishes it (the web pairing page calls this after
	// the user signs in with Google); /poll lets the TV check for
	// completion. /me is the simplest auth-required check.
	mux.HandleFunc("POST /v1/auth/pair/init", s.handlePairInit)
	mux.HandleFunc("POST /v1/auth/pair/claim", s.handlePairClaim)
	mux.HandleFunc("GET /v1/auth/pair/poll", s.handlePairPoll)
	mux.HandleFunc("GET /v1/me", s.handleMe)

	// Pairing web page (served from the same origin as the API so we
	// can avoid CORS gymnastics on the Google ID token claim POST).
	// /pair is the canonical URL; /pair.html is a friendly alias.
	mux.HandleFunc("GET /pair", s.handlePairPage)
	mux.HandleFunc("GET /pair.html", s.handlePairPage)

	// Profile + favourites CRUD. All auth-required (wrapped via
	// authRequired). Profiles live in Firestore at
	// users/{uid}/profiles/{pid}; favourites live on the same doc
	// (favoriteSports / favoriteTeams / lastSelectedSport).
	mux.HandleFunc("GET    /v1/profiles", s.authRequired(s.handleProfilesList))
	mux.HandleFunc("POST   /v1/profiles", s.authRequired(s.handleProfileCreate))
	mux.HandleFunc("PATCH  /v1/profiles/{id}", s.authRequired(s.handleProfileUpdate))
	mux.HandleFunc("DELETE /v1/profiles/{id}", s.authRequired(s.handleProfileDelete))
	mux.HandleFunc("POST   /v1/profiles/active", s.authRequired(s.handleSetActiveProfile))

	// Crash log ingestion. Auth-required. The TV app captures any
	// unhandled exception (and every Diagnostics.LogError site) to a
	// local file, then POSTs the file here on the next launch after
	// auth is restored. Lets us debug remote TVs without USB sdb.
	// Returns 503 when no store is wired (local dev without GCS).
	mux.HandleFunc("POST /v1/crash-logs", s.handleCrashLogPost)

	// iptv-org integration. Public IPTV directory of free streams
	// — used as an alternative to the streamed.pk catalog for
	// channels whose CDNs accept plain `<video src=...>` playback.
	// /channels returns the catalog filtered by category/country;
	// /play wraps an m3u8 in a tiny hls.js page so the TV's
	// existing PlayerWebView can render it.
	mux.HandleFunc("GET /v1/iptv/channels", s.handleIPTVChannels)
	mux.HandleFunc("GET /v1/iptv/play", s.handleIPTVPlay)
	// Stream proxy — fetches m3u8 + segments server-side with the
	// per-stream UA + Referer iptv-org gives us. See proxy.go for
	// the long-form rationale; short version: hls.js can't override
	// Referer for cross-origin XHR, so streams that gate on it
	// reject direct fetches from the WebView.
	mux.HandleFunc("GET /v1/iptv/m3u8", s.handleIPTVM3U8)
	mux.HandleFunc("GET /v1/iptv/seg", s.handleIPTVSeg)

	// streamed.pk catalog proxy. Mirrors the upstream's /api/...
	// surface byte-for-byte (with a per-path TTL cache + stale-on-
	// error fallback) so the TV can be pointed at our origin by
	// flipping StreamedEndpoints.BaseUrl. Disabled handlers return
	// 503; see internal/streamedpk for the wire-level details.
	//
	// The route patterns mirror what the TV's StreamedEndpoints.cs
	// builds. Order matters with net/http's ServeMux: more-specific
	// "/popular" routes come BEFORE the catch-all "{sport}" so a
	// request to /api/matches/all/popular doesn't get swallowed by
	// /api/matches/{sport}.
	mux.HandleFunc("GET /api/sports", s.handleStreamedPKProxy)

	mux.HandleFunc("GET /api/matches/all/popular", s.handleStreamedPKProxy)
	mux.HandleFunc("GET /api/matches/all", s.handleStreamedPKProxy)
	mux.HandleFunc("GET /api/matches/all-today/popular", s.handleStreamedPKProxy)
	mux.HandleFunc("GET /api/matches/all-today", s.handleStreamedPKProxy)
	mux.HandleFunc("GET /api/matches/live/popular", s.handleStreamedPKProxy)
	mux.HandleFunc("GET /api/matches/live", s.handleStreamedPKProxy)
	mux.HandleFunc("GET /api/matches/{sport}/popular", s.handleStreamedPKProxy)
	mux.HandleFunc("GET /api/matches/{sport}", s.handleStreamedPKProxy)

	mux.HandleFunc("GET /api/stream/{source}/{id}", s.handleStreamedPKProxy)

	mux.HandleFunc("GET /api/images/badge/{id}", s.handleStreamedPKProxy)
	mux.HandleFunc("GET /api/images/poster/{home}/{away}", s.handleStreamedPKProxy)
	mux.HandleFunc("GET /api/images/proxy/{id}", s.handleStreamedPKProxy)

	// Stream URL resolver. Auth-required. Takes a wrapper URL
	// (e.g. embedsports.top), fetches it server-side, returns the
	// inner iframe URL — or the original URL unchanged when the
	// wrapper isn't on the allowlist or extraction fails. Lets the
	// TV bypass cross-origin iframes (Tizen HW VideoHole compositor
	// can only see <video> in the top frame).
	mux.HandleFunc("GET /v1/stream-resolve", s.handleStreamResolve)

	// Layered: cors(logging(mux)).
	return s.cors(s.requestLog(mux))
}

// cors permits cross-origin calls from the TV app + the web pairing
// page. Wide-open Allow-Origin for now; tighten before going prod.
// Allow-Headers includes Authorization so the TV can attach Bearer
// tokens; Allow-Methods includes POST for the pairing flow.
func (s *Server) cors(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, PATCH, DELETE, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type, Authorization")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}

// requestLog emits a structured access log line per request.
func (s *Server) requestLog(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		sw := &statusWriter{ResponseWriter: w, code: http.StatusOK}
		next.ServeHTTP(sw, r)
		s.logger.Info("http",
			"method", r.Method,
			"path", r.URL.Path,
			"status", sw.code,
			"durMs", time.Since(start).Milliseconds(),
		)
	})
}

type statusWriter struct {
	http.ResponseWriter
	code int
}

func (sw *statusWriter) WriteHeader(code int) {
	sw.code = code
	sw.ResponseWriter.WriteHeader(code)
}

func (s *Server) handleHealth(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, api.HealthResponse{
		Status: "ok",
		Uptime: time.Since(s.startedAt).Round(time.Second).String(),
	})
}

func (s *Server) handleTeamStandings(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	if id == "" {
		writeError(w, http.StatusBadRequest, "missing team id")
		return
	}
	out, err := s.source.TeamStandings(r.Context(), id)
	if err != nil {
		s.respondError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, out)
}

func (s *Server) handleNextMatch(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	if id == "" {
		writeError(w, http.StatusBadRequest, "missing team id")
		return
	}
	out, err := s.source.NextMatch(r.Context(), id)
	if err != nil {
		s.respondError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, out)
}

func (s *Server) handleLeagueStandings(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	if id == "" {
		writeError(w, http.StatusBadRequest, "missing league id")
		return
	}
	out, err := s.source.LeagueStandings(r.Context(), id)
	if err != nil {
		s.respondError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, out)
}

func (s *Server) handleAllTeamLeagues(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	if id == "" {
		writeError(w, http.StatusBadRequest, "missing team id")
		return
	}
	out, err := s.source.AllTeamStandings(r.Context(), id)
	if err != nil {
		s.respondError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, out)
}

// handleTeamLookup serves GET /v1/teams/lookup?name=...
//
// The name query param is URL-decoded automatically by net/http.
// Resolution is delegated to data.LookupTeamByName which tries the
// in-process alias table first and (when wired) falls back to the
// OpenAI source. A miss in both yields 404 — no body shape change.
func (s *Server) handleTeamLookup(w http.ResponseWriter, r *http.Request) {
	name := strings.TrimSpace(r.URL.Query().Get("name"))
	if name == "" {
		writeError(w, http.StatusBadRequest, "missing name")
		return
	}
	out, err := data.LookupTeamByName(r.Context(), s.source, name)
	if err != nil {
		s.respondError(w, err)
		return
	}
	if out == nil {
		writeError(w, http.StatusNotFound, "not found")
		return
	}
	writeJSON(w, http.StatusOK, out)
}

func (s *Server) respondError(w http.ResponseWriter, err error) {
	if errors.Is(err, data.ErrNotFound) {
		writeError(w, http.StatusNotFound, "not found")
		return
	}
	s.logger.Error("handler error", "err", err)
	writeError(w, http.StatusInternalServerError, "internal error")
}

func writeJSON(w http.ResponseWriter, code int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(body)
}

func writeError(w http.ResponseWriter, code int, msg string) {
	writeJSON(w, code, api.ErrorResponse{Error: msg})
}

// handleAssetsManifest serves GET /v1/posters — a directory listing
// of every cached poster AND every cached team logo. Single endpoint
// so the TV client can discover all generated art in one round trip.
func (s *Server) handleAssetsManifest(w http.ResponseWriter, _ *http.Request) {
	if s.posterGen == nil {
		writeError(w, http.StatusServiceUnavailable, "posters disabled")
		return
	}
	posterEntries, err := s.posterGen.ListPosters()
	if err != nil {
		s.logger.Error("posters list failed", "err", err)
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	logoEntries, err := s.posterGen.ListLogos()
	if err != nil {
		s.logger.Error("logos list failed", "err", err)
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	manifest := api.AssetsManifest{
		Posters: make([]api.Poster, 0, len(posterEntries)),
		Logos:   make([]api.Logo, 0, len(logoEntries)),
	}
	for _, e := range posterEntries {
		manifest.Posters = append(manifest.Posters, api.Poster{
			MatchID:     e.MatchID,
			URL:         e.URL,
			GeneratedAt: e.GeneratedAt.UTC().Format(time.RFC3339),
			SizeBytes:   e.SizeBytes,
		})
	}
	for _, e := range logoEntries {
		manifest.Logos = append(manifest.Logos, api.Logo{
			TeamSlug:    e.TeamSlug,
			TeamName:    e.TeamName,
			URL:         e.URL,
			GeneratedAt: e.GeneratedAt.UTC().Format(time.RFC3339),
			SizeBytes:   e.SizeBytes,
		})
	}
	writeJSON(w, http.StatusOK, manifest)
}

// handlePostersRefresh serves POST /v1/posters/refresh. Kicks the
// cron once and returns immediately. The actual generation is async.
func (s *Server) handlePostersRefresh(w http.ResponseWriter, _ *http.Request) {
	if s.posterTrigger == nil {
		writeError(w, http.StatusServiceUnavailable, "poster cron disabled")
		return
	}
	queued := s.posterTrigger.Trigger()
	status := "started"
	if !queued {
		status = "already-running"
	}
	writeJSON(w, http.StatusAccepted, api.PosterRefreshResponse{Status: status})
}

// handlePosterImage serves GET /v1/posters/{matchId}.png. The path
// param includes the .png suffix (the route pattern is `{matchId}`),
// so we strip it before looking up the cached file.
func (s *Server) handlePosterImage(w http.ResponseWriter, r *http.Request) {
	if s.posterGen == nil {
		writeError(w, http.StatusServiceUnavailable, "posters disabled")
		return
	}
	raw := strings.TrimSpace(r.PathValue("matchId"))
	if !strings.HasSuffix(raw, ".png") {
		writeError(w, http.StatusNotFound, "not found")
		return
	}
	matchID := strings.TrimSuffix(raw, ".png")
	s.streamPNG(w, r, s.posterGen.PosterStore(), matchID, "poster")
}

// handleTeamLogo serves GET /v1/teams/{teamSlug}/logo.png.
func (s *Server) handleTeamLogo(w http.ResponseWriter, r *http.Request) {
	if s.posterGen == nil {
		writeError(w, http.StatusServiceUnavailable, "posters disabled")
		return
	}
	slug := strings.TrimSpace(r.PathValue("teamSlug"))
	s.streamPNG(w, r, s.posterGen.LogoStore(), slug, "logo")
}

// streamPNG is the shared body for serving poster + logo images. Reads
// straight from the configured Store (local disk or GCS) and streams
// the bytes through. Sanitization happens inside store.Open via
// SanitizeMatchID, so an invalid id surfaces as a 400.
func (s *Server) streamPNG(w http.ResponseWriter, r *http.Request, store posters.Store, key, kind string) {
	rc, meta, err := store.Open(r.Context(), key)
	if err != nil {
		if errors.Is(err, posters.ErrNotExist) {
			writeError(w, http.StatusNotFound, "not found")
			return
		}
		// Sanitization errors surface as a non-NotExist error. Treat
		// those as bad input (400) rather than a server failure.
		if strings.HasPrefix(err.Error(), "invalid id") || strings.HasPrefix(err.Error(), "empty id") {
			writeError(w, http.StatusBadRequest, "invalid id")
			return
		}
		s.logger.Error("asset open failed", "kind", kind, "id", key, "err", err)
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	defer rc.Close()
	w.Header().Set("Content-Type", "image/png")
	w.Header().Set("Cache-Control", "public, max-age=86400")
	if meta != nil && meta.Size > 0 {
		// Setting Content-Length before WriteHeader lets clients show a
		// progress bar and lets intermediaries pipeline.
		w.Header().Set("Content-Length", itoaInt64(meta.Size))
	}
	if _, err := io.Copy(w, rc); err != nil {
		// Client likely disconnected. Don't bother trying to write a
		// status — headers may already be flushed.
		s.logger.Info("asset copy interrupted", "kind", kind, "id", key, "err", err)
	}
}

// itoaInt64 avoids pulling strconv just for one tiny use.
func itoaInt64(n int64) string {
	if n == 0 {
		return "0"
	}
	negative := n < 0
	if negative {
		n = -n
	}
	var buf [20]byte
	i := len(buf)
	for n > 0 {
		i--
		buf[i] = byte('0' + n%10)
		n /= 10
	}
	if negative {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}

// ----------------------------------------------------------------------
// Live cricket scores (RapidAPI passthrough)
// ----------------------------------------------------------------------

// handleLiveCricket returns the normalized list of currently-live
// cricket matches. The TV polls this every 60s. Cached upstream for
// 30s, so two polls per minute generally hit cache.
func (s *Server) handleLiveCricket(w http.ResponseWriter, r *http.Request) {
	if s.live == nil {
		writeError(w, http.StatusServiceUnavailable, "live scores disabled")
		return
	}
	out, err := s.live.LiveCricket(r.Context())
	if err != nil {
		s.logger.Warn("live cricket fetch failed", "err", err)
		writeError(w, http.StatusBadGateway, "upstream failure")
		return
	}
	writeJSON(w, http.StatusOK, out)
}

// handleCricketSchedule returns today's + upcoming cricket fixtures
// from the RapidAPI cricket-schedule endpoint, untouched. Used by the
// TV when populating the cricket row with backend metadata.
func (s *Server) handleCricketSchedule(w http.ResponseWriter, r *http.Request) {
	if s.live == nil {
		writeError(w, http.StatusServiceUnavailable, "live scores disabled")
		return
	}
	raw, err := s.live.CricketSchedule(r.Context())
	if err != nil {
		s.logger.Warn("cricket schedule fetch failed", "err", err)
		writeError(w, http.StatusBadGateway, "upstream failure")
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(raw)
}

// handleCricketMatch returns per-match info from the RapidAPI
// cricket-match-info endpoint. Used when the TV wants details for a
// specific tile.
func (s *Server) handleCricketMatch(w http.ResponseWriter, r *http.Request) {
	if s.live == nil {
		writeError(w, http.StatusServiceUnavailable, "live scores disabled")
		return
	}
	matchID := strings.TrimSpace(r.PathValue("matchId"))
	raw, err := s.live.MatchInfo(r.Context(), matchID)
	if err != nil {
		s.logger.Warn("cricket match info fetch failed", "matchId", matchID, "err", err)
		writeError(w, http.StatusBadGateway, "upstream failure")
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(raw)
}

// handleCricketIntlUpcoming serves GET /v1/cricket/international/upcoming.
// Reads the prebuilt feed the cron publishes via WriteFeed. Pure
// Firestore read — no upstream calls. 503 when the feed cache isn't
// wired, 404 until the cron has populated the feed at least once.
func (s *Server) handleCricketIntlUpcoming(w http.ResponseWriter, r *http.Request) {
	if s.cache == nil {
		writeError(w, http.StatusServiceUnavailable, "feeds disabled")
		return
	}
	var feed api.CricketUpcomingFeed
	if err := s.cache.GetFeed(r.Context(), feedIDIntlCricketUpcoming, &feed); err != nil {
		if errors.Is(err, data.ErrNotFound) {
			writeError(w, http.StatusNotFound, "feed empty")
			return
		}
		s.logger.Error("feed read failed", "feed", feedIDIntlCricketUpcoming, "err", err)
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	writeJSON(w, http.StatusOK, feed)
}

// shutdownTimeout is exported for callers that wire graceful shutdown.
const shutdownTimeout = 10 * time.Second

// Shutdown performs a context-bounded graceful shutdown.
func (s *Server) Shutdown(parent context.Context, srv *http.Server) error {
	ctx, cancel := context.WithTimeout(parent, shutdownTimeout)
	defer cancel()
	return srv.Shutdown(ctx)
}
