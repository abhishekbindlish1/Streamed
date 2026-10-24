package data

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	"github.com/abindlish/streamed-backend/internal/api"
)

// recordingNext is a minimal Source that lets tests assert the
// CricAPISource delegates correctly. Each method records the last
// teamID/leagueID it saw and bumps a counter.
type recordingNext struct {
	standingsCalls   int32
	nextMatchCalls   int32
	leagueCalls      int32
	lookupCalls      int32
	lastStandingsID  string
	lastNextMatchID  string
	lastLeagueID     string
	lastLookupName   string
	standingsErr     error
	nextMatchErr     error
	leagueErr        error
	lookupErr        error
	standingsResult  api.TeamStandings
	nextMatchResult  api.NextMatch
	leagueResult     api.LeagueStandings
	lookupResult     *api.TeamLookup
}

func (r *recordingNext) Name() string { return "recording" }
func (r *recordingNext) TeamStandings(_ context.Context, teamID string) (api.TeamStandings, error) {
	atomic.AddInt32(&r.standingsCalls, 1)
	r.lastStandingsID = teamID
	return r.standingsResult, r.standingsErr
}
func (r *recordingNext) NextMatch(_ context.Context, teamID string) (api.NextMatch, error) {
	atomic.AddInt32(&r.nextMatchCalls, 1)
	r.lastNextMatchID = teamID
	return r.nextMatchResult, r.nextMatchErr
}
func (r *recordingNext) LeagueStandings(_ context.Context, leagueID string) (api.LeagueStandings, error) {
	atomic.AddInt32(&r.leagueCalls, 1)
	r.lastLeagueID = leagueID
	return r.leagueResult, r.leagueErr
}
func (r *recordingNext) LookupTeamByName(_ context.Context, name string) (*api.TeamLookup, error) {
	atomic.AddInt32(&r.lookupCalls, 1)
	r.lastLookupName = name
	return r.lookupResult, r.lookupErr
}
func (r *recordingNext) TeamLeagues(_ context.Context, _ string) ([]string, error) {
	return []string{}, nil
}
func (r *recordingNext) AllTeamStandings(_ context.Context, _ string) ([]api.TeamStandings, error) {
	return nil, nil
}

// fakeCricAPI spins up an httptest.Server that mimics the CricAPI v1
// surface for /currentMatches. hitCounter records how many times the
// endpoint was actually called so cache tests can assert hit/miss.
type fakeCricAPI struct {
	server      *httptest.Server
	currentHits int32
	// matches is the data array returned for /currentMatches. Override
	// per-test before issuing requests.
	matches []cricMatch
	// failure forces a status="failure" envelope on /currentMatches.
	failure bool
}

func newFakeCricAPI() *fakeCricAPI {
	f := &fakeCricAPI{}
	mux := http.NewServeMux()
	mux.HandleFunc("/currentMatches", func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&f.currentHits, 1)
		key := r.URL.Query().Get("apikey")
		if key == "" {
			http.Error(w, "missing apikey", http.StatusBadRequest)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		if f.failure {
			_ = json.NewEncoder(w).Encode(map[string]any{
				"apikey": key,
				"data":   nil,
				"status": "failure",
				"reason": "invalid api key",
			})
			return
		}
		_ = json.NewEncoder(w).Encode(map[string]any{
			"apikey": key,
			"data":   f.matches,
			"status": "success",
			"info":   map[string]any{"hitsToday": 1},
		})
	})
	f.server = httptest.NewServer(mux)
	return f
}

func (f *fakeCricAPI) close() { f.server.Close() }

// newCricSourceWithFake wires a CricAPISource pointed at a fake CricAPI
// server, with the given recording next-source. Logger discards.
func newCricSourceWithFake(fake *fakeCricAPI, next Source) *CricAPISource {
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	src := NewCricAPISource("test-key", next, logger)
	src.setBaseURL(fake.server.URL)
	return src
}

func TestCricAPI_Name(t *testing.T) {
	src := NewCricAPISource("k", &recordingNext{}, nil)
	if src.Name() != "cricapi" {
		t.Fatalf("Name() = %q, want cricapi", src.Name())
	}
}

func TestCricAPI_NextMatch_FootballTeamFallsThrough(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	next := &recordingNext{nextMatchResult: api.NextMatch{TeamID: "arsenal", Source: "stub"}}
	src := newCricSourceWithFake(fake, next)

	out, err := src.NextMatch(context.Background(), "arsenal")
	if err != nil {
		t.Fatalf("NextMatch: %v", err)
	}
	if next.nextMatchCalls != 1 {
		t.Fatalf("expected delegation to next, calls=%d", next.nextMatchCalls)
	}
	if atomic.LoadInt32(&fake.currentHits) != 0 {
		t.Fatalf("football team should not hit cricapi; hits=%d", fake.currentHits)
	}
	if out.TeamID != "arsenal" {
		t.Fatalf("teamID = %q, want arsenal", out.TeamID)
	}
}

