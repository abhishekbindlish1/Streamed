package data

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"
)

// --- parseMatchOutcome unit tests --------------------------------------------

func TestParseMatchOutcome_WonByWickets(t *testing.T) {
	teams := []string{"Mumbai Indians", "Chennai Super Kings"}
	cases := []string{
		"Mumbai Indians won by 5 wkts",
		"Mumbai Indians won by 5 wickets",
		"Mumbai Indians won by 1 wkt",
		"mumbai indians won by 7 wickets",
	}
	for _, status := range cases {
		got := parseMatchOutcome(status, teams)
		if !got.finished {
			t.Errorf("status %q: finished=false, want true", status)
		}
		if !strings.EqualFold(got.winner, "Mumbai Indians") {
			t.Errorf("status %q: winner = %q, want Mumbai Indians", status, got.winner)
		}
		if got.isTie || got.isNoResult {
			t.Errorf("status %q: tie/nr flags wrong: %+v", status, got)
		}
	}
}

func TestParseMatchOutcome_WonByRuns(t *testing.T) {
	teams := []string{"Royal Challengers Bengaluru", "Punjab Kings"}
	cases := []string{
		"Royal Challengers Bengaluru won by 30 runs",
		"Royal Challengers Bengaluru won by 1 run",
		"royal challengers bengaluru won by 12 runs",
		"Royal Challengers Bengaluru Won By 8 Runs",
	}
	for _, status := range cases {
		got := parseMatchOutcome(status, teams)
		if !got.finished || got.winner == "" {
			t.Errorf("status %q: %+v", status, got)
			continue
		}
		if !strings.EqualFold(got.winner, "Royal Challengers Bengaluru") {
			t.Errorf("status %q: winner = %q", status, got.winner)
		}
	}
}

func TestParseMatchOutcome_DLSMethod(t *testing.T) {
	teams := []string{"India", "England"}
	got := parseMatchOutcome("England won by 6 runs (DLS Method)", teams)
	if !got.finished {
		t.Fatalf("finished = false")
	}
	if got.winner != "England" {
		t.Fatalf("winner = %q, want England", got.winner)
	}
	if got.isTie || got.isNoResult {
		t.Fatalf("flags wrong: %+v", got)
	}
}

func TestParseMatchOutcome_TieSuperOver_FullName(t *testing.T) {
	teams := []string{"Kolkata Knight Riders", "Rajasthan Royals"}
	got := parseMatchOutcome("Match tied (Kolkata Knight Riders won the Super Over)", teams)
	if !got.finished {
		t.Fatalf("finished = false")
	}
	if got.winner != "Kolkata Knight Riders" {
		t.Fatalf("winner = %q, want Kolkata Knight Riders", got.winner)
	}
	if got.isTie {
		t.Fatalf("isTie = true, want false (Super Over decided)")
	}
}

func TestParseMatchOutcome_TieSuperOver_ShortAlias(t *testing.T) {
	teams := []string{"Kolkata Knight Riders", "Rajasthan Royals"}
	got := parseMatchOutcome("Match tied (KKR won the Super Over)", teams)
	if !got.finished {
		t.Fatalf("finished = false")
	}
	if got.winner != "Kolkata Knight Riders" {
		t.Fatalf("winner = %q, want Kolkata Knight Riders (KKR alias)", got.winner)
	}
}

func TestParseMatchOutcome_TieSuperOver_UnresolvableFallsBackToTie(t *testing.T) {
	teams := []string{"India", "Pakistan"}
	got := parseMatchOutcome("Match tied (Atlantis won the Super Over)", teams)
	if !got.finished {
		t.Fatalf("finished = false")
	}
	if !got.isTie {
		t.Fatalf("isTie = false, want true when winner unresolvable")
	}
	if got.winner != "" {
		t.Fatalf("winner = %q, want empty", got.winner)
	}
}

func TestParseMatchOutcome_PureTie(t *testing.T) {
	teams := []string{"India", "Pakistan"}
	got := parseMatchOutcome("Match tied", teams)
	if !got.finished || !got.isTie || got.winner != "" {
		t.Fatalf("got %+v", got)
	}
}

func TestParseMatchOutcome_NoResult(t *testing.T) {
	teams := []string{"India", "Pakistan"}
	got := parseMatchOutcome("No result", teams)
	if !got.finished || !got.isNoResult || got.winner != "" || got.isTie {
		t.Fatalf("got %+v", got)
	}
}

