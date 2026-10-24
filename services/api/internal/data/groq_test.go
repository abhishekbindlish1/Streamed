package data

import (
	"testing"

	"github.com/abindlish/streamed-backend/internal/api"
)

func TestCanonicalizeLeagueID(t *testing.T) {
	cases := []struct {
		in, want string
	}{
		{"", ""},
		{"Premier League", "premier-league"},
		{"UEFA Champions League", "uefa-champions-league"},
		{"champions-league", "champions-league"},
		{"  LaLiga  ", "laliga"},
		{"Serie A", "serie-a"},
		{"Coupe de France", "coupe-de-france"},
		{"UEFA Europa League", "uefa-europa-league"},
		{"a--b", "a-b"},
		{"-leading", "leading"},
		{"trailing-", "trailing"},
		{"Bundesliga!", "bundesliga"},
		{"100 Things", "100-things"},
	}
	for _, c := range cases {
		got := canonicalizeLeagueID(c.in)
		if got != c.want {
			t.Errorf("canonicalizeLeagueID(%q) = %q want %q", c.in, got, c.want)
		}
	}
}

func TestFindTeamRow(t *testing.T) {
	table := []api.LeagueTableRow{
		{TeamID: "arsenal", TeamName: "Arsenal FC", Position: 1, Points: 62},
		{TeamID: "man-city", TeamName: "Manchester City FC", Position: 2, Points: 58},
		{TeamID: "liverpool", TeamName: "Liverpool FC", Position: 3, Points: 55},
	}

	t.Run("exact match", func(t *testing.T) {
		row := findTeamRow(table, "arsenal")
		if row == nil {
			t.Fatal("expected non-nil row, got nil")
		}
		if row.TeamID != "arsenal" {
			t.Errorf("TeamID: want %q got %q", "arsenal", row.TeamID)
		}
		if row.Points != 62 {
			t.Errorf("Points: want 62 got %d", row.Points)
		}
	})

	t.Run("case-insensitive match", func(t *testing.T) {
		row := findTeamRow(table, "Arsenal")
		if row == nil {
			t.Fatal("expected non-nil row for case-insensitive query, got nil")
		}
		if row.TeamID != "arsenal" {
			t.Errorf("TeamID: want %q got %q", "arsenal", row.TeamID)
		}
	})

	t.Run("not found returns nil", func(t *testing.T) {
		row := findTeamRow(table, "chelsea")
		if row != nil {
			t.Errorf("expected nil for unknown team, got %+v", row)
		}
	})

	t.Run("empty table returns nil", func(t *testing.T) {
		row := findTeamRow([]api.LeagueTableRow{}, "arsenal")
		if row != nil {
			t.Errorf("expected nil for empty table, got %+v", row)
		}
	})
}
