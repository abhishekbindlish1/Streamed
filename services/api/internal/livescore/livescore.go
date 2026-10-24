// Package livescore wraps RapidAPI's cricket-api-free-data listing
// (https://rapidapi.com/.../cricket-api-free-data) and exposes a
// normalized live-score view used by the TV app's score ticker.
//
// Why this lives outside data/ — the data.Source chain optimizes for
// "rare lookup with 6h+ cache TTL" (standings, next match). Live
// scores are the opposite: short TTL (~30s), high request rate, no
// fallback chain, single upstream. Mixing the two access patterns
// inside the Source interface forces ugly compromises, so this is its
// own package with a tighter surface.
package livescore

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/abindlish/streamed-backend/internal/api"
)

// DefaultHost is the RapidAPI gateway host for the cricket-api-free-data
// listing. Override via NewService(host=...) only if you switch APIs.
const DefaultHost = "cricket-api-free-data.p.rapidapi.com"

// DefaultLiveScoresTTL is the in-memory TTL for the upstream
// /cricket-livescores response. Live cricket scores update every 1–6
// balls, ~10s apart at peak; 30s lets us absorb 60s polling from
// multiple TV instances and stay well inside the rate limit.
const DefaultLiveScoresTTL = 30 * time.Second

// DefaultMatchInfoTTL caches per-match info longer because it changes
// less frequently (status flips, innings changes).
const DefaultMatchInfoTTL = 60 * time.Second

// SourceName is the value stamped on every payload's Source field.
const SourceName = "rapidapi-cricket"

// Service holds the upstream credentials + caches and exposes the
// methods that the HTTP handlers call.
type Service struct {
	apiKey string
	host   string
	client *http.Client
	logger *slog.Logger

	mu    sync.Mutex
	cache map[string]cachedEntry
}

type cachedEntry struct {
	value     any
	expiresAt time.Time
	fetchedAt time.Time
}

// New constructs a Service. apiKey is the RapidAPI key; logger may be
// nil (defaults to slog.Default). host defaults to DefaultHost when
// empty.
func New(apiKey, host string, logger *slog.Logger) *Service {
	if logger == nil {
		logger = slog.Default()
	}
	if host == "" {
		host = DefaultHost
	}
	return &Service{
		apiKey: apiKey,
		host:   host,
		client: &http.Client{Timeout: 8 * time.Second},
		logger: logger,
		cache:  make(map[string]cachedEntry),
	}
}

// LiveCricket returns the normalized list of currently-live cricket
// matches. The response is cached for DefaultLiveScoresTTL; cachedAge
// in the response tells the caller how stale the data is.
func (s *Service) LiveCricket(ctx context.Context) (api.LiveCricketResponse, error) {
	const cacheKey = "livescores"

	if hit, age, ok := s.cacheGet(cacheKey); ok {
		resp := hit.(api.LiveCricketResponse)
		resp.CachedAgeSeconds = int(age.Seconds())
		return resp, nil
	}

	raw, err := s.fetchJSON(ctx, "/cricket-livescores", nil)
	if err != nil {
		return api.LiveCricketResponse{}, err
	}

	matches := normalizeLiveScores(raw)
	now := time.Now().UTC().Format(time.RFC3339)
	resp := api.LiveCricketResponse{
		Matches:          matches,
		UpdatedAt:        now,
		Source:           SourceName,
		CachedAgeSeconds: 0,
	}
	s.cachePut(cacheKey, resp, DefaultLiveScoresTTL)
	return resp, nil
}

// CricketSchedule returns the upstream /cricket-schedule response
// minimally massaged. Schedule changes rarely; cached 5 min.
func (s *Service) CricketSchedule(ctx context.Context) (json.RawMessage, error) {
	const cacheKey = "schedule"
	if hit, _, ok := s.cacheGet(cacheKey); ok {
		return hit.(json.RawMessage), nil
	}
	raw, err := s.fetchRaw(ctx, "/cricket-schedule", nil)
	if err != nil {
		return nil, err
	}
	s.cachePut(cacheKey, raw, 5*time.Minute)
	return raw, nil
}