func TestParseMatchOutcome_MatchAbandoned(t *testing.T) {
	teams := []string{"India", "Pakistan"}
	got := parseMatchOutcome("Match abandoned due to rain", teams)
	if !got.finished || !got.isNoResult {
		t.Fatalf("got %+v", got)
	}
}

func TestParseMatchOutcome_NotStarted(t *testing.T) {
	teams := []string{"India", "Pakistan"}
	cases := []string{
		"Match starts at 19:30 IST",
		"Match scheduled to begin at 14:00 GMT",
		"Toss not yet held",
	}
	for _, status := range cases {
		got := parseMatchOutcome(status, teams)
		if got.finished {
			t.Errorf("status %q: finished = true, want false", status)
		}
	}
}

func TestParseMatchOutcome_EmptyStatus(t *testing.T) {
	got := parseMatchOutcome("", []string{"A", "B"})
	if got.finished {
		t.Fatalf("empty status should not be finished, got %+v", got)
	}
}

// --- SeriesStandings integration test ---------------------------------------

// fakeSeriesAPI mimics CricAPI's /series_info endpoint.
type fakeSeriesAPI struct {
	server      *httptest.Server
	infoHits    int32
	matchList   []cricMatch
}

func newFakeSeriesAPI() *fakeSeriesAPI {
	f := &fakeSeriesAPI{}
	mux := http.NewServeMux()
	mux.HandleFunc("/series_info", func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&f.infoHits, 1)
		key := r.URL.Query().Get("apikey")
		if key == "" {
			http.Error(w, "missing apikey", http.StatusBadRequest)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(map[string]any{
			"apikey": key,
			"data": map[string]any{
				"matchList": f.matchList,
			},
			"status": "success",
		})
	})
	f.server = httptest.NewServer(mux)
	return f
}

func (f *fakeSeriesAPI) close() { f.server.Close() }

func newSeriesSource(t *testing.T, fake *fakeSeriesAPI) *CricAPISource {
	t.Helper()
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	src := NewCricAPISource("test-key", &recordingNext{}, logger)
	src.setBaseURL(fake.server.URL)
	return src
}

