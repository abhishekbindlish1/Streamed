# Multi-League Team Standings

**Date:** 2026-05-11<br>
**Status:** Approved

## Problem

A team can participate in multiple leagues (e.g. Arsenal plays Premier League + Champions League). The current `/v1/teams/{id}/standings` endpoint returns a single `TeamStandings` with one `League` string — there is no way for the TV to show a team's position across all competitions.

## Decisions

- **Backward compatibility**: existing `/v1/teams/{id}/standings` is unchanged — returns primary league only.
- **New endpoint**: `GET /v1/teams/{id}/leagues` returns all leagues (up to 3) as `[]TeamStandings`.
- **League discovery**: Groq discovers which leagues a team is in (up to 3, ordered by importance). Result cached 30 days on the team doc.
- **League tables**: full enriched table (all teams, with wins/draws/losses/recentForm) cached per league in a shared `leagues/{leagueId}` Firestore doc with 6h TTL. One cache entry serves every team in that league.
- **Primary league**: first entry Groq returns (domestic league always comes first in practice).

## API

### Existing — unchanged

```
GET /v1/teams/{id}/standings  →  api.TeamStandings
```

### New

```
GET /v1/teams/{id}/leagues  →  api.AllTeamStandingsResponse
```

```go
type AllTeamStandingsResponse struct {
    TeamID  string          `json:"teamId"`
    Leagues []TeamStandings `json:"leagues"` // up to 3, primary first
    Source  string          `json:"source"`
}
```

## Data Model Changes

### `api.LeagueTableRow` — enriched

Add `Wins`, `Draws`, `Losses int` and `RecentForm []string`. These fields were absent; Groq now populates them when fetching a full league table.

```go
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

## Firestore Structure

### Existing `teams/{teamId}` doc — two fields added

| Field | Type | TTL | Notes |
|---|---|---|---|
| `leagueIds` | `[]string` | 30 days | up to 3, Groq-discovered, ordered by importance |
| `leagueIdsUpdatedAt` | `time.Time` | — | timestamp for TTL check |
| `standingsJson` | `string` | 24h | **existing, untouched** |
| `standingsUpdatedAt` | `time.Time` | — | **existing, untouched** |
| `nextMatchJson` | `string` | 24h | **existing, untouched** |
| `nextMatchUpdatedAt` | `time.Time` | — | **existing, untouched** |

### New `leagues/{leagueId}` collection

One document per league. Shared across all teams — if Arsenal and Man City both play in Premier League, they read the same doc.

| Field | Type | TTL | Notes |
|---|---|---|---|
| `tableJson` | `string` | 6h | JSON of enriched `[]LeagueTableRow` |
| `name` | `string` | — | display name e.g. "Premier League" |
| `updatedAt` | `time.Time` | — | timestamp for TTL check |

## Request Flow — `GET /v1/teams/{id}/leagues`

1. Read `teams/{teamId}.leagueIds` from Firestore (30-day TTL).<br>
   **Miss** → call Groq `TeamLeagues(teamID)` → store result on team doc → proceed.<br>
   **Groq failure or empty result** → fall back to single-element response using `TeamStandings` (existing primary standings path), so the endpoint never returns empty.

2. For each leagueId (up to 3), read `leagues/{leagueId}.tableJson` (6h TTL).<br>
   **Miss** → call Groq `LeagueStandings(leagueId)` → store enriched table in league doc → proceed.<br>
   **Team row not found in table** → skip that league (log a warning); don't return a zero-value entry.

3. For each league table, find the row matching `teamId` → build `TeamStandings` from that row.

4. Return `AllTeamStandingsResponse{TeamID, Leagues: []TeamStandings, Source}`.

### `leagueId` canonicalization

Groq returns league names as free text. Before using them as Firestore doc keys or Groq lookup terms, normalize: lowercase, replace spaces with hyphens, strip punctuation. Example: "UEFA Champions League" → `"uefa-champions-league"`. This normalization runs in both `TeamLeagues` (on the IDs Groq returns) and `LeagueStandings` (on the leagueId passed in), ensuring consistent Firestore keys regardless of phrasing variation.

League doc writes are async (non-blocking) just like existing team doc writes.

## Source Interface Changes

Two new methods added to `data.Source`:

```go
// TeamLeagues returns up to 3 league IDs for the team, ordered by importance.
// Returns empty slice (not error) when leagues cannot be determined.
TeamLeagues(ctx context.Context, teamID string) ([]string, error)

// AllTeamStandings returns standings in all leagues the team participates in.
// Primary league is always index 0. Up to 3 entries.
AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error)
```

### Implementations

| Source | `TeamLeagues` | `AllTeamStandings` | `LeagueStandings` |
|---|---|---|---|
| `StubSource` | returns `[]` | wraps `TeamStandings` in slice | existing stub |
| `OpenAISource` | delegates to fallback | delegates to fallback | existing |
| `FootballDataSource` | delegates to fallback | delegates to fallback | existing |
| `CricAPISource` | delegates to fallback | delegates to fallback | existing |
| `GroqSource` | **new Groq call** (discovers leagues) | calls `TeamLeagues` + `LeagueStandings` per league + extracts row | **now implemented** (was delegating to fallback) |
| `FirestoreCacheSource` | reads/writes team doc (30d TTL) | orchestrates: TeamLeagues → per-league LeagueStandings → extract rows | reads/writes league doc (6h TTL) |

## Groq Prompt Changes

### `TeamLeagues` — new

**Search prompt** (compound-beta-mini):
> "What are the top competitions {team} is currently participating in this season? List up to 3, ordered by importance (domestic league first, then continental, then cup)."

**Format prompt** (llama-3.3-70b-versatile):
> Extract from the text and return ONLY: `{"leagueIds": ["premier-league", "champions-league"]}`<br>
> Use lowercase-hyphenated IDs. Max 3 entries.

### `LeagueStandings` — now implemented

**Search prompt** (compound-beta-mini):
> "Current {league} standings table this season: all teams with position, played, wins, draws, losses, points, and last 5 results for each."

**Format prompt** (llama-3.3-70b-versatile):
> Extract and return ONLY: `{"leagueId":"...","name":"...","season":"...","table":[{"position":1,"teamId":"...","teamName":"...","played":0,"wins":0,"draws":0,"losses":0,"points":0,"recentForm":["W","W","D","L","W"]}]}`

## Cache TTL Summary

| Cache | TTL | Rationale |
|---|---|---|
| Team → league IDs | 30 days | League participation doesn't change mid-season |
| League table | 6 hours | Standings update after each matchday |
| Team primary standings | 24h | Existing, unchanged |
| Team next match | 24h | Existing, unchanged |

## What Does Not Change

- `GET /v1/teams/{id}/standings` handler, cache key, TTL
- `GET /v1/teams/{id}/next-match` handler, cache key, TTL
- `GET /v1/leagues/{id}/standings` handler (now backed by the 6h-cached league doc rather than delegating to fallback on every call — improvement, not a breaking change)
- Existing `teamDoc` fields in Firestore
- All existing source chain ordering in `main.go`
