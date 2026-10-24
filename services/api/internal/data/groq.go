package data

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strings"
	"time"

	"github.com/abindlish/streamed-backend/internal/api"
)

const groqBaseURL = "https://api.groq.com/openai/v1"

// Default model names. Override via GROQ_SEARCH_MODEL / GROQ_JSON_MODEL.
const defaultGroqSearchModel = "compound-beta-mini"
const defaultGroqJSONModel = "llama-3.3-70b-versatile"

// GroqSource performs a 2-step pipeline for current sports data:
//
//  1. compound-beta-mini (web search): fetches a prose summary of the
//     team's current standings / next fixture from the live internet.
//
//  2. llama-3.3-70b-versatile: parses that prose into the strict JSON
//     schema the TV app expects.
//
// Both steps run against the Groq API. Free tier is ~8000 TPM; prose
// capped at 1500 tokens to stay within budget.
//
// Falls back to the wrapped source on any error so the TV never 500s.
type GroqSource struct {
	apiKey      string
	endpoint    string
	searchModel string
	jsonModel   string
	client      *http.Client
	fallback    Source
	logger      *slog.Logger
}

// NewGroqSource constructs a GroqSource. searchModel defaults to
// compound-beta-mini; jsonModel defaults to llama-3.3-70b-versatile.
func NewGroqSource(apiKey, searchModel, jsonModel string, fallback Source, logger *slog.Logger) *GroqSource {
	if searchModel == "" {
		searchModel = defaultGroqSearchModel
	}
	if jsonModel == "" {
		jsonModel = defaultGroqJSONModel
	}
	if logger == nil {
		logger = slog.Default()
	}
	return &GroqSource{
		apiKey:      apiKey,
		endpoint:    groqBaseURL + "/chat/completions",
		searchModel: searchModel,
		jsonModel:   jsonModel,
		client:      &http.Client{Timeout: 45 * time.Second},
		fallback:    fallback,
		logger:      logger,
	}
}

func (g *GroqSource) Name() string { return api.SourceGroq }

// searchProse calls compound-beta-mini with a search-oriented prompt
// and returns the assistant's prose response. Capped at 800 tokens —
// compound-mini adds web-search results to the context internally,
// which means even small prompts can hit the free-tier token budget.
// Keep both the prompt and response tight to avoid 413s.
func (g *GroqSource) searchProse(ctx context.Context, query string) (string, error) {
	body := map[string]any{
		"model": g.searchModel,
		"messages": []map[string]string{
			{"role": "user", "content": query},
		},
		"max_completion_tokens": 800,
	}
	return g.callText(ctx, body)
}

// formatJSON calls llama to extract structured data from prose and
// unmarshal it into out. Capped at 512 tokens — the output is always
// a small JSON object.
func (g *GroqSource) formatJSON(ctx context.Context, prompt string, out any) error {
	body := map[string]any{
		"model": g.jsonModel,
		"messages": []map[string]string{
			{
				"role":    "system",
				"content": "You are a sports data formatter. Extract the requested fields from the provided text and return ONLY valid compact JSON with no prose, no markdown fences, and no explanation.",
			},
			{"role": "user", "content": prompt},
		},
		"response_format":       map[string]string{"type": "json_object"},
		"max_completion_tokens": 512,
	}
	text, err := g.callText(ctx, body)
	if err != nil {
		return err
	}
	return json.Unmarshal([]byte(text), out)
}