func TestSeriesStandings_FullSimulation(t *testing.T) {
	fake := newFakeSeriesAPI()
	defer fake.close()

	// 6 matches across 4 teams. Designed to exercise wins, losses, a
	// Super-Over decision, a no-result, and confirm form ordering.
	fake.matchList = []cricMatch{
		// 1) MI beats CSK by wickets
		{
			ID: "m1", Name: "Mumbai Indians vs Chennai Super Kings - IPL 2024",
			Status: "Mumbai Indians won by 6 wkts", DateTimeGMT: "2024-04-01T14:00:00Z",
			Teams: []string{"Mumbai Indians", "Chennai Super Kings"},
		},
		// 2) MI beats RCB by runs
		{
			ID: "m2", Name: "Mumbai Indians vs Royal Challengers Bengaluru - IPL 2024",
			Status: "Mumbai Indians won by 12 runs", DateTimeGMT: "2024-04-05T14:00:00Z",
			Teams: []string{"Mumbai Indians", "Royal Challengers Bengaluru"},
		},
		// 3) CSK beats RCB by DLS
		{
			ID: "m3", Name: "Chennai Super Kings vs Royal Challengers Bengaluru - IPL 2024",
			Status: "Chennai Super Kings won by 5 runs (DLS Method)", DateTimeGMT: "2024-04-10T14:00:00Z",
			Teams: []string{"Chennai Super Kings", "Royal Challengers Bengaluru"},
		},
		// 4) KKR vs RR ties, KKR wins Super Over
		{
			ID: "m4", Name: "Kolkata Knight Riders vs Rajasthan Royals - IPL 2024",
			Status: "Match tied (KKR won the Super Over)", DateTimeGMT: "2024-04-12T14:00:00Z",
			Teams: []string{"Kolkata Knight Riders", "Rajasthan Royals"},
		},
		// 5) MI vs KKR no-result (rain)
		{
			ID: "m5", Name: "Mumbai Indians vs Kolkata Knight Riders - IPL 2024",
			Status: "No result", DateTimeGMT: "2024-04-15T14:00:00Z",
			Teams: []string{"Mumbai Indians", "Kolkata Knight Riders"},
		},
		// 6) MI vs CSK upcoming — should be ignored entirely
		{
			ID: "m6", Name: "Mumbai Indians vs Chennai Super Kings - IPL 2024",
			Status: "Match starts at 19:30 IST", DateTimeGMT: "2024-04-20T14:00:00Z",
			Teams: []string{"Mumbai Indians", "Chennai Super Kings"},
		},
	}

	src := newSeriesSource(t, fake)
	standings, err := src.SeriesStandings(context.Background(), "ipl-2024")
	if err != nil {
		t.Fatalf("SeriesStandings: %v", err)
	}

	if standings.LeagueID != "ipl-2024" {
		t.Errorf("LeagueID = %q, want ipl-2024", standings.LeagueID)
	}
	if standings.Source != SourceCricAPI {
		t.Errorf("Source = %q, want cricapi", standings.Source)
	}
	if !strings.Contains(standings.Name, "IPL 2024") {
		t.Errorf("Name = %q, want to contain IPL 2024", standings.Name)
	}

	// Build a name→row map for assertions.
	got := make(map[string]int) // teamName → points
	gotPlayed := make(map[string]int)
	gotWins := make(map[string]int)
	gotLosses := make(map[string]int)
	gotForm := make(map[string][]string)
	for _, r := range standings.Table {
		got[r.TeamName] = r.Points
		gotPlayed[r.TeamName] = r.Played
		gotWins[r.TeamName] = r.Wins
		gotLosses[r.TeamName] = r.Losses
		gotForm[r.TeamName] = r.RecentForm
	}

	// MI: W (vs CSK), W (vs RCB), N (vs KKR) → 2+2+1 = 5 pts, 2W/0L, played 3
	checkRow(t, "Mumbai Indians", got, gotPlayed, gotWins, gotLosses, 5, 3, 2, 0)
	if !reflect.DeepEqual(gotForm["Mumbai Indians"], []string{"W", "W", "N"}) {
		t.Errorf("MI form = %v, want [W W N]", gotForm["Mumbai Indians"])
	}

	// CSK: L (to MI), W (vs RCB DLS) → 2 pts, 1W/1L, played 2
	checkRow(t, "Chennai Super Kings", got, gotPlayed, gotWins, gotLosses, 2, 2, 1, 1)
	if !reflect.DeepEqual(gotForm["Chennai Super Kings"], []string{"L", "W"}) {
		t.Errorf("CSK form = %v, want [L W]", gotForm["Chennai Super Kings"])
	}

	// RCB: L (to MI), L (to CSK) → 0 pts, 0W/2L, played 2
	checkRow(t, "Royal Challengers Bengaluru", got, gotPlayed, gotWins, gotLosses, 0, 2, 0, 2)

	// KKR: W (Super Over over RR), N (vs MI rain) → 3 pts, 1W/0L, played 2
	checkRow(t, "Kolkata Knight Riders", got, gotPlayed, gotWins, gotLosses, 3, 2, 1, 0)
	if !reflect.DeepEqual(gotForm["Kolkata Knight Riders"], []string{"W", "N"}) {
		t.Errorf("KKR form = %v, want [W N]", gotForm["Kolkata Knight Riders"])
	}

	// RR: L (Super Over) → 0 pts, 0W/1L, played 1
	checkRow(t, "Rajasthan Royals", got, gotPlayed, gotWins, gotLosses, 0, 1, 0, 1)

	// Sort: Mumbai (5) > KKR (3) > CSK (2) > RR (0, name R<R) >? Actually
	// RR (0 pts, 0 W) and RCB (0 pts, 0 W) tie on points and wins, so
	// alphabetical kicks in. "Rajasthan Royals" < "Royal Challengers
	// Bengaluru" → RR before RCB.
	wantOrder := []string{
		"Mumbai Indians",
		"Kolkata Knight Riders",
		"Chennai Super Kings",
		"Rajasthan Royals",
		"Royal Challengers Bengaluru",
	}
	if len(standings.Table) != len(wantOrder) {
		t.Fatalf("table size = %d, want %d", len(standings.Table), len(wantOrder))
	}
	for i, want := range wantOrder {
		if standings.Table[i].TeamName != want {
			t.Errorf("position %d = %q, want %q", i+1, standings.Table[i].TeamName, want)
		}
		if standings.Table[i].Position != i+1 {
			t.Errorf("row %d Position = %d, want %d", i, standings.Table[i].Position, i+1)
		}
		if standings.Table[i].Draws != 0 {
			t.Errorf("row %d Draws = %d, want 0", i, standings.Table[i].Draws)
		}
	}

	// TeamID populated for known canonical names.
	for _, r := range standings.Table {
		switch r.TeamName {
		case "Mumbai Indians":
			if r.TeamID != "mumbai-indians" {
				t.Errorf("MI TeamID = %q", r.TeamID)
			}
		case "Kolkata Knight Riders":
			if r.TeamID != "kolkata-knight-riders" {
				t.Errorf("KKR TeamID = %q", r.TeamID)
			}
		}
	}
}

