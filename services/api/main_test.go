package main

import (
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/abindlish/streamed-backend/internal/api"
	"github.com/abindlish/streamed-backend/internal/data"
	"github.com/abindlish/streamed-backend/internal/server"
)

// newTestServer boots an in-process server using the stub source. We
// don't exercise OpenAI here — that path requires a real key and is
// covered manually.
func newTestServer(t *testing.T) *httptest.Server {
	t.Helper()
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	srv := server.New(data.NewStubSource(), logger)
	return httptest.NewServer(srv.Handler())
}

func TestHealth(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/health")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	var body api.HealthResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if body.Status != "ok" {
		t.Fatalf("status field = %q, want ok", body.Status)
	}
	if body.Uptime == "" {
		t.Fatalf("uptime is empty")
	}
}

func TestArsenalStandings(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/arsenal/standings")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	var body api.TeamStandings
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if body.TeamID != "arsenal" {
		t.Fatalf("teamId = %q, want arsenal", body.TeamID)
	}
	if body.Source != api.SourceStub {
		t.Fatalf("source = %q, want %q", body.Source, api.SourceStub)
	}
	if body.Played == 0 || body.Points == 0 {
		t.Fatalf("expected non-zero played/points, got %+v", body)
	}
	if len(body.RecentForm) != 5 {
		t.Fatalf("recentForm len = %d, want 5", len(body.RecentForm))
	}
}

func TestUnknownTeamReturns404(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/atlantis-fc/standings")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", resp.StatusCode)
	}
}

func TestLeagueStandings(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/leagues/epl/standings")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	var body api.LeagueStandings
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if !strings.EqualFold(body.LeagueID, "epl") {
		t.Fatalf("leagueId = %q", body.LeagueID)
	}
	if len(body.Table) == 0 {
		t.Fatalf("table is empty")
	}
	if body.Source != api.SourceStub {
		t.Fatalf("source = %q, want stub", body.Source)
	}
}

func TestNextMatch(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/arsenal/next-match")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	var body api.NextMatch
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if body.Opponent.ID == "" || body.Kickoff == "" {
		t.Fatalf("expected populated opponent + kickoff, got %+v", body)
	}
}

func TestCORSHeader(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/health")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	if got := resp.Header.Get("Access-Control-Allow-Origin"); got != "*" {
		t.Fatalf("CORS origin = %q, want *", got)
	}
}

// TestTeamLookup exercises the alias-table path of /v1/teams/lookup.
// OpenAI is disabled in the test setup (no key), so a miss must 404
// rather than fall through to a remote call.
func TestTeamLookup(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	cases := []struct {
		name           string
		query          string
		wantStatus     int
		wantTeamID     string
		wantSource     string
		wantConfidence float64
	}{
		{
			name:           "full name",
			query:          "Manchester+United",
			wantStatus:     http.StatusOK,
			wantTeamID:     "man-united",
			wantSource:     api.SourceAlias,
			wantConfidence: 0.95,
		},
		{
			name:           "common abbreviation",
			query:          "Man+Utd",
			wantStatus:     http.StatusOK,
			wantTeamID:     "man-united",
			wantSource:     api.SourceAlias,
			wantConfidence: 0.95,
		},
		{
			name:           "ipl team",
			query:          "Mumbai+Indians",
			wantStatus:     http.StatusOK,
			wantTeamID:     "mumbai-indians",
			wantSource:     api.SourceAlias,
			wantConfidence: 0.95,
		},
		{
			name:       "unknown team with FC suffix - openai disabled in tests",
			query:      "Made+Up+FC",
			wantStatus: http.StatusNotFound,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			resp, err := http.Get(ts.URL + "/v1/teams/lookup?name=" + tc.query)
			if err != nil {
				t.Fatalf("get: %v", err)
			}
			defer resp.Body.Close()

			if resp.StatusCode != tc.wantStatus {
				body, _ := io.ReadAll(resp.Body)
				t.Fatalf("status = %d, want %d (body=%s)", resp.StatusCode, tc.wantStatus, string(body))
			}
			if tc.wantStatus != http.StatusOK {
				return
			}
			var body api.TeamLookup
			if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
				t.Fatalf("decode: %v", err)
			}
			if body.TeamID != tc.wantTeamID {
				t.Fatalf("teamId = %q, want %q", body.TeamID, tc.wantTeamID)
			}
			if body.Source != tc.wantSource {
				t.Fatalf("source = %q, want %q", body.Source, tc.wantSource)
			}
			if body.Confidence != tc.wantConfidence {
				t.Fatalf("confidence = %v, want %v", body.Confidence, tc.wantConfidence)
			}
			if body.CanonicalName == "" {
				t.Fatalf("canonicalName is empty")
			}
			if body.League == "" {
				t.Fatalf("league is empty")
			}
			if body.Query == "" {
				t.Fatalf("query is empty")
			}
		})
	}
}

// TestTeamLookupMissingName ensures a 400 is returned (not a 404 or
// 500) when the name parameter is absent.
func TestTeamLookupMissingName(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/lookup")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", resp.StatusCode)
	}
}

func TestArsenalLeagues(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/arsenal/leagues")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	var body []api.TeamStandings
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if len(body) < 1 {
		t.Fatalf("expected at least 1 league entry, got %d", len(body))
	}
	if body[0].TeamID != "arsenal" {
		t.Fatalf("teamId = %q, want arsenal", body[0].TeamID)
	}
	if body[0].Source != api.SourceStub {
		t.Fatalf("source = %q, want %q", body[0].Source, api.SourceStub)
	}
	if body[0].League == "" {
		t.Fatalf("league is empty")
	}
}

func TestAllTeamLeagues_UnknownTeam(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	// atlantis-fc is absent from the stub source.
	resp, err := http.Get(ts.URL + "/v1/teams/atlantis-fc/leagues")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", resp.StatusCode)
	}
}

func TestExistingStandingsUnchanged(t *testing.T) {
	ts := newTestServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/arsenal/standings")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	// Guard against the handler accidentally returning []api.TeamStandings
	// instead of api.TeamStandings: the body must be a JSON object, not an
	// array.
	raw, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("read body: %v", err)
	}
	if len(raw) == 0 || raw[0] == '[' {
		t.Fatalf("standings endpoint returned a JSON array; want a single object")
	}
	var body api.TeamStandings
	if err := json.Unmarshal(raw, &body); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if body.TeamID != "arsenal" {
		t.Fatalf("teamId = %q, want arsenal", body.TeamID)
	}
}
