// Package data, cricapi.go: CricAPI-backed Source for cricket fixtures.
//
// CricAPISource queries https://api.cricapi.com (free tier) for live,
// upcoming and recently-completed cricket matches and uses that data to
// populate api.NextMatch responses for IPL franchises and major
// international sides. CricAPI does not expose classic league standings,
// so TeamStandings and LeagueStandings always delegate to the wrapped
// fallback Source.
//
// Wrapping pattern (mirrors openai.go):
//
//   - This source takes a `next data.Source` and delegates whenever:
//   - The team ID isn't one we can map to a CricAPI team (e.g. football team)
//   - The HTTP call fails
//   - CricAPI returns no matching data
//
// Constraints:
//
//   - Stdlib only for HTTP.
//   - Auth is a query param (?apikey=...), never a header — and the key
//     is never logged.
//   - The free tier is 100 req/day, so /currentMatches is cached
//     aggressively (10 minutes) and per-team NextMatch results for 30
//     minutes.
package data

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/abindlish/streamed-backend/internal/api"
)

// SourceCricAPI is the value stamped on payloads served from CricAPI.
const SourceCricAPI = "cricapi"

// cricAPIBase is the v1 base URL for the public CricAPI service.
const cricAPIBase = "https://api.cricapi.com/v1"

// canonicalToCricketName maps the canonical IDs from teamCatalog onto
// the human-readable team names CricAPI uses inside the `teams[]`
// arrays of /currentMatches responses. Anything not in this map is a
// non-cricket team — calls for those IDs delegate straight to `next`
// without ever touching the network.
var canonicalToCricketName = map[string]string{
	"mumbai-indians":              "Mumbai Indians",
	"chennai-super-kings":         "Chennai Super Kings",
	"royal-challengers-bangalore": "Royal Challengers Bengaluru",
	"kolkata-knight-riders":       "Kolkata Knight Riders",
	"delhi-capitals":              "Delhi Capitals",
	"gujarat-titans":              "Gujarat Titans",
	"sunrisers-hyderabad":         "Sunrisers Hyderabad",
	"punjab-kings":                "Punjab Kings",
	"rajasthan-royals":            "Rajasthan Royals",
	"lucknow-super-giants":        "Lucknow Super Giants",
	"india":                       "India",
	"australia":                   "Australia",
	"england":                     "England",
	"pakistan":                    "Pakistan",
}

// shorthand alias to keep the cric-side ID resolution self-contained.
// "csk", "rcb", "kkr" land here from the prompt — they're already
// covered by the alias table, but we accept them defensively so
// NextMatch("csk") works even if somebody bypasses the alias path.
var cricShortAliases = map[string]string{
	"csk": "chennai-super-kings",
	"rcb": "royal-challengers-bangalore",
	"kkr": "kolkata-knight-riders",
	"gt":  "gujarat-titans",
	"srh": "sunrisers-hyderabad",
	"pbks": "punjab-kings",
	"rr":  "rajasthan-royals",
	"lsg": "lucknow-super-giants",
	"mi":  "mumbai-indians",
	"dc":  "delhi-capitals",
}

// resolveCanonicalID applies the cric-only short alias map then returns
// the input unchanged. Lowercased.
func resolveCanonicalID(teamID string) string {
	id := strings.ToLower(strings.TrimSpace(teamID))
	if mapped, ok := cricShortAliases[id]; ok {
		return mapped
	}
	return id
}

// cacheEntry is a generic time-bounded cache slot. value is left as
// `any` so the same cache can hold both the parsed currentMatches
// payload and per-team NextMatch results.
type cacheEntry struct {
	value     any
	expiresAt time.Time
}

// CricAPISource is the cricket-fixture-aware Source. It wraps a `next`
// Source so any query it cannot service (non-cricket teams, network
// errors, empty CricAPI responses) transparently falls through.
type CricAPISource struct {
	apiKey string
	client *http.Client
	next   Source
	logger *slog.Logger

	// testBase, when non-empty, replaces cricAPIBase. ONLY set from
	// _test.go helpers via setBaseURL — never from production wiring.
	testBase string

	// cache is a tiny in-process TTL cache. Keys:
	//   "currentMatches"           → []cricMatch
	//   "nextMatch:<canonicalID>"  → api.NextMatch
	cacheMu sync.RWMutex
	cache   map[string]cacheEntry
}

