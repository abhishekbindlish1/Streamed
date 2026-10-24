// Package api defines the JSON response shapes returned by the public HTTP API.
//
// These types are the contract between this backend and any consumer
// (today: the Streamed TV Tizen app). Keep field names stable; the TV
// client serializes against them.
package api

// Source identifies where a payload's data came from. The TV client
// uses this to badge or warn the user about data quality.
const (
	SourceStub   = "stub"
	SourceOpenAI = "openai-llm-summary"
	// SourceAlias indicates the payload was resolved by an exact match
	// against the in-process alias table (no external call).
	SourceAlias = "alias"
	// SourceOpenAILookup is the source value stamped on team-lookup
	// responses fabricated by the OpenAI fallback. It is distinct from
	// SourceOpenAI so the TV client can differentiate "estimated stats"
	// from "best-effort name resolution".
	SourceOpenAILookup = "openai"
	// SourceGroq identifies stats fetched via the 2-step Groq pipeline
	// (compound-mini web search → llama JSON formatting).
	SourceGroq = "groq-live"
	// SourceFirestoreCache identifies stats served from the Firestore
	// per-team cache (fetched by Groq within the last 24h).
	SourceFirestoreCache = "firestore-cache"
	// SourceFuzzy is stamped on team-lookup payloads resolved by the
	// fuzzy-name fallback layer (token Jaccard + bigram overlap against
	// the canonical catalog).
	SourceFuzzy = "fuzzy"
)

// HealthResponse is returned by GET /v1/health.
type HealthResponse struct {
	Status string `json:"status"`
	Uptime string `json:"uptime"`
}

// TeamRef is a lightweight team reference used inside other payloads.
type TeamRef struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

// TeamStandings is returned by GET /v1/teams/:id/standings.
type TeamStandings struct {
	TeamID     string   `json:"teamId"`
	League     string   `json:"league"`
	Position   int      `json:"position"`
	Played     int      `json:"played"`
	Wins       int      `json:"wins"`
	Draws      int      `json:"draws"`
	Losses     int      `json:"losses"`
	Points     int      `json:"points"`
	RecentForm []string `json:"recentForm"`
	Source     string   `json:"source"`
	Warning    string   `json:"warning,omitempty"`
}

// NextMatch is returned by GET /v1/teams/:id/next-match.
type NextMatch struct {
	TeamID      string  `json:"teamId"`
	Opponent    TeamRef `json:"opponent"`
	Kickoff     string  `json:"kickoff"`
	Competition string  `json:"competition"`
	Venue       string  `json:"venue"`
	Source      string  `json:"source"`
	Warning     string  `json:"warning,omitempty"`
}

// LeagueTableRow is a single row in a league standings table.
type LeagueTableRow struct {
	Position   int      `json:"position"`
	TeamID     string   `json:"teamId"`
	TeamName   string   `json:"teamName"`
	Played     int      `json:"played"`
	Wins       int      `json:"wins"`
	Draws      int      `json:"draws"`
	Losses     int      `json:"losses"`
	Points     int      `json:"points"`
	RecentForm []string `json:"recentForm"`
}

// LeagueStandings is returned by GET /v1/leagues/:id/standings.
type LeagueStandings struct {
	LeagueID string           `json:"leagueId"`
	Name     string           `json:"name"`
	Season   string           `json:"season"`
	Table    []LeagueTableRow `json:"table"`
	Source   string           `json:"source"`
	Warning  string           `json:"warning,omitempty"`
}

// ErrorResponse is the standard error body.
type ErrorResponse struct {
	Error string `json:"error"`
}

// TeamLookup is returned by GET /v1/teams/lookup?name=...
//
// It maps free-text team names (e.g. "Manchester United", "Man Utd")
// arriving from upstream feeds onto the canonical team IDs the rest of
// the API uses (e.g. "man-united"). Confidence is a 0..1 score and
// Source identifies how the mapping was resolved (alias table, OpenAI,
// or stub fallback).
type TeamLookup struct {
	Query         string  `json:"query"`
	TeamID        string  `json:"teamId"`
	CanonicalName string  `json:"canonicalName"`
	League        string  `json:"league"`
	Confidence    float64 `json:"confidence"`
	Source        string  `json:"source"`
}

