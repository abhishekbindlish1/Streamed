// Package data, lookup_fuzzy.go: fuzzy team-name fallback.
//
// Sits between the alias table miss and the final 404. Scores the
// incoming query against the canonical teamCatalog using a blend of
// token Jaccard, substring containment, and character-bigram overlap.
package data

import (
	"context"
	"log/slog"
	"strings"

	"github.com/abindlish/streamed-backend/internal/api"
)

// FuzzyCandidate is one entry in the candidate list passed to FuzzyMatchTeam.
type FuzzyCandidate struct {
	ID            string
	CanonicalName string
	League        string
	Aliases       []string
}

// FuzzyMatchResult is the return from FuzzyMatchTeam on a hit.
type FuzzyMatchResult struct {
	Candidate FuzzyCandidate
	Score     float64
	MatchedOn string
}

// FuzzyMatchTeam scores `query` against every candidate (and each
// candidate's aliases). Returns the best match if its score >= minScore,
// else nil. Empty query or empty candidates → nil.
func FuzzyMatchTeam(query string, candidates []FuzzyCandidate, minScore float64) *FuzzyMatchResult {
	q := strings.TrimSpace(query)
	if q == "" || len(candidates) == 0 {
		return nil
	}
	qNorm := normalizeTeamName(q)
	if qNorm == "" {
		return nil
	}
	qTokens := tokenize(qNorm)
	qBigrams := bigrams(qNorm)
	shortQuery := len(qTokens) < 3

	var best *FuzzyMatchResult
	for _, c := range candidates {
		names := make([]string, 0, 1+len(c.Aliases))
		names = append(names, c.CanonicalName)
		names = append(names, c.Aliases...)
		for _, name := range names {
			score := scoreNames(qNorm, qTokens, qBigrams, name, shortQuery)
			if best == nil || score > best.Score {
				best = &FuzzyMatchResult{
					Candidate: c,
					Score:     score,
					MatchedOn: name,
				}
			}
		}
	}
	if best == nil || best.Score < minScore {
		return nil
	}
	return best
}

// scoreNames produces a 0..1 score for one (query, name) pair. The
// query side is precomputed (tokens + bigrams) to keep the per-name
// hot loop tight.
func scoreNames(qNorm string, qTokens []string, qBigrams map[string]struct{}, name string, shortQuery bool) float64 {
	nNorm := normalizeTeamName(name)
	if nNorm == "" {
		return 0
	}
	if nNorm == qNorm {
		return 1.0
	}
	nTokens := tokenize(nNorm)

	jaccard := tokenJaccard(qTokens, nTokens)
	containment := 0.0
	if strings.Contains(nNorm, qNorm) || strings.Contains(qNorm, nNorm) {
		containment = 0.2
	}
	tokenScore := jaccard + containment
	if tokenScore > 1.0 {
		tokenScore = 1.0
	}

	bigramScore := 0.0
	if shortQuery {
		bigramScore = bigramOverlap(qBigrams, bigrams(nNorm))
	}

	if bigramScore > tokenScore {
		return bigramScore
	}
	return tokenScore
}

// tokenize splits an already-normalized name on whitespace. Empty
// input → empty slice.
func tokenize(s string) []string {
	if s == "" {
		return nil
	}
	return strings.Fields(s)
}

// tokenJaccard returns |A ∩ B| / |A ∪ B| over the two token slices.
func tokenJaccard(a, b []string) float64 {
	if len(a) == 0 || len(b) == 0 {
		return 0
	}
	setA := make(map[string]struct{}, len(a))
	for _, t := range a {
		setA[t] = struct{}{}
	}
	setB := make(map[string]struct{}, len(b))
	for _, t := range b {
		setB[t] = struct{}{}
	}
	inter := 0
	for t := range setA {
		if _, ok := setB[t]; ok {
			inter++
		}
	}
	union := len(setA) + len(setB) - inter
	if union == 0 {
		return 0
	}
	return float64(inter) / float64(union)
}

// bigrams returns the set of length-2 character substrings of s. Spaces
// are kept so multi-token names produce distinctive cross-token bigrams.
func bigrams(s string) map[string]struct{} {
	if len(s) < 2 {
		return nil
	}
	out := make(map[string]struct{}, len(s)-1)
	r := []rune(s)
	for i := 0; i < len(r)-1; i++ {
		out[string(r[i:i+2])] = struct{}{}
	}
	return out
}