// setBaseURL points the source at a custom base (e.g. an httptest
// server). Test-only helper exported within the package.
func (c *CricAPISource) setBaseURL(u string) { c.testBase = u }

// TTLs.
const (
	currentMatchesTTL = 10 * time.Minute
	nextMatchTTL      = 30 * time.Minute
	// seriesInfoTTL caches /series_info?id=... per series. Series schedules
	// rarely change mid-tournament, so 6h is a safe default for cron use.
	seriesInfoTTL = 6 * time.Hour
	// seriesSearchTTL caches /series?search=... lookups. The series catalog
	// is stable enough that a daily refresh is plenty.
	seriesSearchTTL = 24 * time.Hour
)

// NewCricAPISource constructs a CricAPI-backed Source. apiKey must be
// pulled from the env at the call site (see main.go) — never hardcode.
// The key is held only in memory and is never logged.
//
// `next` is the Source consulted on any miss/failure. Pass the existing
// chain (e.g. a football-data wrapper around openai+stub) so cricket
// failures fall through to whatever else is wired.
func NewCricAPISource(apiKey string, next Source, logger *slog.Logger) *CricAPISource {
	if logger == nil {
		logger = slog.Default()
	}
	if next == nil {
		// Defensive: a nil next would panic the moment we delegated.
		next = NewStubSource()
	}
	return &CricAPISource{
		apiKey: apiKey,
		client: &http.Client{Timeout: 8 * time.Second},
		next:   next,
		logger: logger,
		cache:  make(map[string]cacheEntry),
	}
}

func (c *CricAPISource) Name() string { return SourceCricAPI }

// cricResponse is the envelope every CricAPI v1 endpoint wraps its
// payload in. status="success" means data is usable; anything else
// (typically "failure") means the body is an error and the data
// payload should not be consumed.
//
// We parse the data payload separately per endpoint into the raw
// json.RawMessage held here — keeps this struct generic.
type cricResponse struct {
	APIKey string          `json:"apikey"`
	Data   json.RawMessage `json:"data"`
	Status string          `json:"status"`
	Reason string          `json:"reason"`
	Info   json.RawMessage `json:"info"`
}

// cricMatch is the (subset of) fields we need from one entry inside
// /currentMatches' data array. The full schema has more fields
// (matchType, score, etc.) — we ignore them.
type cricMatch struct {
	ID          string   `json:"id"`
	Name        string   `json:"name"`
	MatchType   string   `json:"matchType"`
	Status      string   `json:"status"`
	Venue       string   `json:"venue"`
	Date        string   `json:"date"`
	DateTimeGMT string   `json:"dateTimeGMT"`
	Teams       []string `json:"teams"`
	SeriesID    string   `json:"series_id"`
}

// cacheGet returns the cached value for key if it exists and isn't
// expired. The boolean reports liveness.
func (c *CricAPISource) cacheGet(key string) (any, bool) {
	c.cacheMu.RLock()
	defer c.cacheMu.RUnlock()
	entry, ok := c.cache[key]
	if !ok {
		return nil, false
	}
	if time.Now().After(entry.expiresAt) {
		return nil, false
	}
	return entry.value, true
}

// cachePut stores value under key with the supplied TTL.
func (c *CricAPISource) cachePut(key string, value any, ttl time.Duration) {
	c.cacheMu.Lock()
	defer c.cacheMu.Unlock()
	c.cache[key] = cacheEntry{value: value, expiresAt: time.Now().Add(ttl)}
}

// baseURL is overridable in tests via setBaseURL so we can point at
// httptest.NewServer. Production code must always use the real CricAPI
// base.
func (c *CricAPISource) baseURL() string {
	if c.testBase != "" {
		return c.testBase
	}
	return cricAPIBase
}

