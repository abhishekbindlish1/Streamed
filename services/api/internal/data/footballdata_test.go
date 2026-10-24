package data

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/abindlish/streamed-backend/internal/api"
)

// fakeNext is a minimal Source for use as the fallback chain in tests.
// Each method records that it was called and returns canned data the
// test can assert against.
type fakeNext struct {
	teamHits, matchHits, leagueHits, lookupHits int32
	teamReply                                   api.TeamStandings
	matchReply                                  api.NextMatch
	leagueReply                                 api.LeagueStandings
	teamErr, matchErr, leagueErr                error
}

func (f *fakeNext) Name() string { return "fake-next" }

func (f *fakeNext) TeamStandings(_ context.Context, teamID string) (api.TeamStandings, error) {
	atomic.AddInt32(&f.teamHits, 1)
	if f.teamErr != nil {
		return api.TeamStandings{}, f.teamErr
	}
	out := f.teamReply
	if out.TeamID == "" {
		out.TeamID = teamID
	}
	if out.Source == "" {
		out.Source = "fake-next"
	}
	return out, nil
}

func (f *fakeNext) NextMatch(_ context.Context, teamID string) (api.NextMatch, error) {
	atomic.AddInt32(&f.matchHits, 1)
	if f.matchErr != nil {
		return api.NextMatch{}, f.matchErr
	}
	out := f.matchReply
	if out.TeamID == "" {
		out.TeamID = teamID
	}
	if out.Source == "" {
		out.Source = "fake-next"
	}
	return out, nil
}

func (f *fakeNext) LeagueStandings(_ context.Context, leagueID string) (api.LeagueStandings, error) {
	atomic.AddInt32(&f.leagueHits, 1)
	if f.leagueErr != nil {
		return api.LeagueStandings{}, f.leagueErr
	}
	out := f.leagueReply
	if out.LeagueID == "" {
		out.LeagueID = leagueID
	}
	if out.Source == "" {
		out.Source = "fake-next"
	}
	return out, nil
}

func (f *fakeNext) LookupTeamByName(_ context.Context, _ string) (*api.TeamLookup, error) {
	atomic.AddInt32(&f.lookupHits, 1)
	return nil, ErrNotFound
}
func (f *fakeNext) TeamLeagues(_ context.Context, _ string) ([]string, error) {
	return []string{}, nil
}
func (f *fakeNext) AllTeamStandings(_ context.Context, _ string) ([]api.TeamStandings, error) {
	return nil, nil
}

// fdMockServer wires up an httptest.NewServer with the canonical
// football-data v4 endpoints we exercise. Pass "" for a body to
// return a 404 for that endpoint; pass nil to skip the registration.
type fdMockServer struct {
	t              *testing.T
	standingsByCode map[string]string // code -> JSON body (or "" for 404)
	statusByCode    map[string]int    // code -> override status
	matchesByTeam   map[int]string    // numeric team id -> JSON body
	statusByTeam    map[int]int

	standingsCalls map[string]int
	matchesCalls   map[int]int

	srv *httptest.Server
}