// Poster is one entry in the posters list inside the assets manifest.
// URL is a path on this server (not absolute) so the caller can prefix
// its own base URL.
type Poster struct {
	MatchID     string `json:"matchId"`
	URL         string `json:"url"`
	GeneratedAt string `json:"generatedAt"`
	SizeBytes   int64  `json:"sizeBytes"`
}

// Logo is one entry in the logos list inside the assets manifest.
type Logo struct {
	TeamSlug    string `json:"teamSlug"`
	TeamName    string `json:"teamName"`
	URL         string `json:"url"`
	GeneratedAt string `json:"generatedAt"`
	SizeBytes   int64  `json:"sizeBytes"`
}

// AssetsManifest is returned by GET /v1/posters. It groups every
// cached generated image (match posters + team logos) so the TV
// client can do a single request to discover what art is available.
type AssetsManifest struct {
	Posters []Poster `json:"posters"`
	Logos   []Logo   `json:"logos"`
}

// PosterRefreshResponse is returned by POST /v1/posters/refresh. The
// body intentionally does not block on the run completing — the cron
// is kicked asynchronously and the caller polls /v1/posters to see
// results.
type PosterRefreshResponse struct {
	Status string `json:"status"`
}

// LiveTeam is one side of a live match: display name, optional short
// name (3-letter code), current score string, and any extra context
// surfaced by the upstream API (e.g. "180/4 (18.2 ov)").
type LiveTeam struct {
	Name      string `json:"name"`
	ShortName string `json:"shortName,omitempty"`
	Score     string `json:"score,omitempty"`
	Overs     string `json:"overs,omitempty"`
}

// LiveCricketMatch is the normalized shape we return for any cricket
// match — currently-live, just-finished, or about-to-start. Mirrors
// what a TV tile needs: identity, two teams with scores, a human
// status string, and a timestamp so clients can detect stale data.
//
// Source identifies the upstream provider so the TV can label or warn
// when data is from a free tier with quirks.
type LiveCricketMatch struct {
	MatchID    string   `json:"matchId"`
	Series     string   `json:"series,omitempty"`
	Format     string   `json:"format,omitempty"`     // T20 / ODI / Test
	Status     string   `json:"status"`               // human-readable: "Live", "Mumbai won by 60 runs", "Match scheduled to begin..."
	State      string   `json:"state"`                // normalized: "live" | "finished" | "scheduled" | "unknown"
	Team1      LiveTeam `json:"team1"`
	Team2      LiveTeam `json:"team2"`
	Venue      string   `json:"venue,omitempty"`
	StartTime  string   `json:"startTime,omitempty"`  // ISO-8601
	UpdatedAt  string   `json:"updatedAt"`            // ISO-8601, server-stamped
	Source     string   `json:"source"`
}

// LiveCricketResponse is the body of GET /v1/live/cricket.
type LiveCricketResponse struct {
	Matches   []LiveCricketMatch `json:"matches"`
	UpdatedAt string             `json:"updatedAt"`
	Source    string             `json:"source"`
	// CachedAgeSeconds is how old the underlying upstream payload is
	// (0 = freshly fetched). Lets the TV decide whether to skip rendering
	// a flash on no-change polls.
	CachedAgeSeconds int `json:"cachedAgeSeconds"`
}

// CricketUpcomingFeed is returned by GET /v1/cricket/international/upcoming.
// Matches is sorted ascending by start time. UpdatedAt is when the feed
// was last refreshed (cron tick), not when each match was last updated.
type CricketUpcomingFeed struct {
	Matches   []LiveCricketMatch `json:"matches"`
	UpdatedAt string             `json:"updatedAt"`
	Source    string             `json:"source"`
}