// fetchJSON GETs path with the apikey query param appended, decodes the
// envelope, validates status==success and returns the inner data
// RawMessage. Any non-success path (network error, non-2xx, status!=success)
// returns an error suitable for "delegate to next" — we deliberately
// keep the message small and never echo the API key.
func (c *CricAPISource) fetchJSON(ctx context.Context, path string, extra url.Values) (json.RawMessage, error) {
	u, err := url.Parse(c.baseURL() + path)
	if err != nil {
		return nil, fmt.Errorf("build url: %w", err)
	}
	q := u.Query()
	q.Set("apikey", c.apiKey)
	q.Set("offset", "0")
	for k, vs := range extra {
		for _, v := range vs {
			q.Set(k, v)
		}
	}
	u.RawQuery = q.Encode()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u.String(), nil)
	if err != nil {
		return nil, fmt.Errorf("build request: %w", err)
	}
	resp, err := c.client.Do(req)
	if err != nil {
		// Don't include u.String() here — it carries the apikey query
		// param. Path is enough for debugging.
		return nil, fmt.Errorf("cricapi http %s: %w", path, err)
	}
	defer resp.Body.Close()

	if resp.StatusCode/100 != 2 {
		// Drain a small snippet for context but keep it bounded.
		snippet, _ := io.ReadAll(io.LimitReader(resp.Body, 256))
		return nil, fmt.Errorf("cricapi %s status %d: %s", path, resp.StatusCode, strings.TrimSpace(string(snippet)))
	}

	var env cricResponse
	if err := json.NewDecoder(resp.Body).Decode(&env); err != nil {
		return nil, fmt.Errorf("cricapi %s decode: %w", path, err)
	}
	if !strings.EqualFold(env.Status, "success") {
		return nil, fmt.Errorf("cricapi %s failure: %s", path, env.Reason)
	}
	return env.Data, nil
}

// fetchCurrentMatches returns the parsed /currentMatches data array,
// served from cache when fresh. On any error we return the error
// directly so callers can decide to delegate to next.
func (c *CricAPISource) fetchCurrentMatches(ctx context.Context) ([]cricMatch, error) {
	if v, ok := c.cacheGet("currentMatches"); ok {
		if matches, ok := v.([]cricMatch); ok {
			return matches, nil
		}
	}
	raw, err := c.fetchJSON(ctx, "/currentMatches", nil)
	if err != nil {
		return nil, err
	}
	var matches []cricMatch
	if err := json.Unmarshal(raw, &matches); err != nil {
		return nil, fmt.Errorf("decode currentMatches: %w", err)
	}
	c.cachePut("currentMatches", matches, currentMatchesTTL)
	return matches, nil
}

// CricSeries is the slim shape we need from /series and /series_info.
// CricAPI returns more fields (matchType, totalMatches, etc.) — we only
// model what the cron consumes. Exported because the cron iterates over
// these to pick an active series ID.
type CricSeries struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	StartDate string `json:"startDate"`
	EndDate   string `json:"endDate"`
	Matches   int    `json:"matches"`
}

// SearchSeries hits /series?search=<query> and returns the matching
// series catalog entries. Used by the cron to discover the active IPL
// series ID for the current year, or active international tours.
//
// Cached for 24h per query string — the catalog is stable.
func (c *CricAPISource) SearchSeries(ctx context.Context, query string) ([]CricSeries, error) {
	key := "seriesSearch:" + strings.ToLower(strings.TrimSpace(query))
	if v, ok := c.cacheGet(key); ok {
		if list, ok := v.([]CricSeries); ok {
			return list, nil
		}
	}
	q := url.Values{}
	q.Set("search", query)
	raw, err := c.fetchJSON(ctx, "/series", q)
	if err != nil {
		return nil, err
	}
	var list []CricSeries
	if err := json.Unmarshal(raw, &list); err != nil {
		return nil, fmt.Errorf("decode series: %w", err)
	}
	c.cachePut(key, list, seriesSearchTTL)
	return list, nil
}

// SeriesInfo hits /series_info?id=<id> and returns the series's full
// match list. One call covers every team in the series, which is what
// makes this endpoint cheap for cron pre-seeding (1 hit gives all 74 IPL
// matches).
//
// Cached for 6h per series ID. CricAPI nests the matches under
// data.matchList, so we unmarshal accordingly.
func (c *CricAPISource) SeriesInfo(ctx context.Context, seriesID string) ([]cricMatch, error) {
	key := "seriesInfo:" + seriesID
	if v, ok := c.cacheGet(key); ok {
		if list, ok := v.([]cricMatch); ok {
			return list, nil
		}
	}
	q := url.Values{}
	q.Set("id", seriesID)
	raw, err := c.fetchJSON(ctx, "/series_info", q)
	if err != nil {
		return nil, err
	}
	var info struct {
		MatchList []cricMatch `json:"matchList"`
	}
	if err := json.Unmarshal(raw, &info); err != nil {
		return nil, fmt.Errorf("decode series_info: %w", err)
	}
	c.cachePut(key, info.MatchList, seriesInfoTTL)
	return info.MatchList, nil
}