func TestCricAPI_NextMatch_HitExtractsOpponent(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	fake.matches = []cricMatch{
		{
			ID:          "m-1",
			Name:        "Mumbai Indians vs Chennai Super Kings, 12th Match - IPL 2024",
			Status:      "Match scheduled to begin at 19:30 IST",
			Venue:       "Wankhede Stadium, Mumbai",
			Date:        "2024-04-15",
			DateTimeGMT: "2024-04-15T14:00:00Z",
			Teams:       []string{"Mumbai Indians", "Chennai Super Kings"},
			SeriesID:    "ipl-2024",
		},
	}
	next := &recordingNext{nextMatchErr: ErrNotFound}
	src := newCricSourceWithFake(fake, next)

	out, err := src.NextMatch(context.Background(), "mumbai-indians")
	if err != nil {
		t.Fatalf("NextMatch: %v", err)
	}
	if out.Source != SourceCricAPI {
		t.Fatalf("source = %q, want cricapi", out.Source)
	}
	if out.TeamID != "mumbai-indians" {
		t.Fatalf("teamID = %q, want mumbai-indians", out.TeamID)
	}
	if out.Opponent.Name != "Chennai Super Kings" {
		t.Fatalf("opponent name = %q, want Chennai Super Kings", out.Opponent.Name)
	}
	if out.Opponent.ID != "chennai-super-kings" {
		t.Fatalf("opponent id = %q, want chennai-super-kings", out.Opponent.ID)
	}
	if !strings.Contains(out.Competition, "IPL 2024") {
		t.Fatalf("competition = %q, want to contain IPL 2024", out.Competition)
	}
	if out.Venue != "Wankhede Stadium, Mumbai" {
		t.Fatalf("venue = %q", out.Venue)
	}
	if out.Kickoff != "2024-04-15T14:00:00Z" {
		t.Fatalf("kickoff = %q", out.Kickoff)
	}
	if next.nextMatchCalls != 0 {
		t.Fatalf("should not delegate on hit; calls=%d", next.nextMatchCalls)
	}
}

func TestCricAPI_NextMatch_ShortAliasResolves(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	fake.matches = []cricMatch{
		{
			ID:          "m-2",
			Name:        "Royal Challengers Bengaluru vs Kolkata Knight Riders, Eliminator - IPL 2024",
			Status:      "Match scheduled to begin at 19:30 IST",
			Venue:       "Chinnaswamy",
			DateTimeGMT: "2024-05-22T14:00:00Z",
			Teams:       []string{"Royal Challengers Bengaluru", "Kolkata Knight Riders"},
		},
	}
	next := &recordingNext{nextMatchErr: ErrNotFound}
	src := newCricSourceWithFake(fake, next)

	out, err := src.NextMatch(context.Background(), "rcb")
	if err != nil {
		t.Fatalf("NextMatch: %v", err)
	}
	if out.Source != SourceCricAPI {
		t.Fatalf("source = %q", out.Source)
	}
	// TeamID echoes caller input.
	if out.TeamID != "rcb" {
		t.Fatalf("teamID = %q, want rcb", out.TeamID)
	}
	if out.Opponent.ID != "kolkata-knight-riders" {
		t.Fatalf("opponent id = %q", out.Opponent.ID)
	}
}

func TestCricAPI_NextMatch_NoMatchDelegates(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	fake.matches = []cricMatch{
		{
			ID:          "m-3",
			Name:        "England vs Australia, 1st Test - The Ashes 2025",
			Status:      "Match scheduled",
			Venue:       "Lord's",
			DateTimeGMT: "2025-06-18T10:00:00Z",
			Teams:       []string{"England", "Australia"},
		},
	}
	next := &recordingNext{nextMatchResult: api.NextMatch{TeamID: "india", Source: "stub"}}
	src := newCricSourceWithFake(fake, next)

	_, err := src.NextMatch(context.Background(), "india")
	if err != nil {
		t.Fatalf("NextMatch: %v", err)
	}
	if next.nextMatchCalls != 1 {
		t.Fatalf("expected delegation when team not in any match; calls=%d", next.nextMatchCalls)
	}
}

