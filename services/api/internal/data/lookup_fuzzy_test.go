package data

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"testing"

	"github.com/abindlish/streamed-backend/internal/api"
)

func TestFuzzyMatchTeam(t *testing.T) {
	candidates := []FuzzyCandidate{
		{ID: "man-united", CanonicalName: "Manchester United", League: "Premier League", Aliases: []string{"man utd", "manu"}},
		{ID: "man-city", CanonicalName: "Manchester City", League: "Premier League", Aliases: []string{"man city"}},
		{ID: "real-madrid", CanonicalName: "Real Madrid", League: "La Liga"},
		{ID: "psg", CanonicalName: "Paris Saint-Germain", League: "Ligue 1"},
	}

	tests := []struct {
		name      string
		query     string
		minScore  float64
		wantHit   bool
		wantID    string
		wantOn    string
		minResult float64
	}{
		{
			name:      "exact canonical match",
			query:     "Manchester United",
			minScore:  0.5,
			wantHit:   true,
			wantID:    "man-united",
			wantOn:    "Manchester United",
			minResult: 1.0,
		},
		{
			name:      "common short form",
			query:     "Man Utd",
			minScore:  0.5,
			wantHit:   true,
			wantID:    "man-united",
			minResult: 0.6,
		},
		{
			name:      "almost-canonical form",
			query:     "Manchester Utd",
			minScore:  0.5,
			wantHit:   true,
			wantID:    "man-united",
			minResult: 0.5,
		},
		{
			name:      "typo against canonical",
			query:     "Manchstr Utd",
			minScore:  0.5,
			wantHit:   true,
			wantID:    "man-united",
			minResult: 0.5,
		},
		{
			name:     "garbage below threshold",
			query:    "Random Garbage Name",
			minScore: 0.5,
			wantHit:  false,
		},
		{
			name:     "empty query",
			query:    "",
			minScore: 0.5,
			wantHit:  false,
		},
		{
			name:      "alias hit",
			query:     "man utd",
			minScore:  0.5,
			wantHit:   true,
			wantID:    "man-united",
			wantOn:    "man utd",
			minResult: 0.9,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := FuzzyMatchTeam(tc.query, candidates, tc.minScore)
			if !tc.wantHit {
				if got != nil {
					t.Fatalf("expected nil result, got %+v", got)
				}
				return
			}
			if got == nil {
				t.Fatalf("expected hit, got nil")
			}
			if got.Candidate.ID != tc.wantID {
				t.Errorf("ID = %q, want %q (matchedOn=%q score=%.3f)", got.Candidate.ID, tc.wantID, got.MatchedOn, got.Score)
			}
			if tc.wantOn != "" && got.MatchedOn != tc.wantOn {
				t.Errorf("MatchedOn = %q, want %q", got.MatchedOn, tc.wantOn)
			}
			if got.Score < tc.minResult {
				t.Errorf("Score = %.3f, want >= %.3f", got.Score, tc.minResult)
			}
			if got.Score > 1.0 {
				t.Errorf("Score = %.3f exceeds 1.0", got.Score)
			}
		})
	}
}

func TestFuzzyMatchTeamEmptyCandidates(t *testing.T) {
	if got := FuzzyMatchTeam("anything", nil, 0.5); got != nil {
		t.Errorf("empty candidates should return nil, got %+v", got)
	}
}

func TestFuzzyMatchTeamTieBreakDeterministic(t *testing.T) {
	// Two candidates with identical canonical names → first one wins.
	candidates := []FuzzyCandidate{
		{ID: "first", CanonicalName: "Same Name"},
		{ID: "second", CanonicalName: "Same Name"},
	}
	got := FuzzyMatchTeam("Same Name", candidates, 0.5)
	if got == nil {
		t.Fatal("expected hit")
	}
	if got.Candidate.ID != "first" {
		t.Errorf("expected first candidate to win on tie, got %q", got.Candidate.ID)
	}
}