// SeriesNextMatchPerTeam returns the earliest upcoming (not finished,
// kickoff after `now`) match per team for the given series. Only teams
// with a known canonical ID (i.e. in `canonicalToCricketName`) appear
// in the result — the cron only writes for teams the TV app can render.
//
// Pairs with SearchSeries to drive the cricket cron: search by name,
// pick the active series, fan out per-team next matches in one HTTP call.
func (c *CricAPISource) SeriesNextMatchPerTeam(ctx context.Context, seriesID string, now time.Time) (map[string]api.NextMatch, error) {
	matches, err := c.SeriesInfo(ctx, seriesID)
	if err != nil {
		return nil, err
	}
	// Defensive sort — series_info appears chronological but it isn't
	// guaranteed in the docs.
	sort.SliceStable(matches, func(i, j int) bool {
		return matches[i].DateTimeGMT < matches[j].DateTimeGMT
	})
	out := make(map[string]api.NextMatch)
	for _, m := range matches {
		if matchIsFinished(m.Status) {
			continue
		}
		if m.DateTimeGMT != "" {
			if t, err := time.Parse(time.RFC3339, m.DateTimeGMT); err == nil && t.Before(now) {
				continue
			}
		}
		for _, name := range m.Teams {
			teamID := reverseLookupCanonical(name)
			if teamID == "" {
				continue
			}
			if _, set := out[teamID]; set {
				continue
			}
			oppName := otherTeam(m.Teams, name)
			out[teamID] = api.NextMatch{
				TeamID: teamID,
				Opponent: api.TeamRef{
					ID:   reverseLookupCanonical(oppName),
					Name: oppName,
				},
				Kickoff:     m.DateTimeGMT,
				Competition: competitionFromMatch(m),
				Venue:       m.Venue,
				Source:      SourceCricAPI,
			}
		}
	}
	return out, nil
}

// SeriesUpcomingLive returns scheduled (not-yet-started, not-finished)
// matches in the series as api.LiveCricketMatch values, sorted ascending
// by start time. Past and live matches are filtered out — this is the
// shape the international top-N feed consumes.
func (c *CricAPISource) SeriesUpcomingLive(ctx context.Context, seriesID string, now time.Time) ([]api.LiveCricketMatch, error) {
	matches, err := c.SeriesInfo(ctx, seriesID)
	if err != nil {
		return nil, err
	}
	out := make([]api.LiveCricketMatch, 0, len(matches))
	for _, m := range matches {
		if matchIsFinished(m.Status) {
			continue
		}
		if m.DateTimeGMT == "" {
			continue
		}
		t, err := time.Parse(time.RFC3339, m.DateTimeGMT)
		if err != nil || t.Before(now) {
			continue
		}
		out = append(out, liveCricketFromMatch(m, now))
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].StartTime < out[j].StartTime })
	return out, nil
}

// liveCricketFromMatch lifts a cricMatch into the api.LiveCricketMatch
// shape used by the prebuilt international feed. Status text is left
// verbatim; State is normalized to "scheduled" since this helper is only
// invoked from the upcoming-feed path.
func liveCricketFromMatch(m cricMatch, now time.Time) api.LiveCricketMatch {
	var t1, t2 string
	if len(m.Teams) >= 2 {
		t1, t2 = m.Teams[0], m.Teams[1]
	} else if len(m.Teams) == 1 {
		t1 = m.Teams[0]
	}
	return api.LiveCricketMatch{
		MatchID:   m.ID,
		Series:    competitionFromMatch(m),
		Format:    m.MatchType,
		Status:    m.Status,
		State:     "scheduled",
		Team1:     api.LiveTeam{Name: t1},
		Team2:     api.LiveTeam{Name: t2},
		Venue:     m.Venue,
		StartTime: m.DateTimeGMT,
		UpdatedAt: now.UTC().Format(time.RFC3339),
		Source:    SourceCricAPI,
	}
}

