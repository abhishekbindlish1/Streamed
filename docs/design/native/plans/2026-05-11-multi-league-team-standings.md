# Multi-League Team Standings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `GET /v1/teams/{id}/leagues` returning standings across all leagues a team participates in (up to 3), while leaving the existing `/v1/teams/{id}/standings` endpoint completely unchanged.

**Architecture:** Extend the `data.Source` interface with two new methods (`TeamLeagues`, `AllTeamStandings`). Each source in the chain either implements the real logic or delegates to its fallback. `GroqSource` discovers which leagues a team plays in and fetches enriched full-table standings. `FirestoreCacheSource` wraps both with TTL caching (30 days for league IDs, 6 hours for league tables). The HTTP handler stitches the result into `AllTeamStandingsResponse`.

**Tech Stack:** Go 1.22+, Firestore SDK (`cloud.google.com/go/firestore`), Groq API (compound-beta-mini for web search, llama-3.3-70b-versatile for JSON extraction), `net/http` stdlib mux.

---

## File Map

| File | Action | Responsibility |
|---|---|---|
| `backend/internal/api/types.go` | Modify | Add `Wins/Draws/Losses/RecentForm` to `LeagueTableRow`; add `AllTeamStandingsResponse` |
| `backend/internal/data/stub.go` | Modify | Add `TeamLeagues`/`AllTeamStandings` to `Source` interface; implement both on `StubSource` |
| `backend/internal/data/groq.go` | Modify | Implement `TeamLeagues` (Groq call), `LeagueStandings` (real Groq call, was fallback-only), `AllTeamStandings` (orchestrates), and `canonicalizeLeagueID` helper |
| `backend/internal/data/firestorecache.go` | Modify | Add `leagueDoc` struct; implement `TeamLeagues` (30d TTL on team doc), `LeagueStandings` (6h TTL on `leagues/` collection), `AllTeamStandings` (orchestrates) |
| `backend/internal/data/openai.go` | Modify | Stub `TeamLeagues` (return `[]`) and `AllTeamStandings` (delegate to fallback) |
| `backend/internal/data/footballdata.go` | Modify | Same stubs as openai.go |
| `backend/internal/data/cricapi.go` | Modify | Same stubs as openai.go |
| `backend/internal/server/server.go` | Modify | Add `handleAllTeamLeagues` handler; wire `GET /v1/teams/{id}/leagues` in `Handler()` |

---

### Task 1: Enrich `api.LeagueTableRow` and add `AllTeamStandingsResponse`

**Files:**
- Modify: `backend/internal/api/types.go`

- [ ] **Step 1: Write the failing test**

Add to `backend/main_test.go` (or a new `backend/internal/api/types_test.go` if one doesn't exist — but because there's no existing api package test, we test via the compile check by verifying the struct fields exist; the real test comes in Task 8):

Actually `api/types.go` has no test file. We confirm the compile step in step 4.

- [ ] **Step 2: Edit `api/types.go` — enrich `LeagueTableRow`**

In `backend/internal/api/types.go`, replace the `LeagueTableRow` struct (lines 67-74):

```go
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
```

- [ ] **Step 3: Add `AllTeamStandingsResponse` to `api/types.go`**

Add the new type immediately after the `LeagueStandings` struct (after line 84):

```go
// AllTeamStandingsResponse is returned by GET /v1/teams/{id}/leagues.
// Leagues is ordered: primary (domestic) league first, then continental, then cup.
// Always has at least one entry — falls back to primary standings on Groq failure.
type AllTeamStandingsResponse struct {
	TeamID  string         `json:"teamId"`
	Leagues []TeamStandings `json:"leagues"`
	Source  string         `json:"source"`
}
```

- [ ] **Step 4: Verify it compiles**

```bash
cd backend && go build ./...
```

Expected: no errors.

- [ ] **Step 5: Update stub league data to include new fields**

In `backend/internal/data/stub.go`, update the `stubLeagues` map to include `Wins`, `Draws`, `Losses`, and `RecentForm` on each `LeagueTableRow`:

```go
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
```

- [ ] **Step 6: Compile again**

