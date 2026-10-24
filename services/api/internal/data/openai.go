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

// OpenAISource asks an OpenAI-COMPATIBLE chat-completions model to
// fabricate plausible-looking sports payloads. Despite the name it is
// NOT OpenAI-specific — Groq, Together, Anyscale, vLLM, llama.cpp,
// and any other server that implements the /v1/chat/completions wire
// shape can be plugged in by setting baseURL on construction.
//
// Use cases:
//   * OpenAI:  baseURL = "" (defaults to https://api.openai.com/v1)
//   * Groq:    baseURL = "https://api.groq.com/openai/v1"
//
// The model does NOT have live sports data, so every response is
// annotated with Warning="estimated" and Source set to api.SourceOpenAI.
//
// On any failure (network error, bad JSON, missing key), the source
// transparently falls back to the wrapped StubSource so the API never
// 500s due to an LLM hiccup.
type OpenAISource struct {
	apiKey   string
	model    string
	endpoint string // full URL to chat/completions
	client   *http.Client
	fallback Source
	logger   *slog.Logger
}

// NewOpenAISource constructs an OpenAI-COMPATIBLE Source. apiKey must
// come from os.Getenv at the call site — never hardcode it. The key
// is held only in memory and is never logged.
//
// baseURL points at the API root (everything before /chat/completions).
// Empty string defaults to OpenAI's hosted endpoint. Set to
// "https://api.groq.com/openai/v1" to route through Groq, etc. The
// expected wire shape is the OpenAI chat-completions request/response
// format; servers that don't implement it (e.g. Anthropic's native
// /v1/messages) are NOT supported here — they need their own Source.
func NewOpenAISource(apiKey, model, baseURL string, fallback Source, logger *slog.Logger) *OpenAISource {
	if model == "" {
		model = "gpt-4o-mini"
	}
	if logger == nil {
		logger = slog.Default()
	}
	if baseURL == "" {
		baseURL = "https://api.openai.com/v1"
	}
	endpoint := strings.TrimRight(baseURL, "/") + "/chat/completions"
	return &OpenAISource{
		apiKey:   apiKey,
		model:    model,
		endpoint: endpoint,
		client:   &http.Client{Timeout: 20 * time.Second},
		fallback: fallback,
		logger:   logger,
	}
}

func (o *OpenAISource) Name() string { return api.SourceOpenAI }

const systemPrompt = `You are a sports data assistant. Reply with ONLY valid JSON matching the schema the user requests — no prose, no markdown fences. If you do not know real values, return reasonable plausible defaults and include "warning": "estimated" in the JSON.`

// chatCompletion is a minimal subset of the OpenAI request body.
type chatCompletion struct {
	Model    string      `json:"model"`
	Messages []chatMsg   `json:"messages"`
	Response *respFormat `json:"response_format,omitempty"`
}

type chatMsg struct {
	Role    string `json:"role"`
	Content string `json:"content"`
}

type respFormat struct {
	Type string `json:"type"`
}

type chatResponse struct {
	Choices []struct {
		Message chatMsg `json:"message"`
	} `json:"choices"`
}

