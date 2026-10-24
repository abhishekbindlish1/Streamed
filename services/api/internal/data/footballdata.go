// Package data, footballdata.go: real football standings + fixtures
// from football-data.org's free tier.
//
// The free tier has two important quirks this source has to navigate:
//
//  1. Rate limit of 10 req/min. We mitigate it with a small in-process
//     cache (TTL 6h) keyed by endpoint+id. A hot match-day refresh
//     therefore costs ~1 request per league per six hours.
//  2. Only a handful of competitions are exposed. The big-five domestic
//     leagues (PL, PD, SA, BL1, FL1) plus the World Cup are free; UEFA
//     Champions League and others return 403. We treat 403 as
//     "fall through to the next source", logging it once.
//
// Anything we can't answer (unmapped team id, network failure, 404 from
// football-data, 403 on a tier-restricted competition) is delegated to
// the wrapped `next data.Source` so callers never see a 5xx that the
// stub or LLM source could have answered.
package data

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/abindlish/streamed-backend/internal/api"
)

// SourceFootballData is the api.Source value stamped on every payload
// the football-data.org backend produces.
const SourceFootballData = "football-data.org"

// footballDataBase is the v4 REST root. Overridable in tests via the
// unexported baseURL field on FootballDataSource.
const footballDataBase = "https://api.football-data.org/v4"

// cacheTTL is how long a successful response is held in the in-process
// cache before we re-fetch. 6h matches the typical match-day refresh
// cadence and keeps us comfortably under the 10 req/min ceiling.
const cacheTTL = 6 * time.Hour

// canonicalToFootballDataID maps our kebab-case canonical team IDs
// onto the numeric IDs football-data.org uses in its REST URLs. Adding
// a team here is what enables real data for it; without an entry the
// source delegates to `next` for that team.
//
// IDs verified against:
//   GET https://api.football-data.org/v4/competitions/PL/teams
//   GET https://api.football-data.org/v4/competitions/PD/teams
var canonicalToFootballDataID = map[string]int{
	// Premier League.
	"arsenal":     57,
	"chelsea":     61,
	"liverpool":   64,
	"man-city":    65,
	"man-united":  66,
	"spurs":       73,
	"aston-villa": 58,
	"newcastle":   67,

	// La Liga.
	"barcelona":       81,
	"real-madrid":     86,
	"atletico-madrid": 78,
	"girona":          298,
}

// canonicalToFootballDataLeague maps our league IDs to football-data
// competition codes. Free-tier coverage only — Champions League (CL)
// is included optimistically but will 403 on the free tier; the
// status-code handler treats that as a delegate-to-next signal.
var canonicalToFootballDataLeague = map[string]string{
	"epl":        "PL",
	"laliga":     "PD",
	"seriea":     "SA",
	"bundesliga": "BL1",
	"ligue1":     "FL1",
	"ucl":        "CL",
}

// teamLeagueCode returns the football-data competition code for the
// league the given team plays in. Used by TeamStandings to decide
// which standings table to fetch. Falls back to "" when we don't
// have a mapping (the caller delegates in that case).
func teamLeagueCode(canonicalID string) string {
	switch canonicalID {
	case "arsenal", "chelsea", "liverpool", "man-city", "man-united", "spurs", "aston-villa", "newcastle":
		return "PL"
	case "barcelona", "real-madrid", "atletico-madrid", "girona":
		return "PD"
	}
	return ""
}

// reverseFootballDataID returns the canonical kebab-case ID for a
// football-data numeric team ID, or "" if we have no mapping for it.
// Built lazily so adding rows to canonicalToFootballDataID
// automatically extends reverse lookup.
func reverseFootballDataID(numID int) string {
	for canonical, id := range canonicalToFootballDataID {
		if id == numID {
			return canonical
		}
	}
	return ""
}

// We re-use the package-level cacheEntry type (declared in cricapi.go)
// for our in-process TTL cache. The shape is identical: an opaque
// value plus an absolute expiry.

// FootballDataSource is a data.Source backed by football-data.org. It
// always wraps another Source as its fallback chain and delegates on
// any miss, error, or 4xx the wrapped source might still answer.
type FootballDataSource struct {
	apiKey  string
	baseURL string
	client  *http.Client
	next    Source
	logger  *slog.Logger

	// cache is a tiny in-memory TTL cache. Key format is
	// "<endpoint>:<id>" e.g. "standings:PL", "matches:57".
	mu       sync.RWMutex
	cache    map[string]cacheEntry
	loggedTier403 bool
}