```bash
cd backend && go build ./...
```

Expected: no errors.

- [ ] **Step 7: Commit**

```bash
cd backend && git add internal/api/types.go internal/data/stub.go
git commit -m "api: enrich LeagueTableRow with wins/draws/losses/recentForm, add AllTeamStandingsResponse"
```

---

### Task 2: Extend `Source` interface and implement on `StubSource`

**Files:**
- Modify: `backend/internal/data/stub.go`

- [ ] **Step 1: Add two methods to the `Source` interface in `stub.go`**

In `backend/internal/data/stub.go`, update the `Source` interface (starting at line 17) to add two methods:

```go
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
```

- [ ] **Step 2: Verify it fails to compile (all other sources now need the new methods)**

```bash
cd backend && go build ./...
```

Expected: compile errors listing `openai.go`, `footballdata.go`, `cricapi.go`, `groq.go`, `firestorecache.go` as missing the two new methods.

- [ ] **Step 3: Implement `TeamLeagues` and `AllTeamStandings` on `StubSource`**

Add these two methods to `stub.go` after the `LookupTeamByName` method:

```go
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
	primary.Source = api.SourceStub
	return []api.TeamStandings{primary}, nil
}
```

- [ ] **Step 4: Compile — still expect errors from the other sources**

```bash
cd backend && go build ./...
```

Expected: same errors from other files, but NOT from `stub.go`.

- [ ] **Step 5: Commit stub progress**

```bash
cd backend && git add internal/data/stub.go
git commit -m "data: add TeamLeagues/AllTeamStandings to Source interface, implement on StubSource"
```

---

### Task 3: Stub implementations on `OpenAISource`, `FootballDataSource`, `CricAPISource`

**Files:**
- Modify: `backend/internal/data/openai.go`
- Modify: `backend/internal/data/footballdata.go`
- Modify: `backend/internal/data/cricapi.go`

All three delegate to their existing `fallback` source — they have no direct knowledge of league membership.

- [ ] **Step 1: Add stubs to `openai.go`**

Append to `backend/internal/data/openai.go` (after the existing `LookupTeamByName` method):

```go
func (o *OpenAISource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	return o.fallback.TeamLeagues(ctx, teamID)
}

func (o *OpenAISource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	return o.fallback.AllTeamStandings(ctx, teamID)
}
```

- [ ] **Step 2: Add stubs to `footballdata.go`**

Find `FootballDataSource`'s `LookupTeamByName` method and append after it:

```go
func (f *FootballDataSource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	return f.next.TeamLeagues(ctx, teamID)
}

func (f *FootballDataSource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	return f.next.AllTeamStandings(ctx, teamID)
}
```

- [ ] **Step 3: Add stubs to `cricapi.go`**

Find `CricAPISource`'s `LookupTeamByName` method and append after it:

```go
func (c *CricAPISource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	return c.fallback.TeamLeagues(ctx, teamID)
}

func (c *CricAPISource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	return c.fallback.AllTeamStandings(ctx, teamID)
}
```

- [ ] **Step 4: Compile — only `groq.go` and `firestorecache.go` should still error**

```bash
cd backend && go build ./...
```

Expected: only errors from `groq.go` and `firestorecache.go`.

- [ ] **Step 5: Commit**

```bash
cd backend && git add internal/data/openai.go internal/data/footballdata.go internal/data/cricapi.go
git commit -m "data: stub TeamLeagues/AllTeamStandings on OpenAI/FootballData/CricAPI sources (delegate to fallback)"
```

---

### Task 4: Implement `GroqSource.TeamLeagues`, `LeagueStandings`, and `AllTeamStandings`

**Files:**
- Modify: `backend/internal/data/groq.go`

This is the biggest change. Groq now actually implements `LeagueStandings` (was delegating to fallback) and adds `TeamLeagues` + `AllTeamStandings`.

- [ ] **Step 1: Add the `canonicalizeLeagueID` helper to `groq.go`**

Add this function after the `NewGroqSource` constructor:

```go
// canonicalizeLeagueID normalizes a league name to a consistent Firestore
// key. Applies lowercase + hyphen-replace + punctuation stripping so that
// "UEFA Champions League" and "champions-league" both map to
// "uefa-champions-league".
func canonicalizeLeagueID(raw string) string {
	raw = strings.ToLower(strings.TrimSpace(raw))
	raw = strings.ReplaceAll(raw, " ", "-")
	// Strip any character that isn't a letter, digit, or hyphen.
	var b strings.Builder
	for _, r := range raw {
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '-' {
			b.WriteRune(r)
		}
	}
	// Collapse consecutive hyphens (e.g. "a--b" → "a-b").
	result := b.String()
	for strings.Contains(result, "--") {
		result = strings.ReplaceAll(result, "--", "-")
	}
	return strings.Trim(result, "-")
}
```

- [ ] **Step 2: Implement `TeamLeagues` on `GroqSource`**

Add this method to `groq.go` after `LeagueStandings`:

```go
// TeamLeagues calls Groq to discover which leagues (up to 3) the team
// participates in this season. Returns empty slice on any failure —
// callers must handle that gracefully (e.g. fall back to primary standings).
func (g *GroqSource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	prose, err := g.searchProse(ctx, fmt.Sprintf(
		"What are the top competitions %s is currently participating in this season? List up to 3, ordered by importance (domestic league first, then continental, then cup).",
		teamID,
	))
	if err != nil {
		g.logger.Warn("groq team-leagues prose failed", "team", teamID, "err", err)
		return []string{}, nil
	}

	prompt := fmt.Sprintf(
		"Text about %s competitions:\n%s\n\n"+
			`Return ONLY this JSON with lowercase-hyphenated league IDs, max 3 entries: {"leagueIds":["premier-league","champions-league"]}`,
		teamID, prose,
	)

	var out struct {
		LeagueIDs []string `json:"leagueIds"`
	}
	if err := g.formatJSON(ctx, prompt, &out); err != nil {
		g.logger.Warn("groq team-leagues json format failed", "team", teamID, "err", err)
		return []string{}, nil
	}

	// Canonicalize each ID returned by Groq.
	canonical := make([]string, 0, len(out.LeagueIDs))
	for _, id := range out.LeagueIDs {
		if c := canonicalizeLeagueID(id); c != "" {
			canonical = append(canonical, c)
		}
	}
	if len(canonical) > 3 {
		canonical = canonical[:3]
	}
	return canonical, nil
}
```

- [ ] **Step 3: Replace the `LeagueStandings` stub with a real Groq implementation**

Replace the existing `LeagueStandings` method in `groq.go` (currently just delegates to fallback):

```go
// LeagueStandings fetches the full enriched standings table for a league
// via the 2-step Groq pipeline. The leagueID is canonicalized before use.
func (g *GroqSource) LeagueStandings(ctx context.Context, leagueID string) (api.LeagueStandings, error) {
	leagueID = canonicalizeLeagueID(leagueID)
	if leagueID == "" {
		return g.fallback.LeagueStandings(ctx, leagueID)
	}

	prose, err := g.searchProse(ctx, fmt.Sprintf(
		"Current %s standings table this season: all teams with position, played, wins, draws, losses, points, and last 5 results for each.",
		leagueID,
	))
	if err != nil {
		g.logger.Warn("groq league-standings prose failed, falling back", "league", leagueID, "err", err)
		return g.fallback.LeagueStandings(ctx, leagueID)
	}

	prompt := fmt.Sprintf(
		"Text about %s standings:\n%s\n\n"+
			`Extract and return ONLY valid JSON (0 for unknowns, recentForm is an array of "W","D","L"):
{"leagueId":%q,"name":"","season":"","table":[{"position":1,"teamId":"","teamName":"","played":0,"wins":0,"draws":0,"losses":0,"points":0,"recentForm":["W"]}]}`,
		leagueID, prose, leagueID,
	)

	var out api.LeagueStandings
	if err := g.formatJSON(ctx, prompt, &out); err != nil {
		g.logger.Warn("groq league-standings json format failed, falling back", "league", leagueID, "err", err)
		return g.fallback.LeagueStandings(ctx, leagueID)
	}
	out.LeagueID = leagueID
	out.Source = api.SourceGroq
	if out.Warning == "" {
		out.Warning = "live-web"
	}
	return out, nil
}
```