// MatchInfo returns the upstream /cricket-match-info?matchId=X
// response. Cached for DefaultMatchInfoTTL. matchID is taken straight
// from the schedule listing.
func (s *Service) MatchInfo(ctx context.Context, matchID string) (json.RawMessage, error) {
	if strings.TrimSpace(matchID) == "" {
		return nil, errors.New("missing matchId")
	}
	cacheKey := "match:" + matchID
	if hit, _, ok := s.cacheGet(cacheKey); ok {
		return hit.(json.RawMessage), nil
	}
	raw, err := s.fetchRaw(ctx, "/cricket-match-info", map[string]string{"matchId": matchID})
	if err != nil {
		return nil, err
	}
	s.cachePut(cacheKey, raw, DefaultMatchInfoTTL)
	return raw, nil
}

// fetchJSON does the GET + decode into a generic map for normalization.
func (s *Service) fetchJSON(ctx context.Context, path string, query map[string]string) (map[string]any, error) {
	body, err := s.fetchRaw(ctx, path, query)
	if err != nil {
		return nil, err
	}
	var out map[string]any
	if err := json.Unmarshal(body, &out); err != nil {
		return nil, fmt.Errorf("livescore: decode %s: %w", path, err)
	}
	return out, nil
}

// fetchRaw does the GET and returns the raw response bytes. Used both
// by fetchJSON and by the passthrough endpoints (schedule, match-info)
// where we don't want to re-marshal a parsed map.
func (s *Service) fetchRaw(ctx context.Context, path string, query map[string]string) (json.RawMessage, error) {
	url := "https://" + s.host + path
	if len(query) > 0 {
		var b strings.Builder
		b.WriteString(url)
		first := true
		for k, v := range query {
			if first {
				b.WriteByte('?')
				first = false
			} else {
				b.WriteByte('&')
			}
			b.WriteString(k)
			b.WriteByte('=')
			b.WriteString(v)
		}
		url = b.String()
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, fmt.Errorf("livescore: build req %s: %w", path, err)
	}
	req.Header.Set("x-rapidapi-key", s.apiKey)
	req.Header.Set("x-rapidapi-host", s.host)
	req.Header.Set("Accept", "application/json")

	resp, err := s.client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("livescore: http %s: %w", path, err)
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(io.LimitReader(resp.Body, 5*1024*1024))
	if err != nil {
		return nil, fmt.Errorf("livescore: read %s: %w", path, err)
	}
	if resp.StatusCode/100 != 2 {
		// Don't include the URL in the error — it contains the API key
		// nowhere (key is in the header) so this is just defensive.
		return nil, fmt.Errorf("livescore: upstream %s returned %d: %s", path, resp.StatusCode, snippet(body, 200))
	}
	return json.RawMessage(body), nil
}

func snippet(b []byte, n int) string {
	s := strings.TrimSpace(string(b))
	if len(s) > n {
		return s[:n] + "…"
	}
	return s
}

// ----------------------------------------------------------------------
// Normalization
// ----------------------------------------------------------------------

// normalizeLiveScores converts the upstream Cricbuzz-shaped
// /cricket-livescores response into our LiveCricketMatch list.
//
// Upstream shape we expect (when matches are in play):
//
//	{
//	  "status": "success",
//	  "response": [
//	    {
//	      "matchId": 12345,
//	      "seriesName": "IPL 2026",
//	      "matchFormat": "T20",
//	      "status": "Mumbai won by 60 runs",
//	      "state": "Complete",
//	      "team1": {"teamName": "Mumbai Indians", "teamSName": "MI"},
//	      "team2": {"teamName": "CSK", "teamSName": "CSK"},
//	      "team1Score": "180/4",
//	      "team1Overs": "18.2",
//	      "team2Score": "120/5",
//	      "team2Overs": "15.0",
//	      "venue": "Wankhede"
//	    }
//	  ]
//	}
//
// Fields that don't appear in a particular response just stay empty —
// the TV will skip rendering anything that's blank.
func normalizeLiveScores(raw map[string]any) []api.LiveCricketMatch {
	respIface, ok := raw["response"]
	if !ok {
		return []api.LiveCricketMatch{}
	}
	respList, ok := respIface.([]any)
	if !ok || len(respList) == 0 {
		return []api.LiveCricketMatch{}
	}
	out := make([]api.LiveCricketMatch, 0, len(respList))
	for _, item := range respList {
		m, ok := item.(map[string]any)
		if !ok {
			continue
		}
		out = append(out, normalizeOne(m))
	}
	return out
}

