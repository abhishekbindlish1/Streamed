// Package data exposes pluggable sources that produce API payloads.
//
// stub.go provides a deterministic, hardcoded source so the TV client
// can integrate immediately without any external dependencies.
package data

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/abindlish/streamed-backend/internal/api"
)

// Source is the abstraction every data backend implements.
type Source interface {
	TeamStandings(ctx context.Context, teamID string) (api.TeamStandings, error)
	NextMatch(ctx context.Context, teamID string) (api.NextMatch, error)
	LeagueStandings(ctx context.Context, leagueID string) (api.LeagueStandings, error)
	// TeamLeagues returns up to 3 league IDs for the team, ordered by importance.
	// Returns empty slice (not error) when leagues cannot be determined.
	TeamLeagues(ctx context.Context, teamID string) ([]string, error)
	// AllTeamStandings returns standings in all leagues the team participates in.
	// Primary league is always index 0. Up to 3 entries.
	AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error)
	// LookupTeamByName resolves a free-text team name to a canonical
	// api.TeamLookup. Implementations must return ErrNotFound when no
	// resolution is possible. The package-level LookupTeamByName helper
	// always tries the alias table first and only delegates here when
	// the alias table misses, so this method only needs to handle
	// out-of-table names.
	LookupTeamByName(ctx context.Context, name string) (*api.TeamLookup, error)
	Name() string
}

// ErrNotFound is returned when the requested team or league is unknown
// to the source.
var ErrNotFound = fmt.Errorf("not found")

// StubSource returns canned, plausible-looking but NOT real data. It
// always sets Source = "stub" so the consumer knows.
type StubSource struct{}

func NewStubSource() *StubSource { return &StubSource{} }

func (s *StubSource) Name() string { return api.SourceStub }

// canned EPL-flavored standings keyed by lowercase team ID.
var stubStandings = map[string]api.TeamStandings{
	"arsenal": {
		TeamID: "arsenal", League: "Premier League", Position: 2,
		Played: 28, Wins: 19, Draws: 5, Losses: 4, Points: 62,
		RecentForm: []string{"W", "W", "D", "W", "L"},
	},
	"man-city": {
		TeamID: "man-city", League: "Premier League", Position: 1,
		Played: 28, Wins: 21, Draws: 4, Losses: 3, Points: 67,
		RecentForm: []string{"W", "W", "W", "D", "W"},
	},
	"liverpool": {
		TeamID: "liverpool", League: "Premier League", Position: 3,
		Played: 28, Wins: 17, Draws: 7, Losses: 4, Points: 58,
		RecentForm: []string{"D", "W", "W", "L", "W"},
	},
	"chelsea": {
		TeamID: "chelsea", League: "Premier League", Position: 6,
		Played: 28, Wins: 13, Draws: 6, Losses: 9, Points: 45,
		RecentForm: []string{"L", "W", "D", "W", "W"},
	},
	"spurs": {
		TeamID: "spurs", League: "Premier League", Position: 5,
		Played: 28, Wins: 14, Draws: 5, Losses: 9, Points: 47,
		RecentForm: []string{"W", "L", "L", "W", "D"},
	},
}

var stubNextMatch = map[string]api.NextMatch{
	"arsenal": {
		TeamID:      "arsenal",
		Opponent:    api.TeamRef{ID: "man-city", Name: "Manchester City"},
		Competition: "Premier League", Venue: "Emirates Stadium",
	},
	"man-city": {
		TeamID:      "man-city",
		Opponent:    api.TeamRef{ID: "arsenal", Name: "Arsenal"},
		Competition: "Premier League", Venue: "Emirates Stadium",
	},
	"liverpool": {
		TeamID:      "liverpool",
		Opponent:    api.TeamRef{ID: "chelsea", Name: "Chelsea"},
		Competition: "Premier League", Venue: "Anfield",
	},
	"chelsea": {
		TeamID:      "chelsea",
		Opponent:    api.TeamRef{ID: "liverpool", Name: "Liverpool"},
		Competition: "Premier League", Venue: "Anfield",
	},
	"spurs": {
		TeamID:      "spurs",
		Opponent:    api.TeamRef{ID: "arsenal", Name: "Arsenal"},
		Competition: "Premier League", Venue: "Tottenham Hotspur Stadium",
	},
}