- [ ] **Step 4: Implement `AllTeamStandings` on `GroqSource`**

Add this method to `groq.go` after `TeamLeagues`:

```go
// AllTeamStandings discovers the team's leagues then fetches each table,
// extracts the team's row, and builds a TeamStandings per league.
// Returns a single-element slice (primary standings) on any discovery failure.
func (g *GroqSource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	leagueIDs, err := g.TeamLeagues(ctx, teamID)
	if err != nil || len(leagueIDs) == 0 {
		// Fall back to single primary standings so the endpoint never returns empty.
		primary, fErr := g.TeamStandings(ctx, teamID)
		if fErr != nil {
			return nil, fErr
		}
		return []api.TeamStandings{primary}, nil
	}

	var result []api.TeamStandings
	for _, lid := range leagueIDs {
		table, err := g.LeagueStandings(ctx, lid)
		if err != nil {
			g.logger.Warn("groq all-standings: league fetch failed, skipping", "team", teamID, "league", lid, "err", err)
			continue
		}
		row := findTeamRow(table.Table, teamID)
		if row == nil {
			g.logger.Warn("groq all-standings: team not found in league table, skipping", "team", teamID, "league", lid)
			continue
		}
		result = append(result, api.TeamStandings{
			TeamID:     teamID,
			League:     table.Name,
			Position:   row.Position,
			Played:     row.Played,
			Wins:       row.Wins,
			Draws:      row.Draws,
			Losses:     row.Losses,
			Points:     row.Points,
			RecentForm: row.RecentForm,
			Source:     api.SourceGroq,
			Warning:    "live-web",
		})
	}

	if len(result) == 0 {
		primary, err := g.TeamStandings(ctx, teamID)
		if err != nil {
			return nil, err
		}
		return []api.TeamStandings{primary}, nil
	}
	return result, nil
}

// findTeamRow searches a league table for a team by ID (case-insensitive).
// Returns nil when the team is not found.
func findTeamRow(table []api.LeagueTableRow, teamID string) *api.LeagueTableRow {
	lower := strings.ToLower(teamID)
	for i := range table {
		if strings.ToLower(table[i].TeamID) == lower ||
			strings.ToLower(canonicalizeLeagueID(table[i].TeamName)) == lower {
			return &table[i]
		}
	}
	return nil
}
```

- [ ] **Step 5: Compile — only `firestorecache.go` should still error**

```bash
cd backend && go build ./...
```

Expected: only errors from `firestorecache.go`.

- [ ] **Step 6: Commit**

```bash
cd backend && git add internal/data/groq.go
git commit -m "data: implement GroqSource.TeamLeagues, LeagueStandings (real), AllTeamStandings + canonicalizeLeagueID"
```

---

### Task 5: Implement `FirestoreCacheSource` additions

**Files:**
- Modify: `backend/internal/data/firestorecache.go`

This adds `leagueDoc` (the new `leagues/{leagueId}` Firestore document), caching for `TeamLeagues` (30d TTL on the existing team doc), and caching for `LeagueStandings` (6h TTL on the new league doc), plus `AllTeamStandings` orchestration.

- [ ] **Step 1: Add TTL constants and `leagueDoc` struct**

Add at the top of `firestorecache.go` after the existing `teamCacheTTL` constant:

```go
const leagueIDsCacheTTL = 30 * 24 * time.Hour // 30 days — league participation doesn't change mid-season
const leagueTableCacheTTL = 6 * time.Hour      // 6h — refreshes after each matchday
```

Extend the existing `teamDoc` struct to include the two new fields (add after `NextMatchUpdatedAt`):