// matchIsFinished returns true if CricAPI's free-text status string
// looks like a completed match. CricAPI doesn't have an enum here, so
// we look for the well-known keywords ("won", "drawn", "tie", "no
// result"). Anything else (including "Match scheduled to begin at...")
// is treated as upcoming.
func matchIsFinished(status string) bool {
	s := strings.ToLower(strings.TrimSpace(status))
	if s == "" {
		return false
	}
	keywords := []string{" won ", " won by", "drawn", "tied", "no result", "match abandoned"}
	// Also catch "X won by Y" appearing at the start.
	for _, k := range keywords {
		if strings.Contains(s, k) {
			return true
		}
	}
	return false
}

// teamsContain reports whether the given teams slice contains target
// (case-insensitive, trim-whitespace). Used to scan currentMatches for
// the team we care about.
func teamsContain(teams []string, target string) bool {
	target = strings.ToLower(strings.TrimSpace(target))
	if target == "" {
		return false
	}
	for _, t := range teams {
		if strings.ToLower(strings.TrimSpace(t)) == target {
			return true
		}
	}
	return false
}

// otherTeam returns the team in `teams` that is NOT target, or "" if
// target isn't present or there's only one team.
func otherTeam(teams []string, target string) string {
	target = strings.ToLower(strings.TrimSpace(target))
	for _, t := range teams {
		if strings.ToLower(strings.TrimSpace(t)) != target {
			return t
		}
	}
	return ""
}

// reverseLookupCanonical scans canonicalToCricketName for an entry whose
// display name matches `name` case-insensitively, returning the
// canonical ID. Empty if no match.
func reverseLookupCanonical(name string) string {
	want := strings.ToLower(strings.TrimSpace(name))
	if want == "" {
		return ""
	}
	for canonical, display := range canonicalToCricketName {
		if strings.ToLower(display) == want {
			return canonical
		}
	}
	return ""
}

// TeamStandings always delegates. Cricket has no league points table in
// the football sense, and synthesizing one would mislead the TV client.
func (c *CricAPISource) TeamStandings(ctx context.Context, teamID string) (api.TeamStandings, error) {
	return c.next.TeamStandings(ctx, teamID)
}

// LeagueStandings always delegates for the same reason.
func (c *CricAPISource) LeagueStandings(ctx context.Context, leagueID string) (api.LeagueStandings, error) {
	return c.next.LeagueStandings(ctx, leagueID)
}

func (c *CricAPISource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	return c.next.TeamLeagues(ctx, teamID)
}

func (c *CricAPISource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	return c.next.AllTeamStandings(ctx, teamID)
}

// NextMatch resolves teamID → CricAPI display name, fetches the cached
// /currentMatches list, and returns the first non-finished match the
// team appears in. On any miss (unknown ID, network error, no match)
// we delegate to next so the chain can produce something.
func (c *CricAPISource) NextMatch(ctx context.Context, teamID string) (api.NextMatch, error) {
	canonical := resolveCanonicalID(teamID)
	displayName, ok := canonicalToCricketName[canonical]
	if !ok {
		// Not a cricket team we know about — delegate immediately.
		return c.next.NextMatch(ctx, teamID)
	}

	cacheKey := "nextMatch:" + canonical
	if v, ok := c.cacheGet(cacheKey); ok {
		if nm, ok := v.(api.NextMatch); ok {
			// Stamp the original teamID back so the response echoes the
			// caller's input regardless of what alias they used.
			nm.TeamID = teamID
			return nm, nil
		}
	}

	matches, err := c.fetchCurrentMatches(ctx)
	if err != nil {
		c.logger.Warn("cricapi currentMatches failed, falling back", "team", teamID, "err", err)
		return c.next.NextMatch(ctx, teamID)
	}

	for _, m := range matches {
		if matchIsFinished(m.Status) {
			continue
		}
		if !teamsContain(m.Teams, displayName) {
			continue
		}
		opponentName := otherTeam(m.Teams, displayName)
		opponentID := reverseLookupCanonical(opponentName)

		out := api.NextMatch{
			TeamID: teamID,
			Opponent: api.TeamRef{
				ID:   opponentID, // may be ""
				Name: opponentName,
			},
			Kickoff:     m.DateTimeGMT,
			Competition: competitionFromMatch(m),
			Venue:       m.Venue,
			Source:      SourceCricAPI,
		}
		// Cache the canonical-form result; the per-call teamID stamping
		// happens on cache read so the cached value is stable across
		// alias variants.
		cached := out
		cached.TeamID = canonical
		c.cachePut(cacheKey, cached, nextMatchTTL)
		return out, nil
	}

	// No upcoming match in the current window. Don't cache misses (the
	// schedule could update soon); just delegate.
	return c.next.NextMatch(ctx, teamID)
}