func normalizeOne(m map[string]any) api.LiveCricketMatch {
	return api.LiveCricketMatch{
		MatchID:   asString(m["matchId"]),
		Series:    firstNonEmpty(asString(m["seriesName"]), asString(m["series"])),
		Format:    asString(m["matchFormat"]),
		Status:    asString(m["status"]),
		State:     normalizeState(asString(m["state"]), asString(m["status"])),
		Team1:     extractTeam(m, "team1"),
		Team2:     extractTeam(m, "team2"),
		Venue:     asString(m["venue"]),
		StartTime: asString(m["startTime"]),
		UpdatedAt: time.Now().UTC().Format(time.RFC3339),
		Source:    SourceName,
	}
}

// extractTeam pulls the two-team subobject AND any flat sibling fields
// like team1Score / team1Overs. Different API revisions put the score
// in different places; we accept both.
func extractTeam(m map[string]any, key string) api.LiveTeam {
	t := api.LiveTeam{}
	if sub, ok := m[key].(map[string]any); ok {
		t.Name = firstNonEmpty(asString(sub["teamName"]), asString(sub["name"]))
		t.ShortName = firstNonEmpty(asString(sub["teamSName"]), asString(sub["shortName"]))
		t.Score = firstNonEmpty(asString(sub["score"]), asString(sub["scoreText"]))
		t.Overs = firstNonEmpty(asString(sub["overs"]), asString(sub["oversText"]))
	}
	// Sibling field overrides if present (some payloads).
	if v := asString(m[key+"Score"]); v != "" {
		t.Score = v
	}
	if v := asString(m[key+"Overs"]); v != "" {
		t.Overs = v
	}
	return t
}

// normalizeState collapses the noisy state vocabulary into a small
// enum the TV can switch on.
func normalizeState(state, status string) string {
	low := strings.ToLower(strings.TrimSpace(state))
	switch low {
	case "in progress", "live":
		return "live"
	case "complete", "finished":
		return "finished"
	case "preview", "scheduled", "upcoming":
		return "scheduled"
	}
	// Sometimes only `status` is populated (e.g. Cricbuzz returns
	// "Match scheduled to begin at..." with no state). Keyword sniff.
	low = strings.ToLower(status)
	switch {
	case low == "":
		return "unknown"
	case strings.Contains(low, "won by"), strings.Contains(low, "drawn"), strings.Contains(low, "tied"), strings.Contains(low, "no result"), strings.Contains(low, "abandoned"):
		return "finished"
	case strings.Contains(low, "scheduled"), strings.Contains(low, "starts in"):
		return "scheduled"
	case strings.Contains(low, "innings"), strings.Contains(low, "trail"), strings.Contains(low, "lead"), strings.Contains(low, "needs"):
		return "live"
	}
	return "unknown"
}

func asString(v any) string {
	switch x := v.(type) {
	case string:
		return x
	case float64:
		// JSON numbers come in as float64. Match IDs in particular are
		// integers we want as plain string.
		if x == float64(int64(x)) {
			return fmt.Sprintf("%d", int64(x))
		}
		return fmt.Sprintf("%g", x)
	case int:
		return fmt.Sprintf("%d", x)
	case int64:
		return fmt.Sprintf("%d", x)
	case bool:
		if x {
			return "true"
		}
		return "false"
	case nil:
		return ""
	}
	return ""
}

func firstNonEmpty(a ...string) string {
	for _, s := range a {
		if strings.TrimSpace(s) != "" {
			return s
		}
	}
	return ""
}

// ----------------------------------------------------------------------
// Cache (small, kept inline because nothing else needs it)
// ----------------------------------------------------------------------

func (s *Service) cacheGet(key string) (any, time.Duration, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	e, ok := s.cache[key]
	if !ok || time.Now().After(e.expiresAt) {
		return nil, 0, false
	}
	return e.value, time.Since(e.fetchedAt), true
}

func (s *Service) cachePut(key string, value any, ttl time.Duration) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cache[key] = cachedEntry{
		value:     value,
		expiresAt: time.Now().Add(ttl),
		fetchedAt: time.Now(),
	}
}