```go
type teamDoc struct {
	StandingsJSON      string    `firestore:"standingsJson"`
	NextMatchJSON      string    `firestore:"nextMatchJson"`
	StandingsUpdatedAt time.Time `firestore:"standingsUpdatedAt"`
	NextMatchUpdatedAt time.Time `firestore:"nextMatchUpdatedAt"`
	// New fields for multi-league support.
	LeagueIDs          []string  `firestore:"leagueIds"`
	LeagueIDsUpdatedAt time.Time `firestore:"leagueIdsUpdatedAt"`
}
```

Add a new struct for the `leagues/{leagueId}` collection:

```go
// leagueDoc is the document stored at leagues/{leagueId}.
// Shared across all teams in the same league.
type leagueDoc struct {
	TableJSON string    `firestore:"tableJson"`
	Name      string    `firestore:"name"`
	UpdatedAt time.Time `firestore:"updatedAt"`
}
```

- [ ] **Step 2: Implement `TeamLeagues` on `FirestoreCacheSource`**

Add after the existing `LookupTeamByName` method:

```go
func (f *FirestoreCacheSource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	// Try cache first.
	if ids, ok := f.getCachedLeagueIDs(ctx, teamID); ok {
		f.logger.Info("firestore cache hit", "type", "league-ids", "team", teamID)
		return ids, nil
	}
	ids, err := f.inner.TeamLeagues(ctx, teamID)
	if err != nil {
		return ids, err
	}
	go f.writeLeagueIDs(teamID, ids)
	return ids, nil
}

func (f *FirestoreCacheSource) getCachedLeagueIDs(ctx context.Context, teamID string) ([]string, bool) {
	snap, err := f.fs.Collection("teams").Doc(teamID).Get(ctx)
	if err != nil {
		return nil, false
	}
	var doc teamDoc
	if err := snap.DataTo(&doc); err != nil {
		return nil, false
	}
	if len(doc.LeagueIDs) == 0 || time.Since(doc.LeagueIDsUpdatedAt) > leagueIDsCacheTTL {
		return nil, false
	}
	return doc.LeagueIDs, true
}

func (f *FirestoreCacheSource) writeLeagueIDs(teamID string, ids []string) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	_, err := f.fs.Collection("teams").Doc(teamID).Set(ctx, map[string]any{
		"leagueIds":          ids,
		"leagueIdsUpdatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	if err != nil {
		f.logger.Warn("cache: write league-ids failed", "team", teamID, "err", err)
	}
}
```

- [ ] **Step 3: Implement `LeagueStandings` on `FirestoreCacheSource` with shared league doc**

Replace the existing `LeagueStandings` method (currently just delegates to inner):

```go
func (f *FirestoreCacheSource) LeagueStandings(ctx context.Context, leagueID string) (api.LeagueStandings, error) {
	if cached, ok := f.getCachedLeagueStandings(ctx, leagueID); ok {
		f.logger.Info("firestore cache hit", "type", "league-standings", "league", leagueID)
		return *cached, nil
	}
	result, err := f.inner.LeagueStandings(ctx, leagueID)
	if err != nil {
		return result, err
	}
	go f.writeLeagueStandings(leagueID, result)
	return result, nil
}

func (f *FirestoreCacheSource) getCachedLeagueStandings(ctx context.Context, leagueID string) (*api.LeagueStandings, bool) {
	snap, err := f.fs.Collection("leagues").Doc(leagueID).Get(ctx)
	if err != nil {
		return nil, false
	}
	var doc leagueDoc
	if err := snap.DataTo(&doc); err != nil {
		return nil, false
	}
	if doc.TableJSON == "" || time.Since(doc.UpdatedAt) > leagueTableCacheTTL {
		return nil, false
	}
	var table []api.LeagueTableRow
	if err := json.Unmarshal([]byte(doc.TableJSON), &table); err != nil {
		return nil, false
	}
	return &api.LeagueStandings{
		LeagueID: leagueID,
		Name:     doc.Name,
		Table:    table,
		Source:   api.SourceFirestoreCache,
	}, true
}

func (f *FirestoreCacheSource) writeLeagueStandings(leagueID string, ls api.LeagueStandings) {
	b, err := json.Marshal(ls.Table)
	if err != nil {
		f.logger.Warn("cache: marshal league table failed", "league", leagueID, "err", err)
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	_, err = f.fs.Collection("leagues").Doc(leagueID).Set(ctx, map[string]any{
		"tableJson": string(b),
		"name":      ls.Name,
		"updatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	if err != nil {
		f.logger.Warn("cache: write league standings failed", "league", leagueID, "err", err)
	}
}
```