// competitionFromMatch derives a human-readable competition label from
// a cricMatch. CricAPI's `name` field typically looks like
// "<TeamA> vs <TeamB>, <Nth> Match - <Series Name>". When we can split
// off the series name we use it; otherwise we fall back to the match
// name itself, then the series_id, then a generic label.
func competitionFromMatch(m cricMatch) string {
	if name := strings.TrimSpace(m.Name); name != "" {
		// Heuristic: take the segment after the last " - ", which is
		// usually the series name (e.g. "...Match - IPL 2024").
		if idx := strings.LastIndex(name, " - "); idx > 0 && idx+3 < len(name) {
			tail := strings.TrimSpace(name[idx+3:])
			if tail != "" {
				return tail
			}
		}
		return name
	}
	if id := strings.TrimSpace(m.SeriesID); id != "" {
		return id
	}
	return "Cricket"
}

// LookupTeamByName implements the Source contract. It is reached only
// when the package-level alias-table lookup misses.
//
// Strategy:
//
//  1. Normalize the input via the same normalizer the alias table
//     uses, then check the canonical→cricket display name map
//     directly. A hit returns a high-confidence result.
//  2. Otherwise hit /currentMatches and look for the closest substring
//     match against any team name we see. A hit there returns a
//     lower-confidence result tagged with the match's display name as
//     the canonical name and a slugified id.
//  3. Otherwise ErrNotFound — the caller (package-level helper) will
//     translate that into a 404.
func (c *CricAPISource) LookupTeamByName(ctx context.Context, name string) (*api.TeamLookup, error) {
	q := strings.TrimSpace(name)
	if q == "" {
		return nil, ErrNotFound
	}
	norm := normalizeTeamName(q)
	if norm == "" {
		return nil, ErrNotFound
	}

	// 1) canonical map exact (after both sides normalized).
	for canonical, display := range canonicalToCricketName {
		if normalizeTeamName(display) == norm {
			meta, ok := teamCatalog[canonical]
			if !ok {
				// Should not happen — every cricket id we care about is in the
				// catalog — but guard anyway.
				continue
			}
			return &api.TeamLookup{
				Query:         q,
				TeamID:        meta.ID,
				CanonicalName: meta.CanonicalName,
				League:        meta.League,
				Confidence:    0.9,
				Source:        SourceCricAPI,
			}, nil
		}
	}

	// 2) substring scan against currentMatches' teams.
	matches, err := c.fetchCurrentMatches(ctx)
	if err != nil {
		// Network failure → fall through to next. The wider chain
		// handles the actual translation to 404 if everything misses.
		return c.next.LookupTeamByName(ctx, name)
	}
	if hit := substringTeamMatch(matches, norm); hit != "" {
		return &api.TeamLookup{
			Query:         q,
			TeamID:        slugify(hit),
			CanonicalName: hit,
			League:        "Cricket",
			Confidence:    0.6,
			Source:        SourceCricAPI,
		}, nil
	}
	return nil, ErrNotFound
}

// substringTeamMatch scans every team name across `matches` and returns
// the first one whose normalized form contains, or is contained in, the
// query. Returns the original (un-normalized) team name on a hit.
func substringTeamMatch(matches []cricMatch, normQuery string) string {
	if normQuery == "" {
		return ""
	}
	seen := make(map[string]struct{})
	for _, m := range matches {
		for _, t := range m.Teams {
			if _, dup := seen[t]; dup {
				continue
			}
			seen[t] = struct{}{}
			n := normalizeTeamName(t)
			if n == "" {
				continue
			}
			if n == normQuery || strings.Contains(n, normQuery) || strings.Contains(normQuery, n) {
				return t
			}
		}
	}
	return ""
}

// slugify converts a free-text name to a kebab-case identifier suitable
// for use as a TeamID in api.TeamLookup. Lowercase, drop punctuation,
// collapse whitespace to single hyphens. Thin wrapper over the public
// SlugifyTeam helper so existing call sites compile unchanged.
func slugify(in string) string { return SlugifyTeam(in) }