func TestCricAPI_NextMatch_FinishedMatchSkipped(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	fake.matches = []cricMatch{
		{
			ID:          "done",
			Name:        "Mumbai Indians vs Chennai Super Kings, 5th Match - IPL 2024",
			Status:      "Mumbai Indians won by 5 wickets",
			Venue:       "Wankhede",
			DateTimeGMT: "2024-04-01T14:00:00Z",
			Teams:       []string{"Mumbai Indians", "Chennai Super Kings"},
		},
	}
	next := &recordingNext{nextMatchResult: api.NextMatch{TeamID: "mumbai-indians", Source: "stub"}}
	src := newCricSourceWithFake(fake, next)

	_, err := src.NextMatch(context.Background(), "mumbai-indians")
	if err != nil {
		t.Fatalf("NextMatch: %v", err)
	}
	if next.nextMatchCalls != 1 {
		t.Fatalf("finished match should be skipped → delegate; calls=%d", next.nextMatchCalls)
	}
}

func TestCricAPI_TeamStandings_AlwaysDelegates(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	want := api.TeamStandings{TeamID: "mumbai-indians", League: "IPL", Source: "stub"}
	next := &recordingNext{standingsResult: want, standingsErr: ErrNotFound}
	src := newCricSourceWithFake(fake, next)

	out, err := src.TeamStandings(context.Background(), "mumbai-indians")
	if err != ErrNotFound {
		t.Fatalf("err = %v, want ErrNotFound", err)
	}
	if next.standingsCalls != 1 {
		t.Fatalf("expected one delegation; calls=%d", next.standingsCalls)
	}
	if out.TeamID != want.TeamID {
		t.Fatalf("teamID = %q", out.TeamID)
	}
	if atomic.LoadInt32(&fake.currentHits) != 0 {
		t.Fatalf("standings must never call cricapi; hits=%d", fake.currentHits)
	}
}

func TestCricAPI_LeagueStandings_AlwaysDelegates(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	next := &recordingNext{leagueErr: ErrNotFound}
	src := newCricSourceWithFake(fake, next)

	_, err := src.LeagueStandings(context.Background(), "ipl")
	if err != ErrNotFound {
		t.Fatalf("err = %v, want ErrNotFound", err)
	}
	if next.leagueCalls != 1 {
		t.Fatalf("expected one delegation; calls=%d", next.leagueCalls)
	}
}

func TestCricAPI_CurrentMatchesCacheHit(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	fake.matches = []cricMatch{
		{
			ID:          "m-cache",
			Name:        "India vs Pakistan, Asia Cup Final - Asia Cup 2024",
			Status:      "Match scheduled",
			Venue:       "Colombo",
			DateTimeGMT: "2024-09-20T09:00:00Z",
			Teams:       []string{"India", "Pakistan"},
		},
	}
	next := &recordingNext{nextMatchErr: ErrNotFound}
	src := newCricSourceWithFake(fake, next)

	for i := 0; i < 3; i++ {
		_, err := src.NextMatch(context.Background(), "india")
		if err != nil {
			t.Fatalf("call %d: %v", i, err)
		}
	}
	// First call populates cache; per-team result is also cached so
	// subsequent calls for "india" should not re-hit /currentMatches at
	// all. We expect exactly 1 hit total.
	if got := atomic.LoadInt32(&fake.currentHits); got != 1 {
		t.Fatalf("currentMatches hits = %d, want 1 (cache must short-circuit)", got)
	}
}

func TestCricAPI_CurrentMatchesCachedAcrossTeams(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	fake.matches = []cricMatch{
		{
			ID:          "m-a",
			Name:        "India vs Australia, 1st ODI - Bilateral 2024",
			Status:      "Match scheduled",
			Venue:       "Mumbai",
			DateTimeGMT: "2024-10-01T09:00:00Z",
			Teams:       []string{"India", "Australia"},
		},
	}
	next := &recordingNext{nextMatchErr: ErrNotFound}
	src := newCricSourceWithFake(fake, next)

	if _, err := src.NextMatch(context.Background(), "india"); err != nil {
		t.Fatalf("india: %v", err)
	}
	if _, err := src.NextMatch(context.Background(), "australia"); err != nil {
		t.Fatalf("australia: %v", err)
	}
	// Only one /currentMatches hit even across two distinct teams (the
	// list is shared cache).
	if got := atomic.LoadInt32(&fake.currentHits); got != 1 {
		t.Fatalf("currentMatches hits = %d, want 1", got)
	}
}