- [ ] **Step 4: Implement `AllTeamStandings` on `FirestoreCacheSource`**

Add this method after `writeLeagueStandings`:

```go
// AllTeamStandings orchestrates: fetch league IDs (cached 30d) → per league
// fetch full table (cached 6h) → extract the team's row → build TeamStandings.
// Falls back to single-element primary standings if league discovery fails.
func (f *FirestoreCacheSource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	leagueIDs, err := f.TeamLeagues(ctx, teamID)
	if err != nil || len(leagueIDs) == 0 {
		primary, fErr := f.inner.TeamStandings(ctx, teamID)
		if fErr != nil {
			return nil, fErr
		}
		return []api.TeamStandings{primary}, nil
	}

	var result []api.TeamStandings
	for _, lid := range leagueIDs {
		table, err := f.LeagueStandings(ctx, lid)
		if err != nil {
			f.logger.Warn("cache: all-standings: league fetch failed, skipping", "team", teamID, "league", lid, "err", err)
			continue
		}
		row := findTeamRow(table.Table, teamID)
		if row == nil {
			f.logger.Warn("cache: all-standings: team not in league table, skipping", "team", teamID, "league", lid)
			continue
		}
		result = append(result, api.TeamStandings{
			TeamID:     teamID,
			League:     table.Name,
			Position:   row.Position,
			Played:     row.Played,
			Wins:       row.Wins,
			Draws:      row.Draws,
			Losses:     row.Losses,
			Points:     row.Points,
			RecentForm: row.RecentForm,
			Source:     table.Source,
		})
	}

	if len(result) == 0 {
		primary, err := f.inner.TeamStandings(ctx, teamID)
		if err != nil {
			return nil, err
		}
		return []api.TeamStandings{primary}, nil
	}
	return result, nil
}
```

- [ ] **Step 5: Compile — everything should pass now**

```bash
cd backend && go build ./...
```

Expected: no errors.

- [ ] **Step 6: Run existing tests**

```bash
cd backend && go test ./...
```

Expected: all pass (no new tests yet — added in Task 7).

- [ ] **Step 7: Commit**

```bash
cd backend && git add internal/data/firestorecache.go
git commit -m "data: FirestoreCacheSource — TeamLeagues (30d TTL), LeagueStandings (6h leagues/ doc), AllTeamStandings"
```

---

### Task 6: Wire the HTTP handler

**Files:**
- Modify: `backend/internal/server/server.go`

- [ ] **Step 1: Add `handleAllTeamLeagues` handler to `server.go`**

Add this method to `server.go` after `handleLeagueStandings`:

```go
// handleAllTeamLeagues serves GET /v1/teams/{id}/leagues.
// Returns standings in all leagues the team participates in (up to 3).
// Primary league (domestic) is always index 0.
// Falls back to single-element primary standings if league discovery fails,
// so the response always contains at least one entry.
func (s *Server) handleAllTeamLeagues(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	if id == "" {
		writeError(w, http.StatusBadRequest, "missing team id")
		return
	}
	leagues, err := s.source.AllTeamStandings(r.Context(), id)
	if err != nil {
		s.respondError(w, err)
		return
	}
	source := api.SourceStub
	if len(leagues) > 0 {
		source = leagues[0].Source
	}
	writeJSON(w, http.StatusOK, api.AllTeamStandingsResponse{
		TeamID:  id,
		Leagues: leagues,
		Source:  source,
	})
}
```

- [ ] **Step 2: Wire the route in `Handler()`**

In `server.go`, find the block where `handleLeagueStandings` is registered (around line 101) and add the new route immediately after:

```go
mux.HandleFunc("GET /v1/teams/{id}/standings", s.handleTeamStandings)
mux.HandleFunc("GET /v1/teams/{id}/next-match", s.handleNextMatch)
mux.HandleFunc("GET /v1/teams/{id}/leagues", s.handleAllTeamLeagues)  // NEW
mux.HandleFunc("GET /v1/leagues/{id}/standings", s.handleLeagueStandings)
```