// SlugifyTeam is the public, package-level slug function used by both
// the cricapi source and the cron when it needs to mint an ID for a
// team that isn't in any canonical reverse-map.
func SlugifyTeam(in string) string {
	s := strings.ToLower(strings.TrimSpace(in))
	if s == "" {
		return ""
	}
	s = punctRE.ReplaceAllString(s, " ")
	s = whitespaceRE.ReplaceAllString(s, " ")
	s = strings.TrimSpace(s)
	return strings.ReplaceAll(s, " ", "-")
}

// matchOutcome is the parsed result of a single match, used to tally
// per-team rows when synthesizing a league standings table from raw
// CricAPI status strings. Winner is the canonical (display) team name
// from the supplied teams[] slice — empty for ties or no-results.
type matchOutcome struct {
	finished   bool
	winner     string
	isTie      bool
	isNoResult bool
}

// parseMatchOutcome interprets CricAPI's free-text status string for
// one match and pairs it with the match's two-team slice to figure out
// who won, whether it was a tie or no-result, and whether the match is
// finished at all. CricAPI is fuzzy here so the parser is intentionally
// forgiving and case-insensitive.
//
// Recognized patterns:
//
//   - "<TeamName> won by N wkts" / "won by N wickets" / "won by N runs"
//     (with or without "(DLS Method)" suffix)
//   - "Match tied (XYZ won the Super Over)" — XYZ is resolved via
//     cricShortAliases first, then via case-insensitive substring scan
//     of the teams[] slice.
//   - "Match tied" — pure tie, no winner.
//   - "No result" / "Match abandoned" — no result.
//   - Anything else (including "Match starts at...") — not finished.
func parseMatchOutcome(status string, teams []string) matchOutcome {
	s := strings.ToLower(strings.TrimSpace(status))
	if s == "" {
		return matchOutcome{}
	}

	// No-result variants first; these never have a winner.
	if strings.Contains(s, "no result") || strings.Contains(s, "match abandoned") {
		return matchOutcome{finished: true, isNoResult: true}
	}

	// Tie + Super Over: pull the suffix and try to resolve the winner.
	if strings.Contains(s, "tied") && strings.Contains(s, "super over") {
		// Look for "(<team> won the super over)" — accept any wrapping.
		// We split on "won the super over" and take the token immediately
		// before it as the candidate name.
		if idx := strings.Index(s, "won the super over"); idx > 0 {
			head := strings.TrimSpace(s[:idx])
			// Strip any leading "match tied (" or trailing punctuation.
			head = strings.TrimLeft(head, "(")
			head = strings.TrimSpace(head)
			if openIdx := strings.LastIndex(head, "("); openIdx >= 0 {
				head = strings.TrimSpace(head[openIdx+1:])
			}
			if winner := findWinnerInTeams(head, teams); winner != "" {
				return matchOutcome{finished: true, winner: winner}
			}
		}
		// Couldn't resolve — treat as a real tie.
		return matchOutcome{finished: true, isTie: true}
	}

	// Pure tie.
	if strings.Contains(s, "match tied") || s == "tied" {
		return matchOutcome{finished: true, isTie: true}
	}

	// "<Team> won by ..." — winner is everything before the first " won ".
	if idx := strings.Index(s, " won "); idx > 0 {
		candidate := strings.TrimSpace(s[:idx])
		if winner := findWinnerInTeams(candidate, teams); winner != "" {
			return matchOutcome{finished: true, winner: winner}
		}
		// "won" present but we can't pin a team — log and treat as tie.
		// (Logging is left to the caller so this stays a pure parser.)
		return matchOutcome{finished: true, isTie: true}
	}

	// Unrecognized — not finished.
	return matchOutcome{}
}

// findWinnerInTeams resolves a candidate team string (which may be a
// short alias like "kkr" or a partial display name like "kolkata") to
// the matching canonical name from teams[]. Returns "" if no match.
func findWinnerInTeams(candidate string, teams []string) string {
	cand := strings.ToLower(strings.TrimSpace(candidate))
	if cand == "" {
		return ""
	}
	// Alias path: "kkr" → "kolkata-knight-riders" → "Kolkata Knight Riders".
	if canonical, ok := cricShortAliases[cand]; ok {
		if display, ok := canonicalToCricketName[canonical]; ok {
			for _, t := range teams {
				if strings.EqualFold(strings.TrimSpace(t), display) {
					return t
				}
			}
		}
	}
	// Substring path: candidate ⊆ team name OR team name ⊆ candidate.
	for _, t := range teams {
		tl := strings.ToLower(strings.TrimSpace(t))
		if tl == "" {
			continue
		}
		if tl == cand || strings.Contains(tl, cand) || strings.Contains(cand, tl) {
			return t
		}
	}
	return ""
}