func TestSeriesStandings_NoFinishedMatchesEmpty(t *testing.T) {
	fake := newFakeSeriesAPI()
	defer fake.close()
	fake.matchList = []cricMatch{
		{
			ID: "u1", Name: "India vs Pakistan - Asia Cup 2026",
			Status: "Match starts at 19:30 IST",
			Teams:  []string{"India", "Pakistan"},
		},
	}
	src := newSeriesSource(t, fake)

	standings, err := src.SeriesStandings(context.Background(), "asia-cup-2026")
	if err != nil {
		t.Fatalf("SeriesStandings: %v", err)
	}
	if len(standings.Table) != 0 {
		t.Fatalf("table = %v, want empty", standings.Table)
	}
	if standings.Source != SourceCricAPI {
		t.Fatalf("Source = %q", standings.Source)
	}
	if standings.LeagueID != "asia-cup-2026" {
		t.Fatalf("LeagueID = %q", standings.LeagueID)
	}
}

func TestSeriesStandings_MalformedTeamsSkipped(t *testing.T) {
	fake := newFakeSeriesAPI()
	defer fake.close()
	fake.matchList = []cricMatch{
		{
			ID: "bad", Name: "Lone Team - IPL 2024",
			Status:      "Mumbai Indians won by 5 wkts",
			DateTimeGMT: "2024-04-01T14:00:00Z",
			Teams:       []string{"Mumbai Indians"}, // only one team — must skip
		},
		{
			ID: "good", Name: "Mumbai Indians vs CSK - IPL 2024",
			Status:      "Mumbai Indians won by 3 wkts",
			DateTimeGMT: "2024-04-02T14:00:00Z",
			Teams:       []string{"Mumbai Indians", "Chennai Super Kings"},
		},
	}
	src := newSeriesSource(t, fake)

	standings, err := src.SeriesStandings(context.Background(), "ipl")
	if err != nil {
		t.Fatalf("SeriesStandings: %v", err)
	}
	// Only the well-formed match should produce rows: 2 teams.
	if len(standings.Table) != 2 {
		t.Fatalf("table size = %d, want 2 (malformed match must be skipped)", len(standings.Table))
	}
}

func TestSeriesStandings_CachedAcrossCalls(t *testing.T) {
	fake := newFakeSeriesAPI()
	defer fake.close()
	fake.matchList = []cricMatch{
		{
			ID: "c1", Name: "Mumbai Indians vs CSK - IPL 2024",
			Status:      "Mumbai Indians won by 3 wkts",
			DateTimeGMT: "2024-04-02T14:00:00Z",
			Teams:       []string{"Mumbai Indians", "Chennai Super Kings"},
		},
	}
	src := newSeriesSource(t, fake)

	for i := 0; i < 3; i++ {
		if _, err := src.SeriesStandings(context.Background(), "ipl-cache"); err != nil {
			t.Fatalf("call %d: %v", i, err)
		}
	}
	// SeriesInfo also caches, so first call → 1 hit, subsequent calls →
	// hit the standings cache directly. Either way, only 1 server hit.
	if got := atomic.LoadInt32(&fake.infoHits); got != 1 {
		t.Fatalf("series_info hits = %d, want 1", got)
	}
}

// checkRow asserts the four scalar tally fields in one place to keep
// failure output focused.
func checkRow(t *testing.T, team string, points, played, wins, losses map[string]int, wantPts, wantPlayed, wantWins, wantLosses int) {
	t.Helper()
	if points[team] != wantPts {
		t.Errorf("%s points = %d, want %d", team, points[team], wantPts)
	}
	if played[team] != wantPlayed {
		t.Errorf("%s played = %d, want %d", team, played[team], wantPlayed)
	}
	if wins[team] != wantWins {
		t.Errorf("%s wins = %d, want %d", team, wins[team], wantWins)
	}
	if losses[team] != wantLosses {
		t.Errorf("%s losses = %d, want %d", team, losses[team], wantLosses)
	}
}