- [ ] **Step 3: Compile**

```bash
cd backend && go build ./...
```

Expected: no errors.

- [ ] **Step 4: Smoke test with curl**

Start the server locally (stub source, no Groq key needed):

```bash
cd backend && go run . &
sleep 1
curl -s http://localhost:8080/v1/teams/arsenal/leagues | jq .
```

Expected output shape:
```json
{
  "teamId": "arsenal",
  "leagues": [
    {
      "teamId": "arsenal",
      "league": "Premier League",
      "position": 2,
      "played": 28,
      "wins": 19,
      "draws": 5,
      "losses": 4,
      "points": 62,
      "recentForm": ["W","W","D","W","L"],
      "source": "stub"
    }
  ],
  "source": "stub"
}
```

Also verify the existing endpoint is unchanged:
```bash
curl -s http://localhost:8080/v1/teams/arsenal/standings | jq .
```

Expected: same response as before (no regression).

Kill the background server: `kill %1`

- [ ] **Step 5: Commit**

```bash
cd backend && git add internal/server/server.go
git commit -m "server: add GET /v1/teams/{id}/leagues handler wired to AllTeamStandings"
```

---

### Task 7: Unit tests for `canonicalizeLeagueID` and `findTeamRow`

**Files:**
- Create: `backend/internal/data/groq_test.go`

These are pure functions with no external dependencies — ideal for unit tests.

- [ ] **Step 1: Write the test file**

Create `backend/internal/data/groq_test.go`:

```go
package data

import (
	"testing"

	"github.com/abindlish/streamed-backend/internal/api"
)

func TestCanonicalizeLeagueID(t *testing.T) {
	cases := []struct {
		in   string
		want string
	}{
		{"Premier League", "premier-league"},
		{"UEFA Champions League", "uefa-champions-league"},
		{"La Liga", "la-liga"},
		{"Bundesliga", "bundesliga"},
		{"Serie A", "serie-a"},
		{"already-canonical", "already-canonical"},
		{"  spaces  ", "spaces"},
		{"with--double--hyphen", "with-double-hyphen"},
		{"punctuation!@#$%", "punctuation"},
		{"", ""},
	}
	for _, tc := range cases {
		got := canonicalizeLeagueID(tc.in)
		if got != tc.want {
			t.Errorf("canonicalizeLeagueID(%q) = %q, want %q", tc.in, got, tc.want)
		}
	}
}

func TestFindTeamRow(t *testing.T) {
	table := []api.LeagueTableRow{
		{Position: 1, TeamID: "man-city", TeamName: "Manchester City", Points: 67},
		{Position: 2, TeamID: "arsenal", TeamName: "Arsenal", Points: 62},
		{Position: 3, TeamID: "liverpool", TeamName: "Liverpool", Points: 58},
	}

	// Find by exact teamID.
	row := findTeamRow(table, "arsenal")
	if row == nil || row.Position != 2 {
		t.Errorf("expected arsenal at position 2, got %v", row)
	}

	// Find by case-insensitive teamID.
	row = findTeamRow(table, "LIVERPOOL")
	if row == nil || row.Position != 3 {
		t.Errorf("expected liverpool at position 3, got %v", row)
	}

	// Not found.
	row = findTeamRow(table, "barcelona")
	if row != nil {
		t.Errorf("expected nil for unknown team, got %v", row)
	}

	// Empty table.
	row = findTeamRow(nil, "arsenal")
	if row != nil {
		t.Errorf("expected nil for empty table, got %v", row)
	}
}
```

- [ ] **Step 2: Run tests**

```bash
cd backend && go test ./internal/data/ -run TestCanonicalize -v
cd backend && go test ./internal/data/ -run TestFindTeam -v
```

Expected: both PASS.

- [ ] **Step 3: Run the full test suite**

```bash
cd backend && go test ./...
```

Expected: all pass.

- [ ] **Step 4: Commit**