// NewFootballDataSource constructs the source. apiKey must come from
// the FOOTBALL_DATA_KEY env var at the call site — never hardcode it.
// next is the fallback Source consulted whenever we can't answer.
func NewFootballDataSource(apiKey string, next Source, logger *slog.Logger) *FootballDataSource {
	if logger == nil {
		logger = slog.Default()
	}
	return &FootballDataSource{
		apiKey:  apiKey,
		baseURL: footballDataBase,
		client:  &http.Client{Timeout: 8 * time.Second},
		next:    next,
		logger:  logger,
		cache:   make(map[string]cacheEntry),
	}
}

func (f *FootballDataSource) Name() string { return SourceFootballData }

// errTierRestricted signals a 403 from football-data — the requested
// competition isn't on the free tier. Treated as "delegate to next"
// rather than a hard error.
var errTierRestricted = errors.New("football-data: tier restricted")

// errFDNotFound signals a 404 from football-data. Same handling as
// errTierRestricted: delegate to next.
var errFDNotFound = errors.New("football-data: not found")

// cacheGet returns a cached value if present and unexpired. The bool
// is false on miss or expiry.
func (f *FootballDataSource) cacheGet(key string) (any, bool) {
	f.mu.RLock()
	entry, ok := f.cache[key]
	f.mu.RUnlock()
	if !ok {
		return nil, false
	}
	if time.Now().After(entry.expiresAt) {
		return nil, false
	}
	return entry.value, true
}

// cachePut stores value under key with the configured TTL. Replaces
// any existing entry for the same key.
func (f *FootballDataSource) cachePut(key string, value any) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.cache[key] = cacheEntry{value: value, expiresAt: time.Now().Add(cacheTTL)}
}

// doGet performs a GET against the football-data API and decodes the
// response into out. Returns errTierRestricted on 403, errFDNotFound
// on 404, and a wrapped error otherwise.
func (f *FootballDataSource) doGet(ctx context.Context, path string, out any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, f.baseURL+path, nil)
	if err != nil {
		return fmt.Errorf("build request: %w", err)
	}
	// X-Auth-Token carries the API key. It's only set on the outbound
	// request and is never logged.
	req.Header.Set("X-Auth-Token", f.apiKey)
	req.Header.Set("Accept", "application/json")

	resp, err := f.client.Do(req)
	if err != nil {
		return fmt.Errorf("football-data http: %w", err)
	}
	defer resp.Body.Close()

	switch resp.StatusCode {
	case http.StatusOK:
		// Fall through.
	case http.StatusForbidden:
		// Drain the body but don't echo it (no need to log API responses).
		_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
		return errTierRestricted
	case http.StatusNotFound:
		_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, 1024))
		return errFDNotFound
	default:
		snippet, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
		return fmt.Errorf("football-data status %d: %s", resp.StatusCode, strings.TrimSpace(string(snippet)))
	}

	if err := json.NewDecoder(resp.Body).Decode(out); err != nil {
		return fmt.Errorf("decode response: %w", err)
	}
	return nil
}

// fdStandingsResponse mirrors GET /competitions/{code}/standings. We
// only model the fields we use; everything else is ignored by the
// json decoder.
type fdStandingsResponse struct {
	Competition struct {
		Name string `json:"name"`
		Code string `json:"code"`
	} `json:"competition"`
	Season struct {
		// e.g. "2025-08-15"
		StartDate string `json:"startDate"`
		EndDate   string `json:"endDate"`
	} `json:"season"`
	Standings []struct {
		// Type is "TOTAL", "HOME", or "AWAY". We always pick TOTAL.
		Type  string `json:"type"`
		Table []fdTableRow `json:"table"`
	} `json:"standings"`
}

// fdTableRow is one row of a league standings table.
type fdTableRow struct {
	Position    int `json:"position"`
	PlayedGames int `json:"playedGames"`
	Won         int `json:"won"`
	Draw        int `json:"draw"`
	Lost        int `json:"lost"`
	Points      int `json:"points"`
	Form        string `json:"form"`
	Team        struct {
		ID        int    `json:"id"`
		Name      string `json:"name"`
		ShortName string `json:"shortName"`
		TLA       string `json:"tla"`
	} `json:"team"`
}

// fdMatchesResponse mirrors GET /teams/{id}/matches?status=SCHEDULED.
type fdMatchesResponse struct {
	Matches []fdMatch `json:"matches"`
}