// SeriesStandings derives a league standings table for the given series
// by parsing the result string of every completed match returned by
// /series_info. CricAPI's free tier doesn't expose a standings
// endpoint, so this is a local computation over data we already cache.
//
// Cached for seriesInfoTTL under "seriesStandings:<id>". Returns an
// empty table (no error) if the series has no finished matches yet.
func (c *CricAPISource) SeriesStandings(ctx context.Context, seriesID string) (api.LeagueStandings, error) {
	cacheKey := "seriesStandings:" + seriesID
	if v, ok := c.cacheGet(cacheKey); ok {
		if ls, ok := v.(api.LeagueStandings); ok {
			return ls, nil
		}
	}

	matches, err := c.SeriesInfo(ctx, seriesID)
	if err != nil {
		return api.LeagueStandings{}, err
	}

	// Chronological sort so RecentForm reflects actual order.
	sort.SliceStable(matches, func(i, j int) bool {
		return matches[i].DateTimeGMT < matches[j].DateTimeGMT
	})

	type row struct {
		name   string
		played int
		wins   int
		losses int
		tienr  int // ties + no-results, both worth 1 point in IPL
		points int
		form   []string
	}
	rows := make(map[string]*row)
	getRow := func(name string) *row {
		key := strings.ToLower(strings.TrimSpace(name))
		if r, ok := rows[key]; ok {
			return r
		}
		r := &row{name: name}
		rows[key] = r
		return r
	}

	leagueName := ""
	for _, m := range matches {
		if leagueName == "" {
			leagueName = competitionFromMatch(m)
		}
		if len(m.Teams) != 2 {
			continue
		}
		out := parseMatchOutcome(m.Status, m.Teams)
		if !out.finished {
			continue
		}

		t1, t2 := m.Teams[0], m.Teams[1]
		r1, r2 := getRow(t1), getRow(t2)
		r1.played++
		r2.played++

		switch {
		case out.isNoResult, out.isTie:
			r1.tienr++
			r2.tienr++
			r1.points++
			r2.points++
			letter := "N"
			if out.isTie {
				letter = "T"
			}
			r1.form = append(r1.form, letter)
			r2.form = append(r2.form, letter)
		case out.winner != "":
			winner := getRow(out.winner)
			winner.wins++
			winner.points += 2
			winner.form = append(winner.form, "W")
			loserName := otherTeam(m.Teams, out.winner)
			if loserName != "" {
				loser := getRow(loserName)
				loser.losses++
				loser.form = append(loser.form, "L")
			}
		default:
			// Status said "won" but we couldn't resolve the team. Log and
			// score as a tie so totals still balance.
			c.logger.Warn("series standings: could not parse winner", "status", m.Status)
			r1.tienr++
			r2.tienr++
			r1.points++
			r2.points++
			r1.form = append(r1.form, "T")
			r2.form = append(r2.form, "T")
		}
	}

	table := make([]api.LeagueTableRow, 0, len(rows))
	for _, r := range rows {
		form := r.form
		if len(form) > 5 {
			form = form[len(form)-5:]
		}
		table = append(table, api.LeagueTableRow{
			TeamID:     reverseLookupCanonical(r.name),
			TeamName:   r.name,
			Played:     r.played,
			Wins:       r.wins,
			Draws:      0,
			Losses:     r.losses,
			Points:     r.points,
			RecentForm: form,
		})
	}

	sort.SliceStable(table, func(i, j int) bool {
		if table[i].Points != table[j].Points {
			return table[i].Points > table[j].Points
		}
		if table[i].Wins != table[j].Wins {
			return table[i].Wins > table[j].Wins
		}
		return table[i].TeamName < table[j].TeamName
	})
	for i := range table {
		table[i].Position = i + 1
	}

	out := api.LeagueStandings{
		LeagueID: seriesID,
		Name:     leagueName,
		Table:    table,
		Source:   SourceCricAPI,
	}
	c.cachePut(cacheKey, out, seriesInfoTTL)
	return out, nil
}