func (g *GroqSource) callText(ctx context.Context, body any) (string, error) {
	buf, err := json.Marshal(body)
	if err != nil {
		return "", fmt.Errorf("marshal: %w", err)
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, g.endpoint, bytes.NewReader(buf))
	if err != nil {
		return "", fmt.Errorf("build request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+g.apiKey)

	resp, err := g.client.Do(req)
	if err != nil {
		return "", fmt.Errorf("http: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode/100 != 2 {
		snippet, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
		return "", fmt.Errorf("groq status %d: %s", resp.StatusCode, strings.TrimSpace(string(snippet)))
	}

	var parsed struct {
		Choices []struct {
			Message struct {
				Content string `json:"content"`
			} `json:"message"`
		} `json:"choices"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&parsed); err != nil {
		return "", fmt.Errorf("decode: %w", err)
	}
	if len(parsed.Choices) == 0 {
		return "", fmt.Errorf("no choices in response")
	}
	return parsed.Choices[0].Message.Content, nil
}

func (g *GroqSource) TeamStandings(ctx context.Context, teamID string) (api.TeamStandings, error) {
	prose, err := g.searchProse(ctx, fmt.Sprintf(
		"Current league position and stats for %s this season: position, played, wins, draws, losses, points, last 5 results.",
		teamID,
	))
	if err != nil {
		g.logger.Warn("groq standings prose failed, falling back", "team", teamID, "err", err)
		return g.fallback.TeamStandings(ctx, teamID)
	}

	prompt := fmt.Sprintf(
		"Text about %s:\n%s\n\nReturn ONLY this JSON (0 for unknowns). recentForm must be an array of single-character strings each being W, D, or L (e.g. [\"W\",\"L\",\"W\",\"D\",\"W\"]):\n"+
			`{"teamId":%q,"league":"","position":0,"played":0,"wins":0,"draws":0,"losses":0,"points":0,"recentForm":[]}`,
		teamID, prose, teamID,
	)

	var out api.TeamStandings
	if err := g.formatJSON(ctx, prompt, &out); err != nil {
		g.logger.Warn("groq standings json format failed, falling back", "team", teamID, "err", err)
		return g.fallback.TeamStandings(ctx, teamID)
	}
	out.TeamID = teamID
	out.Source = api.SourceGroq
	if out.Warning == "" {
		out.Warning = "live-web"
	}
	return out, nil
}

func (g *GroqSource) NextMatch(ctx context.Context, teamID string) (api.NextMatch, error) {
	prose, err := g.searchProse(ctx, fmt.Sprintf(
		"Next scheduled match for %s: opponent, date/time, competition, venue.",
		teamID,
	))
	if err != nil {
		g.logger.Warn("groq next-match prose failed, falling back", "team", teamID, "err", err)
		return g.fallback.NextMatch(ctx, teamID)
	}

	prompt := fmt.Sprintf(
		"Text about %s next match:\n%s\n\nReturn ONLY this JSON (ISO-8601 kickoff, empty strings for unknowns):\n"+
			`{"teamId":%q,"opponent":{"id":"","name":""},"kickoff":"","competition":"","venue":""}`,
		teamID, prose, teamID,
	)

	var out api.NextMatch
	if err := g.formatJSON(ctx, prompt, &out); err != nil {
		g.logger.Warn("groq next-match json format failed, falling back", "team", teamID, "err", err)
		return g.fallback.NextMatch(ctx, teamID)
	}
	out.TeamID = teamID
	out.Source = api.SourceGroq
	if out.Warning == "" {
		out.Warning = "live-web"
	}
	return out, nil
}

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
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '-' { // ASCII-only; non-Latin runes are silently dropped
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

// LeagueStandings fetches the full enriched standings table for a league
// via the 2-step Groq pipeline. The leagueID is canonicalized before use.
func (g *GroqSource) LeagueStandings(ctx context.Context, leagueID string) (api.LeagueStandings, error) {
	leagueID = canonicalizeLeagueID(leagueID)
	if leagueID == "" {
		return api.LeagueStandings{}, ErrNotFound
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
{"leagueId":%q,"name":"","season":"","table":[{"position":1,"teamId":"","teamName":"","played":0,"wins":0,"draws":0,"losses":0,"points":0,"recentForm":[]}]}`,
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

// AllTeamStandings discovers the team's leagues then fetches each table,
// extracts the team's row, and builds a TeamStandings per league.
// Returns a single-element slice (primary standings) on any discovery failure.
func (g *GroqSource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	leagueIDs, err := g.TeamLeagues(ctx, teamID)
	if err != nil || len(leagueIDs) == 0 { // TeamLeagues never returns non-nil err; guard is future-proof
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
		if strings.ToLower(table[i].TeamID) == lower {
			return &table[i]
		}
	}
	return nil
}

// LookupTeamByName delegates to the fallback — name resolution is
// handled by the alias table + OpenAI layer.
func (g *GroqSource) LookupTeamByName(ctx context.Context, name string) (*api.TeamLookup, error) {
	return g.fallback.LookupTeamByName(ctx, name)
}