// fdMatch is one scheduled fixture.
type fdMatch struct {
	UTCDate     string `json:"utcDate"`
	Venue       string `json:"venue"`
	Competition struct {
		Name string `json:"name"`
		Code string `json:"code"`
	} `json:"competition"`
	HomeTeam fdTeamRef `json:"homeTeam"`
	AwayTeam fdTeamRef `json:"awayTeam"`
}

// fdTeamRef is a slim team identifier embedded in a match.
type fdTeamRef struct {
	ID        int    `json:"id"`
	Name      string `json:"name"`
	ShortName string `json:"shortName"`
	TLA       string `json:"tla"`
}

// fetchStandings pulls and caches the TOTAL standings for a league.
// Returns errTierRestricted if the competition is gated to a paid
// tier; the caller should fall through to next on that.
func (f *FootballDataSource) fetchStandings(ctx context.Context, leagueCode string) (*fdStandingsResponse, error) {
	key := "standings:" + leagueCode
	if cached, ok := f.cacheGet(key); ok {
		return cached.(*fdStandingsResponse), nil
	}
	var resp fdStandingsResponse
	if err := f.doGet(ctx, "/competitions/"+leagueCode+"/standings", &resp); err != nil {
		return nil, err
	}
	f.cachePut(key, &resp)
	return &resp, nil
}

// fetchNextMatch pulls the next scheduled fixture for a team.
func (f *FootballDataSource) fetchNextMatch(ctx context.Context, teamID int) (*fdMatch, error) {
	key := fmt.Sprintf("nextmatch:%d", teamID)
	if cached, ok := f.cacheGet(key); ok {
		m := cached.(*fdMatch)
		return m, nil
	}
	var resp fdMatchesResponse
	path := fmt.Sprintf("/teams/%d/matches?status=SCHEDULED&limit=1", teamID)
	if err := f.doGet(ctx, path, &resp); err != nil {
		return nil, err
	}
	if len(resp.Matches) == 0 {
		return nil, errFDNotFound
	}
	m := resp.Matches[0]
	f.cachePut(key, &m)
	return &m, nil
}

// totalTable picks the TOTAL standings table from a response. Returns
// nil if absent (some competitions have group-stage standings only).
func totalTable(resp *fdStandingsResponse) []fdTableRow {
	for _, s := range resp.Standings {
		if s.Type == "TOTAL" || s.Type == "" {
			return s.Table
		}
	}
	if len(resp.Standings) > 0 {
		return resp.Standings[0].Table
	}
	return nil
}

// formStringToSlice parses football-data's compact form string
// ("WDLWW") into a 5-element slice. The API's separator varies — we
// accept comma, slash, or none — and trims to at most 5 entries.
func formStringToSlice(form string) []string {
	if form == "" {
		return nil
	}
	// Accept either a separated form ("W,D,L,W,W") or a packed one
	// ("WDLWW"). Both shapes have appeared in football-data responses.
	cleaned := strings.NewReplacer(",", "", " ", "", "/", "", "-", "").Replace(form)
	cleaned = strings.ToUpper(cleaned)
	out := make([]string, 0, 5)
	for _, r := range cleaned {
		if r == 'W' || r == 'D' || r == 'L' {
			out = append(out, string(r))
		}
		if len(out) == 5 {
			break
		}
	}
	return out
}

// shouldDelegate returns true for errors that mean "next source might
// be able to answer this" — tier restrictions, not-found, and any
// transport/decode failure. A nil error returns false.
func shouldDelegate(err error) bool {
	if err == nil {
		return false
	}
	if errors.Is(err, errTierRestricted) || errors.Is(err, errFDNotFound) {
		return true
	}
	return true // network and decode errors should fall through too.
}

// logTier403Once emits a single startup log when we first observe a
// 403 on a tier-restricted competition. Subsequent 403s are silent so
// we don't fill logs on every request for the same league.
func (f *FootballDataSource) logTier403Once(leagueCode string) {
	f.mu.Lock()
	first := !f.loggedTier403
	f.loggedTier403 = true
	f.mu.Unlock()
	if first {
		f.logger.Info("football-data: competition gated to paid tier; delegating to next source", "competition", leagueCode)
	}
}