```bash
cd backend && git add internal/data/groq_test.go
git commit -m "test: unit tests for canonicalizeLeagueID and findTeamRow"
```

---

### Task 8: Integration test for the new endpoint via `main_test.go`

**Files:**
- Modify: `backend/main_test.go`

- [ ] **Step 1: Read the existing test structure**

Read `backend/main_test.go` to understand how existing integration tests start the server and make requests. Follow the same pattern exactly.

- [ ] **Step 2: Add `TestAllTeamLeagues` test**

Add the following test to `backend/main_test.go` (following the existing test style):

```go
func TestAllTeamLeagues(t *testing.T) {
	srv := server.New(data.NewStubSource(), slog.Default())
	ts := httptest.NewServer(srv.Handler())
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/arsenal/leagues")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("expected 200, got %d", resp.StatusCode)
	}

	var out api.AllTeamStandingsResponse
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		t.Fatal(err)
	}

	if out.TeamID != "arsenal" {
		t.Errorf("expected teamId=arsenal, got %q", out.TeamID)
	}
	if len(out.Leagues) == 0 {
		t.Fatal("expected at least one league entry")
	}
	first := out.Leagues[0]
	if first.TeamID != "arsenal" {
		t.Errorf("expected leagues[0].teamId=arsenal, got %q", first.TeamID)
	}
	if first.Position == 0 {
		t.Error("expected non-zero position")
	}
}

func TestAllTeamLeagues_UnknownTeam(t *testing.T) {
	srv := server.New(data.NewStubSource(), slog.Default())
	ts := httptest.NewServer(srv.Handler())
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/nonexistent-team-xyz/leagues")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()

	// Unknown teams return ErrNotFound → 404.
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("expected 404 for unknown team, got %d", resp.StatusCode)
	}
}

func TestExistingStandingsUnchanged(t *testing.T) {
	// Regression: the existing /standings endpoint must be unaffected.
	srv := server.New(data.NewStubSource(), slog.Default())
	ts := httptest.NewServer(srv.Handler())
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/arsenal/standings")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("standings: expected 200, got %d", resp.StatusCode)
	}

	var out api.TeamStandings
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		t.Fatal(err)
	}
	if out.TeamID != "arsenal" {
		t.Errorf("standings: expected teamId=arsenal, got %q", out.TeamID)
	}
}
```

Note: if `main_test.go` doesn't already import `net/http/httptest`, `encoding/json`, and the `api`/`data`/`server` packages, add them to the import block.

- [ ] **Step 3: Run the new tests**

```bash
cd backend && go test ./... -run TestAllTeamLeagues -v
cd backend && go test ./... -run TestExistingStandingsUnchanged -v
```

Expected: all PASS.

- [ ] **Step 4: Run the full suite one final time**

```bash
cd backend && go test ./...
```

Expected: all pass, no regressions.

- [ ] **Step 5: Commit**

```bash
cd backend && git add main_test.go
git commit -m "test: integration tests for GET /v1/teams/{id}/leagues and regression on /standings"
```

---

## Self-Review

**Spec coverage:**
- ✅ Backward compat: existing `/standings` untouched
- ✅ New endpoint: `GET /v1/teams/{id}/leagues` → `AllTeamStandingsResponse`
- ✅ Groq discovers leagues (up to 3, 30d cache)
- ✅ Shared `leagues/{leagueId}` Firestore doc (6h cache)
- ✅ `LeagueTableRow` enriched with `wins/draws/losses/recentForm`
- ✅ `canonicalizeLeagueID` applied at both read and write points
- ✅ Fallback on Groq failure → single-element primary standings
- ✅ Team-not-in-table → skip that league (log warning)
- ✅ All sources implement the new interface methods
- ✅ Async (non-blocking) Firestore writes via `go f.write*()`
- ✅ `Source` field on response reflects actual data origin

**Placeholder scan:** None found — every step has explicit code.

**Type consistency:** `findTeamRow` defined in `groq.go` (package `data`) and called from both `groq.go` and `firestorecache.go` — they share the `data` package so no import needed. Confirmed consistent.