func TestCricAPI_FailureEnvelopeDelegates(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	fake.failure = true
	next := &recordingNext{nextMatchResult: api.NextMatch{TeamID: "india", Source: "stub"}}
	src := newCricSourceWithFake(fake, next)

	out, err := src.NextMatch(context.Background(), "india")
	if err != nil {
		t.Fatalf("NextMatch: %v", err)
	}
	if next.nextMatchCalls != 1 {
		t.Fatalf("failure envelope should delegate; calls=%d", next.nextMatchCalls)
	}
	if out.Source != "stub" {
		t.Fatalf("source = %q, want stub from delegate", out.Source)
	}
}

func TestCricAPI_LookupTeamByName_CanonicalMap(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	next := &recordingNext{lookupErr: ErrNotFound}
	src := newCricSourceWithFake(fake, next)

	got, err := src.LookupTeamByName(context.Background(), "Mumbai Indians")
	if err != nil {
		t.Fatalf("Lookup: %v", err)
	}
	if got == nil {
		t.Fatalf("expected non-nil lookup")
	}
	if got.TeamID != "mumbai-indians" {
		t.Fatalf("teamID = %q", got.TeamID)
	}
	if got.Source != SourceCricAPI {
		t.Fatalf("source = %q", got.Source)
	}
	if got.Confidence < 0.8 {
		t.Fatalf("confidence = %v, want >= 0.8 for canonical hit", got.Confidence)
	}
	if atomic.LoadInt32(&fake.currentHits) != 0 {
		t.Fatalf("canonical-map hit must not hit network; hits=%d", fake.currentHits)
	}
}

func TestCricAPI_LookupTeamByName_SubstringScan(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	fake.matches = []cricMatch{
		{
			ID:          "m-sub",
			Name:        "Sunrisers Hyderabad vs Punjab Kings - IPL 2024",
			Status:      "Match scheduled",
			Venue:       "Hyderabad",
			DateTimeGMT: "2024-04-20T14:00:00Z",
			Teams:       []string{"Sunrisers Hyderabad", "Punjab Kings"},
		},
	}
	next := &recordingNext{lookupErr: ErrNotFound}
	src := newCricSourceWithFake(fake, next)

	got, err := src.LookupTeamByName(context.Background(), "Sunrisers")
	if err != nil {
		t.Fatalf("Lookup: %v", err)
	}
	if got == nil {
		t.Fatalf("expected substring match, got nil")
	}
	if got.CanonicalName != "Sunrisers Hyderabad" {
		t.Fatalf("canonicalName = %q", got.CanonicalName)
	}
	if got.TeamID != "sunrisers-hyderabad" {
		t.Fatalf("teamID = %q, want sunrisers-hyderabad", got.TeamID)
	}
	if got.Source != SourceCricAPI {
		t.Fatalf("source = %q", got.Source)
	}
	if got.Confidence != 0.6 {
		t.Fatalf("confidence = %v, want 0.6", got.Confidence)
	}
}

func TestCricAPI_LookupTeamByName_MissReturnsErrNotFound(t *testing.T) {
	fake := newFakeCricAPI()
	defer fake.close()
	fake.matches = []cricMatch{
		{
			ID:          "m-x",
			Name:        "India vs Pakistan, Test - 2025",
			Status:      "Match scheduled",
			Venue:       "Karachi",
			DateTimeGMT: "2025-02-01T05:00:00Z",
			Teams:       []string{"India", "Pakistan"},
		},
	}
	next := &recordingNext{lookupErr: ErrNotFound}
	src := newCricSourceWithFake(fake, next)

	got, err := src.LookupTeamByName(context.Background(), "Atlantis FC")
	if err != ErrNotFound {
		t.Fatalf("err = %v (got=%+v), want ErrNotFound", err, got)
	}
	if got != nil {
		t.Fatalf("expected nil lookup, got %+v", got)
	}
}

// Sanity check the canonical map is not silently empty after some
// future refactor — guard against typos.
func TestCricAPI_CanonicalMapCoverage(t *testing.T) {
	required := []string{"mumbai-indians", "chennai-super-kings", "royal-challengers-bangalore", "kolkata-knight-riders", "delhi-capitals", "india", "australia", "england", "pakistan"}
	for _, id := range required {
		if _, ok := canonicalToCricketName[id]; !ok {
			t.Fatalf("canonicalToCricketName missing %q", id)
		}
		if _, ok := teamCatalog[id]; !ok {
			t.Fatalf("teamCatalog missing %q (referenced by canonicalToCricketName)", id)
		}
	}
}

// fmt is used implicitly via the t.Fatalf calls; this small no-op
// reference keeps gofmt+goimports happy if any helper above is removed
// in the future.
var _ = fmt.Sprintf

// Ensure the recordingNext satisfies Source at compile time.
var _ Source = (*recordingNext)(nil)