// callJSON posts a chat-completions request asking the model to reply
// with strict JSON, then unmarshals the assistant's content into out.
// Returns an error if the network call fails, the model returns no
// choices, or its content is not valid JSON.
func (o *OpenAISource) callJSON(ctx context.Context, userPrompt string, out any) error {
	body := chatCompletion{
		Model: o.model,
		Messages: []chatMsg{
			{Role: "system", Content: systemPrompt},
			{Role: "user", Content: userPrompt},
		},
		Response: &respFormat{Type: "json_object"},
	}
	buf, err := json.Marshal(body)
	if err != nil {
		return fmt.Errorf("marshal request: %w", err)
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, o.endpoint, bytes.NewReader(buf))
	if err != nil {
		return fmt.Errorf("build request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")
	// Authorization carries the secret. It is only set on the outbound
	// request and is never logged.
	req.Header.Set("Authorization", "Bearer "+o.apiKey)

	resp, err := o.client.Do(req)
	if err != nil {
		return fmt.Errorf("openai http: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode/100 != 2 {
		// Drain a small amount of the body for debug context but do
		// NOT echo the request, which contained the bearer token.
		snippet, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
		return fmt.Errorf("openai status %d: %s", resp.StatusCode, strings.TrimSpace(string(snippet)))
	}

	var parsed chatResponse
	if err := json.NewDecoder(resp.Body).Decode(&parsed); err != nil {
		return fmt.Errorf("decode wrapper: %w", err)
	}
	if len(parsed.Choices) == 0 {
		return fmt.Errorf("openai returned no choices")
	}
	content := parsed.Choices[0].Message.Content
	if err := json.Unmarshal([]byte(content), out); err != nil {
		return fmt.Errorf("decode model json: %w", err)
	}
	return nil
}

func (o *OpenAISource) TeamStandings(ctx context.Context, teamID string) (api.TeamStandings, error) {
	prompt := fmt.Sprintf(`Provide current league standings for the football team with id "%s" as JSON with this exact shape:
{"teamId": string, "league": string, "position": int, "played": int, "wins": int, "draws": int, "losses": int, "points": int, "recentForm": ["W"|"L"|"D", ...5 entries]}`, teamID)

	var out api.TeamStandings
	if err := o.callJSON(ctx, prompt, &out); err != nil {
		o.logger.Warn("openai team standings failed, falling back", "team", teamID, "err", err)
		return o.fallback.TeamStandings(ctx, teamID)
	}
	out.TeamID = teamID
	out.Source = api.SourceOpenAI
	if out.Warning == "" {
		out.Warning = "estimated"
	}
	return out, nil
}

func (o *OpenAISource) NextMatch(ctx context.Context, teamID string) (api.NextMatch, error) {
	prompt := fmt.Sprintf(`Provide the next scheduled match for football team id "%s" as JSON with this exact shape:
{"teamId": string, "opponent": {"id": string, "name": string}, "kickoff": ISO8601 string, "competition": string, "venue": string}`, teamID)

	var out api.NextMatch
	if err := o.callJSON(ctx, prompt, &out); err != nil {
		o.logger.Warn("openai next match failed, falling back", "team", teamID, "err", err)
		return o.fallback.NextMatch(ctx, teamID)
	}
	out.TeamID = teamID
	out.Source = api.SourceOpenAI
	if out.Warning == "" {
		out.Warning = "estimated"
	}
	return out, nil
}

func (o *OpenAISource) LeagueStandings(ctx context.Context, leagueID string) (api.LeagueStandings, error) {
	prompt := fmt.Sprintf(`Provide the current standings table for football league id "%s" as JSON with this exact shape:
{"leagueId": string, "name": string, "season": string, "table": [{"position": int, "teamId": string, "teamName": string, "played": int, "points": int}]}`, leagueID)

	var out api.LeagueStandings
	if err := o.callJSON(ctx, prompt, &out); err != nil {
		o.logger.Warn("openai league standings failed, falling back", "league", leagueID, "err", err)
		return o.fallback.LeagueStandings(ctx, leagueID)
	}
	out.LeagueID = leagueID
	out.Source = api.SourceOpenAI
	if out.Warning == "" {
		out.Warning = "estimated"
	}
	return out, nil
}

// LookupTeamByName asks the model to map a free-text team name onto a
// kebab-case canonical id, returning league context too. We only land
// here after the alias table has missed (see data.LookupTeamByName), so
// every call here is an actual remote hit. On any failure (network,
// bad JSON, empty fields) we delegate to the fallback Source — for the
// stub that means ErrNotFound, which the handler translates into 404.
func (o *OpenAISource) LookupTeamByName(ctx context.Context, name string) (*api.TeamLookup, error) {
	prompt := fmt.Sprintf(`Given the sports team name %q, return JSON describing the canonical team it refers to:
{"teamId": kebab-case slug string, "canonicalName": string, "league": string}
Reply ONLY with that JSON. If you cannot identify the team, return {"teamId": "", "canonicalName": "", "league": ""}.`, name)

	var raw struct {
		TeamID        string `json:"teamId"`
		CanonicalName string `json:"canonicalName"`
		League        string `json:"league"`
	}
	if err := o.callJSON(ctx, prompt, &raw); err != nil {
		o.logger.Warn("openai team lookup failed, falling back", "name", name, "err", err)
		return o.fallback.LookupTeamByName(ctx, name)
	}
	if strings.TrimSpace(raw.TeamID) == "" || strings.TrimSpace(raw.CanonicalName) == "" {
		// Model returned an explicit "I don't know" payload. Surface a
		// 404 to the caller via ErrNotFound rather than echoing empty
		// strings.
		return nil, ErrNotFound
	}
	return &api.TeamLookup{
		Query:         name,
		TeamID:        raw.TeamID,
		CanonicalName: raw.CanonicalName,
		League:        raw.League,
		Confidence:    0.7,
		Source:        api.SourceOpenAILookup,
	}, nil
}

func (o *OpenAISource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	return o.fallback.TeamLeagues(ctx, teamID)
}

func (o *OpenAISource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	return o.fallback.AllTeamStandings(ctx, teamID)
}