var stubLeagues = map[string]api.LeagueStandings{
	"epl": {
		LeagueID: "epl", Name: "Premier League", Season: "2025/26",
		Table: []api.LeagueTableRow{
			{Position: 1, TeamID: "man-city", TeamName: "Manchester City", Played: 28, Wins: 21, Draws: 4, Losses: 3, Points: 67, RecentForm: []string{"W", "W", "W", "D", "W"}},
			{Position: 2, TeamID: "arsenal", TeamName: "Arsenal", Played: 28, Wins: 19, Draws: 5, Losses: 4, Points: 62, RecentForm: []string{"W", "W", "D", "W", "L"}},
			{Position: 3, TeamID: "liverpool", TeamName: "Liverpool", Played: 28, Wins: 17, Draws: 7, Losses: 4, Points: 58, RecentForm: []string{"D", "W", "W", "L", "W"}},
			{Position: 4, TeamID: "aston-villa", TeamName: "Aston Villa", Played: 28, Wins: 15, Draws: 6, Losses: 7, Points: 51, RecentForm: []string{"W", "L", "W", "W", "D"}},
			{Position: 5, TeamID: "spurs", TeamName: "Tottenham Hotspur", Played: 28, Wins: 14, Draws: 5, Losses: 9, Points: 47, RecentForm: []string{"W", "L", "L", "W", "D"}},
			{Position: 6, TeamID: "chelsea", TeamName: "Chelsea", Played: 28, Wins: 13, Draws: 6, Losses: 9, Points: 45, RecentForm: []string{"L", "W", "D", "W", "W"}},
		},
	},
	"laliga": {
		LeagueID: "laliga", Name: "La Liga", Season: "2025/26",
		Table: []api.LeagueTableRow{
			{Position: 1, TeamID: "real-madrid", TeamName: "Real Madrid", Played: 28, Wins: 22, Draws: 4, Losses: 2, Points: 70, RecentForm: []string{"W", "W", "W", "W", "D"}},
			{Position: 2, TeamID: "barcelona", TeamName: "Barcelona", Played: 28, Wins: 20, Draws: 4, Losses: 4, Points: 64, RecentForm: []string{"W", "D", "W", "W", "L"}},
			{Position: 3, TeamID: "atletico-madrid", TeamName: "Atletico Madrid", Played: 28, Wins: 17, Draws: 7, Losses: 4, Points: 58, RecentForm: []string{"D", "W", "W", "D", "W"}},
			{Position: 4, TeamID: "girona", TeamName: "Girona", Played: 28, Wins: 15, Draws: 7, Losses: 6, Points: 52, RecentForm: []string{"W", "L", "D", "W", "W"}},
		},
	},
}

func (s *StubSource) TeamStandings(_ context.Context, teamID string) (api.TeamStandings, error) {
	row, ok := stubStandings[strings.ToLower(teamID)]
	if !ok {
		return api.TeamStandings{}, ErrNotFound
	}
	row.Source = api.SourceStub
	return row, nil
}

func (s *StubSource) NextMatch(_ context.Context, teamID string) (api.NextMatch, error) {
	m, ok := stubNextMatch[strings.ToLower(teamID)]
	if !ok {
		return api.NextMatch{}, ErrNotFound
	}
	// Always present a kickoff in the near future so the TV UI has a
	// usable timestamp.
	m.Kickoff = time.Now().Add(72 * time.Hour).UTC().Format(time.RFC3339)
	m.Source = api.SourceStub
	return m, nil
}

func (s *StubSource) LeagueStandings(_ context.Context, leagueID string) (api.LeagueStandings, error) {
	l, ok := stubLeagues[strings.ToLower(leagueID)]
	if !ok {
		return api.LeagueStandings{}, ErrNotFound
	}
	l.Source = api.SourceStub
	return l, nil
}

// LookupTeamByName on the stub only consults the alias table — it has
// no remote source to fall back to. Returns ErrNotFound on a miss so
// the package-level helper can translate that into a 404. The Source
// stamped on a hit is api.SourceStub so consumers can tell this came
// from the stub path (the package-level helper uses api.SourceAlias for
// alias-table hits before delegating; this branch is only reached if a
// caller invokes the method directly).
func (s *StubSource) LookupTeamByName(_ context.Context, name string) (*api.TeamLookup, error) {
	hit, ok := lookupAlias(name)
	if !ok {
		return nil, ErrNotFound
	}
	hit.Source = api.SourceStub
	return &hit, nil
}

// stubTeamLeagues maps known stub team IDs to their league IDs.
var stubTeamLeagues = map[string][]string{
	"arsenal":   {"epl", "uefa-champions-league"},
	"man-city":  {"epl", "uefa-champions-league"},
	"liverpool": {"epl", "uefa-europa-league"},
	"chelsea":   {"epl"},
	"spurs":     {"epl"},
}

func (s *StubSource) TeamLeagues(_ context.Context, teamID string) ([]string, error) {
	ids, ok := stubTeamLeagues[strings.ToLower(teamID)]
	if !ok {
		return []string{}, nil
	}
	return ids, nil
}

func (s *StubSource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	primary, err := s.TeamStandings(ctx, teamID)
	if err != nil {
		return nil, err
	}
	return []api.TeamStandings{primary}, nil
}