// TeamStandings returns the league row for the given team. Delegates
// to next on unmapped team, tier restriction, or any HTTP failure.
func (f *FootballDataSource) TeamStandings(ctx context.Context, teamID string) (api.TeamStandings, error) {
	canonical := strings.ToLower(teamID)
	fdID, ok := canonicalToFootballDataID[canonical]
	if !ok {
		return f.next.TeamStandings(ctx, teamID)
	}
	leagueCode := teamLeagueCode(canonical)
	if leagueCode == "" {
		return f.next.TeamStandings(ctx, teamID)
	}

	resp, err := f.fetchStandings(ctx, leagueCode)
	if err != nil {
		if errors.Is(err, errTierRestricted) {
			f.logTier403Once(leagueCode)
		} else {
			f.logger.Warn("football-data team standings failed, falling back", "team", teamID, "err", err)
		}
		return f.next.TeamStandings(ctx, teamID)
	}

	for _, row := range totalTable(resp) {
		if row.Team.ID != fdID {
			continue
		}
		out := api.TeamStandings{
			TeamID:     canonical,
			League:     resp.Competition.Name,
			Position:   row.Position,
			Played:     row.PlayedGames,
			Wins:       row.Won,
			Draws:      row.Draw,
			Losses:     row.Lost,
			Points:     row.Points,
			RecentForm: formStringToSlice(row.Form),
			Source:     SourceFootballData,
		}
		return out, nil
	}

	// Team mapped but not present in the table — possibly relegated /
	// promoted out of season alignment. Delegate.
	return f.next.TeamStandings(ctx, teamID)
}

// NextMatch returns the next scheduled fixture for the team.
func (f *FootballDataSource) NextMatch(ctx context.Context, teamID string) (api.NextMatch, error) {
	canonical := strings.ToLower(teamID)
	fdID, ok := canonicalToFootballDataID[canonical]
	if !ok {
		return f.next.NextMatch(ctx, teamID)
	}
	m, err := f.fetchNextMatch(ctx, fdID)
	if err != nil {
		if errors.Is(err, errTierRestricted) {
			f.logTier403Once(fmt.Sprintf("team-%d", fdID))
		} else if !errors.Is(err, errFDNotFound) {
			f.logger.Warn("football-data next match failed, falling back", "team", teamID, "err", err)
		}
		return f.next.NextMatch(ctx, teamID)
	}

	// The team that ISN'T ours is the opponent.
	var opp fdTeamRef
	if m.HomeTeam.ID == fdID {
		opp = m.AwayTeam
	} else {
		opp = m.HomeTeam
	}

	return api.NextMatch{
		TeamID: canonical,
		Opponent: api.TeamRef{
			ID:   reverseFootballDataID(opp.ID),
			Name: opp.Name,
		},
		Kickoff:     m.UTCDate,
		Competition: m.Competition.Name,
		Venue:       m.Venue,
		Source:      SourceFootballData,
	}, nil
}

// LeagueStandings returns the full league table.
func (f *FootballDataSource) LeagueStandings(ctx context.Context, leagueID string) (api.LeagueStandings, error) {
	canonical := strings.ToLower(leagueID)
	leagueCode, ok := canonicalToFootballDataLeague[canonical]
	if !ok {
		return f.next.LeagueStandings(ctx, leagueID)
	}

	resp, err := f.fetchStandings(ctx, leagueCode)
	if err != nil {
		if errors.Is(err, errTierRestricted) {
			f.logTier403Once(leagueCode)
		} else {
			f.logger.Warn("football-data league standings failed, falling back", "league", leagueID, "err", err)
		}
		return f.next.LeagueStandings(ctx, leagueID)
	}

	rows := totalTable(resp)
	out := api.LeagueStandings{
		LeagueID: canonical,
		Name:     resp.Competition.Name,
		Season:   seasonLabel(resp.Season.StartDate, resp.Season.EndDate),
		Table:    make([]api.LeagueTableRow, 0, len(rows)),
		Source:   SourceFootballData,
	}
	for _, r := range rows {
		out.Table = append(out.Table, api.LeagueTableRow{
			Position:   r.Position,
			TeamID:     teamIDOrSlug(fdTeamRef{ID: r.Team.ID, Name: r.Team.Name}),
			TeamName:   r.Team.Name,
			Played:     r.PlayedGames,
			Wins:       r.Won,
			Draws:      r.Draw,
			Losses:     r.Lost,
			Points:     r.Points,
			RecentForm: formStringToSlice(r.Form),
		})
	}
	return out, nil
}