// bigramOverlap is the Sorensen-Dice coefficient over the two bigram
// sets: 2|A ∩ B| / (|A| + |B|). Dice (vs raw |inter|/max) gives higher
// scores for short typo'd queries — which is the failure mode this
// fallback is designed to catch.
func bigramOverlap(a, b map[string]struct{}) float64 {
	if len(a) == 0 || len(b) == 0 {
		return 0
	}
	inter := 0
	for k := range a {
		if _, ok := b[k]; ok {
			inter++
		}
	}
	return 2 * float64(inter) / float64(len(a)+len(b))
}

// FuzzyLookupSource wraps an inner Source and falls back to a fuzzy
// match against teamCatalog when the inner LookupTeamByName returns
// ErrNotFound. All other Source methods pass through unchanged.
type FuzzyLookupSource struct {
	inner      Source
	minScore   float64
	logger     *slog.Logger
	candidates []FuzzyCandidate
}

// NewFuzzyLookupSource builds a wrapper around `inner`. minScore is the
// minimum score required to count as a match (0.55 is a reasonable
// default for the lookup flow).
func NewFuzzyLookupSource(inner Source, minScore float64, logger *slog.Logger) *FuzzyLookupSource {
	if logger == nil {
		logger = slog.Default()
	}
	if inner == nil {
		inner = NewStubSource()
	}
	return &FuzzyLookupSource{
		inner:      inner,
		minScore:   minScore,
		logger:     logger,
		candidates: buildFuzzyCandidates(),
	}
}

// buildFuzzyCandidates flattens the package-level teamCatalog into the
// candidate list, attaching every alias whose value matches the
// catalog id.
func buildFuzzyCandidates() []FuzzyCandidate {
	aliasesByID := make(map[string][]string, len(teamCatalog))
	for alias, id := range teamAliases {
		aliasesByID[id] = append(aliasesByID[id], alias)
	}
	out := make([]FuzzyCandidate, 0, len(teamCatalog))
	for id, meta := range teamCatalog {
		out = append(out, FuzzyCandidate{
			ID:            id,
			CanonicalName: meta.CanonicalName,
			League:        meta.League,
			Aliases:       aliasesByID[id],
		})
	}
	return out
}

func (f *FuzzyLookupSource) Name() string { return f.inner.Name() }

func (f *FuzzyLookupSource) TeamStandings(ctx context.Context, teamID string) (api.TeamStandings, error) {
	return f.inner.TeamStandings(ctx, teamID)
}

func (f *FuzzyLookupSource) NextMatch(ctx context.Context, teamID string) (api.NextMatch, error) {
	return f.inner.NextMatch(ctx, teamID)
}

func (f *FuzzyLookupSource) LeagueStandings(ctx context.Context, leagueID string) (api.LeagueStandings, error) {
	return f.inner.LeagueStandings(ctx, leagueID)
}

func (f *FuzzyLookupSource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	return f.inner.TeamLeagues(ctx, teamID)
}

func (f *FuzzyLookupSource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	return f.inner.AllTeamStandings(ctx, teamID)
}

// LookupTeamByName tries inner first; on ErrNotFound, scores the query
// against teamCatalog and returns a fuzzy hit when one clears minScore.
func (f *FuzzyLookupSource) LookupTeamByName(ctx context.Context, name string) (*api.TeamLookup, error) {
	out, err := f.inner.LookupTeamByName(ctx, name)
	if err == nil {
		return out, nil
	}
	if err != ErrNotFound {
		return nil, err
	}
	hit := FuzzyMatchTeam(name, f.candidates, f.minScore)
	if hit == nil {
		return nil, ErrNotFound
	}
	f.logger.Info("fuzzy lookup hit",
		"query", name,
		"teamId", hit.Candidate.ID,
		"matchedOn", hit.MatchedOn,
		"score", hit.Score,
	)
	return &api.TeamLookup{
		Query:         strings.TrimSpace(name),
		TeamID:        hit.Candidate.ID,
		CanonicalName: hit.Candidate.CanonicalName,
		League:        hit.Candidate.League,
		Confidence:    hit.Score,
		Source:        api.SourceFuzzy,
	}, nil
}