func TestFuzzyMatchTeamScoreBounds(t *testing.T) {
	// A query that hits both jaccard + containment should not exceed 1.0.
	candidates := []FuzzyCandidate{
		{ID: "x", CanonicalName: "Manchester United"},
	}
	got := FuzzyMatchTeam("Manchester United", candidates, 0.0)
	if got == nil {
		t.Fatal("expected hit")
	}
	if got.Score > 1.0 || got.Score < 0.0 {
		t.Errorf("score out of [0,1]: %.3f", got.Score)
	}
}

// fuzzyTestSource lets the wrapper tests stub LookupTeamByName while
// passing every other Source method straight through to a stub.
type fuzzyTestSource struct {
	StubSource
	lookupResult *api.TeamLookup
	lookupErr    error
	lookupCalls  int
	lastName     string
}

func (s *fuzzyTestSource) LookupTeamByName(_ context.Context, name string) (*api.TeamLookup, error) {
	s.lookupCalls++
	s.lastName = name
	return s.lookupResult, s.lookupErr
}

func newSilentLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(io.Discard, nil))
}

func TestFuzzyLookupSourceInnerHit(t *testing.T) {
	want := &api.TeamLookup{TeamID: "man-united", Source: api.SourceAlias, Confidence: 0.95}
	inner := &fuzzyTestSource{lookupResult: want, lookupErr: nil}
	w := NewFuzzyLookupSource(inner, 0.55, newSilentLogger())

	got, err := w.LookupTeamByName(context.Background(), "Manchester United")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != want {
		t.Errorf("expected inner result to pass through unchanged, got %+v", got)
	}
}

func TestFuzzyLookupSourceFuzzyHitOnNotFound(t *testing.T) {
	inner := &fuzzyTestSource{lookupResult: nil, lookupErr: ErrNotFound}
	w := NewFuzzyLookupSource(inner, 0.55, newSilentLogger())

	got, err := w.LookupTeamByName(context.Background(), "Manchstr Utd")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got == nil {
		t.Fatal("expected fuzzy hit, got nil")
	}
	if got.TeamID != "man-united" {
		t.Errorf("TeamID = %q, want man-united", got.TeamID)
	}
	if got.Source != api.SourceFuzzy {
		t.Errorf("Source = %q, want %q", got.Source, api.SourceFuzzy)
	}
	if got.Confidence < 0.55 {
		t.Errorf("Confidence = %.3f, want >= minScore", got.Confidence)
	}
	if got.Query != "Manchstr Utd" {
		t.Errorf("Query = %q, want %q", got.Query, "Manchstr Utd")
	}
}

func TestFuzzyLookupSourceFuzzyMissOnNotFound(t *testing.T) {
	inner := &fuzzyTestSource{lookupResult: nil, lookupErr: ErrNotFound}
	w := NewFuzzyLookupSource(inner, 0.55, newSilentLogger())

	_, err := w.LookupTeamByName(context.Background(), "Completely Unknown Team Name XYZ")
	if !errors.Is(err, ErrNotFound) {
		t.Errorf("expected ErrNotFound, got %v", err)
	}
}

func TestFuzzyLookupSourceNonNotFoundErrorPropagates(t *testing.T) {
	wantErr := errors.New("upstream blew up")
	inner := &fuzzyTestSource{lookupResult: nil, lookupErr: wantErr}
	w := NewFuzzyLookupSource(inner, 0.55, newSilentLogger())

	_, err := w.LookupTeamByName(context.Background(), "anything")
	if err != wantErr {
		t.Errorf("expected upstream error to propagate, got %v", err)
	}
}

func TestBuildFuzzyCandidatesIncludesAliases(t *testing.T) {
	cands := buildFuzzyCandidates()
	if len(cands) != len(teamCatalog) {
		t.Fatalf("candidate count = %d, want %d", len(cands), len(teamCatalog))
	}
	var manU FuzzyCandidate
	for _, c := range cands {
		if c.ID == "man-united" {
			manU = c
			break
		}
	}
	if manU.ID == "" {
		t.Fatal("man-united not found in candidates")
	}
	hasManUtd := false
	for _, a := range manU.Aliases {
		if a == "man utd" {
			hasManUtd = true
		}
	}
	if !hasManUtd {
		t.Errorf("expected 'man utd' in man-united aliases, got %v", manU.Aliases)
	}
}