// LeagueScheduledMatches returns all scheduled (not-yet-played) matches
// for a league, in chronological order (earliest first). Used by the
// cron to derive next-match-per-team for every team in the league via a
// single API call.
//
// Delegates to next on unmapped league or any failure (including tier
// restriction) so cron callers can still try the chain's other writers.
func (f *FootballDataSource) LeagueScheduledMatches(ctx context.Context, leagueID string) ([]fdMatch, error) {
	canonical := strings.ToLower(leagueID)
	leagueCode, ok := canonicalToFootballDataLeague[canonical]
	if !ok {
		return nil, fmt.Errorf("league %q not mapped for football-data", leagueID)
	}
	key := "league-scheduled:" + leagueCode
	if cached, ok := f.cacheGet(key); ok {
		return cached.([]fdMatch), nil
	}
	var resp fdMatchesResponse
	if err := f.doGet(ctx, "/competitions/"+leagueCode+"/matches?status=SCHEDULED", &resp); err != nil {
		if errors.Is(err, errTierRestricted) {
			f.logTier403Once(leagueCode)
		}
		return nil, fmt.Errorf("league scheduled matches %s: %w", leagueCode, err)
	}
	// Already chronological from football-data, but defensively sort.
	sort.SliceStable(resp.Matches, func(i, j int) bool {
		return resp.Matches[i].UTCDate < resp.Matches[j].UTCDate
	})
	f.cachePut(key, resp.Matches)
	return resp.Matches, nil
}

// LeagueNextMatchPerTeam fetches all scheduled fixtures in a league and
// returns the earliest upcoming match for each team. Teams in our
// canonical reverse-map use that ID; otherwise we slugify the football-
// data team name. The cron writes for every team so the TV can browse
// non-canonical teams (Brentford, Mallorca, etc.) without needing
// canonical entries up front.
//
// One HTTP call covers ~20 teams, which is what makes the league-driven
// cron strategy cheap.
func (f *FootballDataSource) LeagueNextMatchPerTeam(ctx context.Context, leagueID string) (map[string]api.NextMatch, error) {
	matches, err := f.LeagueScheduledMatches(ctx, leagueID)
	if err != nil {
		return nil, err
	}
	out := make(map[string]api.NextMatch)
	for _, m := range matches { // already sorted ascending by UTCDate
		for _, side := range []fdTeamRef{m.HomeTeam, m.AwayTeam} {
			teamID := teamIDOrSlug(side)
			if teamID == "" {
				continue
			}
			if _, set := out[teamID]; set {
				continue // first hit per team is the earliest
			}
			opp := m.AwayTeam
			if side.ID == m.AwayTeam.ID {
				opp = m.HomeTeam
			}
			out[teamID] = api.NextMatch{
				TeamID: teamID,
				Opponent: api.TeamRef{
					ID:   teamIDOrSlug(opp),
					Name: opp.Name,
				},
				Kickoff:     m.UTCDate,
				Competition: m.Competition.Name,
				Venue:       m.Venue,
				Source:      SourceFootballData,
			}
		}
	}
	return out, nil
}

// teamIDOrSlug returns the canonical ID for a football-data team if it's
// in our reverse-map; otherwise slugifies the display name so the cron
// can still write a doc for Brentford, Mallorca, etc.
func teamIDOrSlug(t fdTeamRef) string {
	if id := reverseFootballDataID(t.ID); id != "" {
		return id
	}
	return SlugifyTeam(t.Name)
}

// LookupTeamByName is out of scope for this source — the package-level
// alias table handles all our currently-mapped teams before reaching
// the source chain. Defer to next so the OpenAI fallback (if wired)
// can still answer for unknown names.
func (f *FootballDataSource) LookupTeamByName(ctx context.Context, name string) (*api.TeamLookup, error) {
	return f.next.LookupTeamByName(ctx, name)
}

func (f *FootballDataSource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	return f.next.TeamLeagues(ctx, teamID)
}

func (f *FootballDataSource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	return f.next.AllTeamStandings(ctx, teamID)
}

// seasonLabel formats a season window like "2025-08-15"/"2026-05-25"
// into "2025/26". Falls back to the raw start year when end is empty.
func seasonLabel(start, end string) string {
	if len(start) < 4 {
		return ""
	}
	startYear := start[:4]
	if len(end) < 4 {
		return startYear
	}
	endYear := end[:4]
	if startYear == endYear {
		return startYear
	}
	// "2025"/"2026" -> "2025/26".
	return startYear + "/" + endYear[2:]
}