func newFDMock(t *testing.T) *fdMockServer {
	m := &fdMockServer{
		t:               t,
		standingsByCode: map[string]string{},
		statusByCode:    map[string]int{},
		matchesByTeam:   map[int]string{},
		statusByTeam:    map[int]int{},
		standingsCalls:  map[string]int{},
		matchesCalls:    map[int]int{},
	}
	m.srv = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Verify the auth token header is set on every request, but
		// don't echo or log it.
		if got := r.Header.Get("X-Auth-Token"); got == "" {
			t.Errorf("missing X-Auth-Token header on %s", r.URL.Path)
		}
		path := r.URL.Path
		switch {
		case strings.HasPrefix(path, "/competitions/") && strings.HasSuffix(path, "/standings"):
			code := strings.TrimSuffix(strings.TrimPrefix(path, "/competitions/"), "/standings")
			m.standingsCalls[code]++
			body, ok := m.standingsByCode[code]
			if status, hasStatus := m.statusByCode[code]; hasStatus {
				w.WriteHeader(status)
				_, _ = io.WriteString(w, body)
				return
			}
			if !ok || body == "" {
				w.WriteHeader(http.StatusNotFound)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			_, _ = io.WriteString(w, body)
		case strings.HasPrefix(path, "/teams/") && strings.HasSuffix(path, "/matches"):
			// /teams/{id}/matches
			rest := strings.TrimPrefix(path, "/teams/")
			rest = strings.TrimSuffix(rest, "/matches")
			var teamID int
			if _, err := jsonScanInt(rest, &teamID); err != nil {
				t.Errorf("bad team id in path %q", path)
				w.WriteHeader(http.StatusBadRequest)
				return
			}
			m.matchesCalls[teamID]++
			body, ok := m.matchesByTeam[teamID]
			if status, hasStatus := m.statusByTeam[teamID]; hasStatus {
				w.WriteHeader(status)
				_, _ = io.WriteString(w, body)
				return
			}
			if !ok || body == "" {
				w.WriteHeader(http.StatusNotFound)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			_, _ = io.WriteString(w, body)
		default:
			t.Logf("unexpected mock path: %s", path)
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	t.Cleanup(m.srv.Close)
	return m
}

// jsonScanInt parses a digit-only string into an int. Tiny helper to
// keep test code dependency-free (no fmt.Sscanf in hot paths).
func jsonScanInt(s string, out *int) (int, error) {
	v := 0
	n := 0
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, errBadInt
		}
		v = v*10 + int(r-'0')
		n++
	}
	*out = v
	return n, nil
}

var errBadInt = &mockErr{"not an int"}

type mockErr struct{ s string }

func (e *mockErr) Error() string { return e.s }

// newFDSource wires a FootballDataSource pointed at the mock server
// with the supplied fallback. Logger is a discard logger to keep
// test output clean.
func newFDSource(mock *fdMockServer, next Source) *FootballDataSource {
	src := NewFootballDataSource("test-key", next, slog.New(slog.NewTextHandler(io.Discard, nil)))
	src.baseURL = mock.srv.URL
	return src
}

// canned PL standings with Arsenal (57) and Man City (65).
var plStandingsJSON = mustJSON(map[string]any{
	"competition": map[string]any{"name": "Premier League", "code": "PL"},
	"season":      map[string]any{"startDate": "2025-08-15", "endDate": "2026-05-25"},
	"standings": []any{
		map[string]any{
			"type": "TOTAL",
			"table": []any{
				map[string]any{
					"position":    1,
					"playedGames": 28,
					"won":         21,
					"draw":        4,
					"lost":        3,
					"points":      67,
					"form":        "W,W,W,D,W",
					"team": map[string]any{
						"id":        65,
						"name":      "Manchester City FC",
						"shortName": "Man City",
						"tla":       "MCI",
					},
				},
				map[string]any{
					"position":    2,
					"playedGames": 28,
					"won":         19,
					"draw":        5,
					"lost":        4,
					"points":      62,
					"form":        "WWDWL",
					"team": map[string]any{
						"id":        57,
						"name":      "Arsenal FC",
						"shortName": "Arsenal",
						"tla":       "ARS",
					},
				},
			},
		},
	},
})

func mustJSON(v any) string {
	b, err := json.Marshal(v)
	if err != nil {
		panic(err)
	}
	return string(b)
}

// --- Tests --------------------------------------------------------------

func TestFD_TeamStandings_Hit(t *testing.T) {
	mock := newFDMock(t)
	mock.standingsByCode["PL"] = plStandingsJSON
	next := &fakeNext{}
	src := newFDSource(mock, next)

	out, err := src.TeamStandings(context.Background(), "arsenal")
	if err != nil {
		t.Fatalf("unexpected err: %v", err)
	}
	if out.Source != SourceFootballData {
		t.Errorf("Source: want %q got %q", SourceFootballData, out.Source)
	}
	if out.TeamID != "arsenal" {
		t.Errorf("TeamID: want arsenal got %q", out.TeamID)
	}
	if out.Position != 2 {
		t.Errorf("Position: want 2 got %d", out.Position)
	}
	if out.Points != 62 {
		t.Errorf("Points: want 62 got %d", out.Points)
	}
	if out.League != "Premier League" {
		t.Errorf("League: want %q got %q", "Premier League", out.League)
	}
	if got := strings.Join(out.RecentForm, ""); got != "WWDWL" {
		t.Errorf("RecentForm: want WWDWL got %q", got)
	}
	if next.teamHits != 0 {
		t.Errorf("expected 0 fallback calls, got %d", next.teamHits)
	}
}

func TestFD_TeamStandings_UnmappedDelegates(t *testing.T) {
	mock := newFDMock(t)
	next := &fakeNext{teamReply: api.TeamStandings{TeamID: "mumbai-indians", League: "IPL"}}
	src := newFDSource(mock, next)

	out, err := src.TeamStandings(context.Background(), "mumbai-indians")
	if err != nil {
		t.Fatalf("unexpected err: %v", err)
	}
	if next.teamHits != 1 {
		t.Errorf("expected fallback call, got %d", next.teamHits)
	}
	if out.Source != "fake-next" {
		t.Errorf("Source: want fake-next got %q", out.Source)
	}
	if len(mock.standingsCalls) != 0 {
		t.Errorf("expected 0 mock calls for unmapped team, got %v", mock.standingsCalls)
	}
}

func TestFD_NextMatch_OpponentExtraction(t *testing.T) {
	mock := newFDMock(t)
	// Arsenal (57) is HOME; Man City (65) is AWAY → opponent is Man City.
	mock.matchesByTeam[57] = mustJSON(map[string]any{
		"matches": []any{
			map[string]any{
				"utcDate":     "2026-05-12T19:00:00Z",
				"venue":       "Emirates Stadium",
				"competition": map[string]any{"name": "Premier League", "code": "PL"},
				"homeTeam":    map[string]any{"id": 57, "name": "Arsenal FC"},
				"awayTeam":    map[string]any{"id": 65, "name": "Manchester City FC"},
			},
		},
	})
	next := &fakeNext{}
	src := newFDSource(mock, next)

	out, err := src.NextMatch(context.Background(), "arsenal")
	if err != nil {
		t.Fatalf("unexpected err: %v", err)
	}
	if out.Opponent.ID != "man-city" {
		t.Errorf("Opponent.ID: want man-city got %q", out.Opponent.ID)
	}
	if out.Opponent.Name != "Manchester City FC" {
		t.Errorf("Opponent.Name: want %q got %q", "Manchester City FC", out.Opponent.Name)
	}
	if out.Venue != "Emirates Stadium" {
		t.Errorf("Venue: want Emirates Stadium got %q", out.Venue)
	}
	if out.Competition != "Premier League" {
		t.Errorf("Competition: want %q got %q", "Premier League", out.Competition)
	}
	if out.Source != SourceFootballData {
		t.Errorf("Source: want %q got %q", SourceFootballData, out.Source)
	}
	if next.matchHits != 0 {
		t.Errorf("expected 0 fallback calls, got %d", next.matchHits)
	}

	// Now flip: Man City fetches its own next-match — should resolve
	// opponent as the home side (Arsenal).
	mock.matchesByTeam[65] = mock.matchesByTeam[57]
	out2, err := src.NextMatch(context.Background(), "man-city")
	if err != nil {
		t.Fatalf("unexpected err: %v", err)
	}
	if out2.Opponent.ID != "arsenal" {
		t.Errorf("Opponent.ID for man-city: want arsenal got %q", out2.Opponent.ID)
	}
}

func TestFD_LeagueStandings_FullTable(t *testing.T) {
	mock := newFDMock(t)
	mock.standingsByCode["PL"] = plStandingsJSON
	next := &fakeNext{}
	src := newFDSource(mock, next)

	out, err := src.LeagueStandings(context.Background(), "epl")
	if err != nil {
		t.Fatalf("unexpected err: %v", err)
	}
	if out.Source != SourceFootballData {
		t.Errorf("Source: want %q got %q", SourceFootballData, out.Source)
	}
	if out.LeagueID != "epl" {
		t.Errorf("LeagueID: want epl got %q", out.LeagueID)
	}
	if out.Name != "Premier League" {
		t.Errorf("Name: want Premier League got %q", out.Name)
	}
	if out.Season != "2025/26" {
		t.Errorf("Season: want 2025/26 got %q", out.Season)
	}
	if len(out.Table) != 2 {
		t.Fatalf("expected 2 rows, got %d", len(out.Table))
	}
	row1 := out.Table[0]
	if row1.TeamID != "man-city" {
		t.Errorf("row1.TeamID: want man-city got %q", row1.TeamID)
	}
	if row1.TeamName != "Manchester City FC" {
		t.Errorf("row1.TeamName: want Manchester City FC got %q", row1.TeamName)
	}
	if row1.Position != 1 || row1.Points != 67 || row1.Played != 28 {
		t.Errorf("row1 numbers wrong: %+v", row1)
	}
	row2 := out.Table[1]
	if row2.TeamID != "arsenal" {
		t.Errorf("row2.TeamID: want arsenal got %q", row2.TeamID)
	}
}

func TestFD_403Delegates(t *testing.T) {
	mock := newFDMock(t)
	mock.statusByCode["CL"] = http.StatusForbidden
	mock.standingsByCode["CL"] = `{"error":"tier"}`
	next := &fakeNext{leagueReply: api.LeagueStandings{LeagueID: "ucl", Name: "Champions League"}}
	src := newFDSource(mock, next)

	out, err := src.LeagueStandings(context.Background(), "ucl")
	if err != nil {
		t.Fatalf("unexpected err: %v", err)
	}
	if next.leagueHits != 1 {
		t.Errorf("expected 1 fallback call, got %d", next.leagueHits)
	}
	if out.Source != "fake-next" {
		t.Errorf("Source: want fake-next got %q", out.Source)
	}
}

func TestFD_CacheHitOnRepeat(t *testing.T) {
	mock := newFDMock(t)
	mock.standingsByCode["PL"] = plStandingsJSON
	next := &fakeNext{}
	src := newFDSource(mock, next)

	for i := 0; i < 3; i++ {
		if _, err := src.TeamStandings(context.Background(), "arsenal"); err != nil {
			t.Fatalf("iter %d: %v", i, err)
		}
	}
	if got := mock.standingsCalls["PL"]; got != 1 {
		t.Errorf("expected 1 upstream call (cache hit on repeats), got %d", got)
	}

	// LeagueStandings on the same code should also hit cache.
	if _, err := src.LeagueStandings(context.Background(), "epl"); err != nil {
		t.Fatalf("league: %v", err)
	}
	if got := mock.standingsCalls["PL"]; got != 1 {
		t.Errorf("expected 1 upstream call after league fetch (still cached), got %d", got)
	}

	// Forcing expiry should re-fetch.
	src.mu.Lock()
	for k, v := range src.cache {
		v.expiresAt = time.Now().Add(-1 * time.Minute)
		src.cache[k] = v
	}
	src.mu.Unlock()
	if _, err := src.TeamStandings(context.Background(), "arsenal"); err != nil {
		t.Fatalf("post-expiry: %v", err)
	}
	if got := mock.standingsCalls["PL"]; got != 2 {
		t.Errorf("expected 2 upstream calls after expiry, got %d", got)
	}
}

func TestFD_FormStringParser(t *testing.T) {
	cases := []struct {
		in, want string
	}{
		{"", ""},
		{"WWDWL", "WWDWL"},
		{"W,W,D,W,L", "WWDWL"},
		{"W/W/D/W/L", "WWDWL"},
		{"W W D W L", "WWDWL"},
		{"WWWWWWW", "WWWWW"}, // capped at 5
		{"xWyDzL", "WDL"},
	}
	for _, c := range cases {
		got := strings.Join(formStringToSlice(c.in), "")
		if got != c.want {
			t.Errorf("formStringToSlice(%q) = %q want %q", c.in, got, c.want)
		}
	}
}

func TestFD_LookupTeamByNameDelegates(t *testing.T) {
	mock := newFDMock(t)
	next := &fakeNext{}
	src := newFDSource(mock, next)
	if _, err := src.LookupTeamByName(context.Background(), "Arsenal"); err != ErrNotFound {
		t.Errorf("expected delegated ErrNotFound, got %v", err)
	}
	if next.lookupHits != 1 {
		t.Errorf("expected 1 lookup delegation, got %d", next.lookupHits)
	}
}
