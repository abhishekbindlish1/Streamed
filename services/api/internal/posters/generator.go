// Package posters generates and serves AI-rendered poster art and team
// logos for the cricket listings shown on the TV app.
//
// The generator handles two artifact kinds, both cached on disk so
// each cron pass is idempotent:
//
//   - Posters — one PNG per match at data/posters/{matchId}.png,
//     served at GET /v1/posters/{matchId}.png.
//   - Logos — one PNG per team slug at data/logos/{teamSlug}.png,
//     served at GET /v1/teams/{teamSlug}/logo.png.
//
// Both kinds use a pluggable ImageClient. Two implementations ship in
// this package: PollinationsImageClient (free, no auth, default) and
// OpenAIImageClient (paid, requires OPENAI_API_KEY). Without an
// ImageClient configured the run is a no-op so the service degrades
// gracefully.
package posters

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"
)

// PosterPromptTemplate is the cinematic, text-free poster prompt used
// for every match. {{HOME}} and {{AWAY}} are replaced before sending.
const PosterPromptTemplate = "A cinematic dark sports poster for a cricket match between {{HOME}} and {{AWAY}}. Stadium under floodlights, dramatic lighting, deep red and navy blue palette, no text, no logos, photorealistic, 16:9 framing."

// LogoPromptTemplate is the prompt used for per-team crest images.
// {{TEAM}} is replaced with the team display name.
const LogoPromptTemplate = "Minimalist sports team crest for {{TEAM}}, vector style, single emblem centered, dark background, team identity colors, no text labels, professional broadcast logo, 1024x1024."

// Backwards-compat alias kept for any caller that still references the
// original constant name.
const PromptTemplate = PosterPromptTemplate

// DefaultMaxPerRun caps how many *new* generations one cron pass will
// produce (across posters + logos combined). Already-cached items do
// not count against the cap. At ~$0.04 per image on gpt-image-1 this
// bounds daily spend to roughly $0.80.
const DefaultMaxPerRun = 20

// DefaultConcurrency is the number of in-flight image requests. The
// OpenAI images endpoint is aggressively rate-limited; 4 keeps us well
// inside typical org limits.
const DefaultConcurrency = 4

const defaultModel = "gpt-image-1"
const imagesEndpoint = "https://api.openai.com/v1/images/generations"

// matchIDPattern is the allow-list used to sanitize a matchId before
// turning it into a filesystem path. Only letters, digits, dashes and
// dots; everything else is rejected. Prevents `..` traversal and any
// path-separator surprises.
var matchIDPattern = regexp.MustCompile(`^[A-Za-z0-9._-]+$`)

// SanitizeMatchID returns the input unchanged if it is a safe
// identifier, or an error otherwise. Used for both match ids and team
// slugs (the format is identical).
func SanitizeMatchID(id string) (string, error) {
	if id == "" {
		return "", errors.New("empty id")
	}
	if !matchIDPattern.MatchString(id) {
		return "", fmt.Errorf("invalid id: %q", id)
	}
	if strings.Contains(id, "..") {
		return "", fmt.Errorf("invalid id: %q", id)
	}
	return id, nil
}

// slugifyRe matches one or more non-alphanumeric runs.
var slugifyRe = regexp.MustCompile(`[^a-z0-9]+`)

// Slugify converts a free-text team name into a URL-safe slug:
// lowercase, non-alphanumeric runs collapsed to `-`, leading and
// trailing dashes trimmed. Returns "" for empty input or input that
// contains no alphanumerics.
//
//	Slugify("Northern XI")   == "northern-xi"
//	Slugify("Royal Challengers Bangalore") == "royal-challengers-bangalore"
//	Slugify("  ")            == ""
func Slugify(name string) string {
	s := strings.ToLower(strings.TrimSpace(name))
	if s == "" {
		return ""
	}
	s = slugifyRe.ReplaceAllString(s, "-")
	s = strings.Trim(s, "-")
	return s
}

// Match is the minimum subset of an upstream match record needed to
// produce poster + logo artwork.
type Match struct {
	ID       string
	HomeName string
	AwayName string
}

// Result describes one cron pass.
type Result struct {
	Found     int           // matches received from caller
	Generated int           // images successfully written this run
	Skipped   int           // already cached + unbuildable + over-cap
	Failed    int           // image client or write error
	Elapsed   time.Duration // wall-clock duration of the pass
}

// ImageClient abstracts the OpenAI call so tests can inject a fake.
type ImageClient interface {
	Generate(ctx context.Context, prompt string) ([]byte, error)
}

// PosterEntry is metadata for one cached match poster.
type PosterEntry struct {
	MatchID     string    `json:"matchId"`
	URL         string    `json:"url"`
	GeneratedAt time.Time `json:"generatedAt"`
	SizeBytes   int64     `json:"sizeBytes"`
}

// LogoEntry is metadata for one cached team logo.
type LogoEntry struct {
	TeamSlug    string    `json:"teamSlug"`
	TeamName    string    `json:"teamName"`
	URL         string    `json:"url"`
	GeneratedAt time.Time `json:"generatedAt"`
	SizeBytes   int64     `json:"sizeBytes"`
}

// Generator runs the asset pipeline and serves the cached files. Two
// stores are managed: one for match posters and one for team crests.
// Stores can be local-disk or GCS-backed (see store.go); the Generator
// itself is store-agnostic.
type Generator struct {
	posterStore Store
	logoStore   Store
	client      ImageClient
	logger      *slog.Logger
	maxPerRun   int
	concurrency int

	// teamNameBySlug tracks the last seen display name for each slug,
	// so the manifest can include a human-readable label even though
	// the on-disk filename is just the slug. Populated lazily by
	// Generate.
	mu             sync.Mutex
	teamNameBySlug map[string]string
}

// Option is a functional option for the Generator constructor.
type Option func(*Generator)

// WithMaxPerRun overrides DefaultMaxPerRun.
func WithMaxPerRun(n int) Option {
	return func(g *Generator) {
		if n > 0 {
			g.maxPerRun = n
		}
	}
}

// WithConcurrency overrides DefaultConcurrency.
func WithConcurrency(n int) Option {
	return func(g *Generator) {
		if n > 0 {
			g.concurrency = n
		}
	}
}

// NewGenerator constructs a generator that caches posters into
// posterDir and logos into logoDir using the provided ImageClient.
// Both directories are created on demand. logoDir may be empty, in
// which case logos are derived from posterDir's parent + "logos".
//
// Convenience wrapper around NewGeneratorWithStore that wires two
// localStore instances. Prefer NewGeneratorWithStore for production
// (Cloud Run + GCS) wiring.
func NewGenerator(posterDir, logoDir string, client ImageClient, logger *slog.Logger, opts ...Option) *Generator {
	if logoDir == "" {
		logoDir = filepath.Join(filepath.Dir(posterDir), "logos")
	}
	return NewGeneratorWithStore(NewLocalStore(posterDir), NewLocalStore(logoDir), client, logger, opts...)
}

// NewGeneratorWithStore is the explicit constructor — pass the poster
// and logo stores directly. Use this when wiring GCS (or any other
// non-local Store impl).
func NewGeneratorWithStore(posterStore, logoStore Store, client ImageClient, logger *slog.Logger, opts ...Option) *Generator {
	if logger == nil {
		logger = slog.Default()
	}
	g := &Generator{
		posterStore:    posterStore,
		logoStore:      logoStore,
		client:         client,
		logger:         logger,
		maxPerRun:      DefaultMaxPerRun,
		concurrency:    DefaultConcurrency,
		teamNameBySlug: map[string]string{},
	}
	for _, o := range opts {
		o(g)
	}
	g.preloadTeamNames()
	return g
}

// PosterStore exposes the underlying store so handlers can stream PNGs
// directly without going through the Generator. Same for LogoStore.
func (g *Generator) PosterStore() Store { return g.posterStore }
func (g *Generator) LogoStore() Store   { return g.logoStore }

// Describe returns a one-line summary of the wired stores. Used in
// startup logs so operators can see at a glance whether GCS or local
// disk is being used.
func (g *Generator) Describe() string {
	return "posters=" + g.posterStore.Describe() + " logos=" + g.logoStore.Describe()
}

// HasPoster reports whether a cached PNG exists for matchId.
func (g *Generator) HasPoster(matchID string) bool {
	return g.posterStore.Exists(context.Background(), matchID)
}

// HasLogo reports whether a cached PNG exists for teamSlug.
func (g *Generator) HasLogo(teamSlug string) bool {
	return g.logoStore.Exists(context.Background(), teamSlug)
}

// ListPosters returns metadata for every poster currently cached,
// sorted by matchId. Bad / unreadable files are skipped silently.
func (g *Generator) ListPosters() ([]PosterEntry, error) {
	objs, err := g.posterStore.List(context.Background())
	if err != nil {
		return nil, err
	}
	out := make([]PosterEntry, 0, len(objs))
	for _, o := range objs {
		out = append(out, PosterEntry{
			MatchID:     o.Key,
			URL:         "/v1/posters/" + o.Key + ".png",
			GeneratedAt: o.ModTime,
			SizeBytes:   o.Size,
		})
	}
	return out, nil
}

// ListLogos returns metadata for every logo currently cached, sorted
// by team slug. The team display name comes from the in-memory
// teamNameBySlug table populated during the most recent Generate call;
// after a process restart it falls back to a Title-Cased version of
// the slug.
func (g *Generator) ListLogos() ([]LogoEntry, error) {
	objs, err := g.logoStore.List(context.Background())
	if err != nil {
		return nil, err
	}
	out := make([]LogoEntry, 0, len(objs))
	g.mu.Lock()
	for _, o := range objs {
		name, ok := g.teamNameBySlug[o.Key]
		if !ok || name == "" {
			name = humanize(o.Key)
		}
		out = append(out, LogoEntry{
			TeamSlug:    o.Key,
			TeamName:    name,
			URL:         "/v1/teams/" + o.Key + "/logo.png",
			GeneratedAt: o.ModTime,
			SizeBytes:   o.Size,
		})
	}
	g.mu.Unlock()
	return out, nil
}

type rawDirEntry struct {
	id      string
	size    int64
	modTime time.Time
}

// humanize turns "northern-xi" into "Northern XI" — a best-effort
// fallback when the in-memory team name is unknown after a restart.
func humanize(slug string) string {
	if slug == "" {
		return ""
	}
	parts := strings.Split(slug, "-")
	for i, p := range parts {
		if p == "" {
			continue
		}
		// Common cricket-suffix uppercases.
		switch strings.ToLower(p) {
		case "xi", "fc", "uae", "psl", "ipl", "wc", "t20":
			parts[i] = strings.ToUpper(p)
		default:
			parts[i] = strings.ToUpper(p[:1]) + p[1:]
		}
	}
	return strings.Join(parts, " ")
}

// preloadTeamNames seeds the in-memory display-name table from any
// existing logo files known to the logo store so List* survives a
// process restart. We can't recover the exact original name from a
// slug, but humanize is a reasonable approximation.
func (g *Generator) preloadTeamNames() {
	objs, err := g.logoStore.List(context.Background())
	if err != nil {
		// Listing can fail before the bucket / dir exists. Not fatal —
		// teamNameBySlug will populate during the first cron pass.
		return
	}
	for _, o := range objs {
		g.teamNameBySlug[o.Key] = humanize(o.Key)
	}
}

// jobKind disambiguates the two asset types in the per-job work list.
type jobKind int

const (
	jobPoster jobKind = iota
	jobLogo
)

type job struct {
	kind     jobKind
	id       string // match id or team slug
	teamName string // for logos: original display name (so manifest looks nice)
	prompt   string
}

// Generate runs one pass: for every match, enqueue a poster job and
// up to two logo jobs (home + away). Skip if the cached file already
// exists. Honors maxPerRun and concurrency. Always returns a
// populated Result, even on partial failures.
func (g *Generator) Generate(ctx context.Context, matches []Match) Result {
	start := time.Now()
	res := Result{Found: len(matches)}

	if g.client == nil {
		g.logger.Warn("posters: no image client configured, skipping run")
		res.Skipped = len(matches)
		res.Elapsed = time.Since(start)
		return res
	}

	// Build the work list. Each match yields up to 3 candidate jobs
	// (poster + 2 logos). Already-cached jobs are filtered out so they
	// do not consume the maxPerRun budget. Logos are de-duplicated
	// across matches (Northern XI vs Coastal XI tomorrow + Northern XI
	// vs Birds XI next week shares the Northern XI logo).
	work := make([]job, 0, len(matches)*3)
	seenLogo := map[string]bool{}

	for _, m := range matches {
		// Poster ----------------------------------------------------------
		if _, err := SanitizeMatchID(m.ID); err != nil {
			res.Skipped++
		} else if strings.TrimSpace(m.HomeName) == "" || strings.TrimSpace(m.AwayName) == "" {
			res.Skipped++
		} else if g.HasPoster(m.ID) {
			res.Skipped++
		} else {
			work = append(work, job{
				kind:   jobPoster,
				id:     m.ID,
				prompt: buildPosterPrompt(m),
			})
		}

		// Logos -----------------------------------------------------------
		for _, name := range []string{m.HomeName, m.AwayName} {
			name = strings.TrimSpace(name)
			if name == "" {
				continue
			}
			slug := Slugify(name)
			if slug == "" {
				continue
			}
			// Always remember the display name so the manifest is nice
			// even if the file is already cached.
			g.mu.Lock()
			g.teamNameBySlug[slug] = name
			g.mu.Unlock()

			if seenLogo[slug] {
				continue
			}
			seenLogo[slug] = true
			if g.HasLogo(slug) {
				res.Skipped++
				continue
			}
			work = append(work, job{
				kind:     jobLogo,
				id:       slug,
				teamName: name,
				prompt:   buildLogoPrompt(name),
			})
		}
	}

	// Apply the per-run cap. Anything beyond it is treated as
	// "skipped this run" and will be picked up on the next pass.
	if len(work) > g.maxPerRun {
		res.Skipped += len(work) - g.maxPerRun
		work = work[:g.maxPerRun]
	}

	if len(work) == 0 {
		res.Elapsed = time.Since(start)
		return res
	}

	sem := make(chan struct{}, g.concurrency)
	var mu sync.Mutex
	var wg sync.WaitGroup

	for _, j := range work {
		if err := ctx.Err(); err != nil {
			mu.Lock()
			res.Failed += len(work) - (res.Generated + res.Failed)
			mu.Unlock()
			break
		}
		wg.Add(1)
		sem <- struct{}{}
		go func(j job) {
			defer wg.Done()
			defer func() { <-sem }()
			if err := g.runJob(ctx, j); err != nil {
				g.logger.Warn("posters: generate failed", "kind", kindName(j.kind), "id", j.id, "err", err)
				mu.Lock()
				res.Failed++
				mu.Unlock()
				return
			}
			mu.Lock()
			res.Generated++
			mu.Unlock()
		}(j)
	}
	wg.Wait()

	res.Elapsed = time.Since(start)
	return res
}

func kindName(k jobKind) string {
	if k == jobLogo {
		return "logo"
	}
	return "poster"
}

func (g *Generator) runJob(ctx context.Context, j job) error {
	png, err := g.client.Generate(ctx, j.prompt)
	if err != nil {
		return err
	}
	if len(png) == 0 {
		return errors.New("empty image bytes")
	}
	store := g.posterStore
	if j.kind == jobLogo {
		store = g.logoStore
	}
	return store.Write(ctx, j.id, png)
}

func buildPosterPrompt(m Match) string {
	p := strings.ReplaceAll(PosterPromptTemplate, "{{HOME}}", m.HomeName)
	p = strings.ReplaceAll(p, "{{AWAY}}", m.AwayName)
	return p
}

func buildLogoPrompt(teamName string) string {
	return strings.ReplaceAll(LogoPromptTemplate, "{{TEAM}}", teamName)
}

// OpenAIImageClient is the production ImageClient that talks to
// OpenAI's /v1/images/generations endpoint.
type OpenAIImageClient struct {
	apiKey string
	model  string
	client *http.Client
}

// NewOpenAIImageClient returns a client. apiKey is read at the call
// site from os.Getenv; never hardcode and never log it. model defaults
// to gpt-image-1 if empty.
func NewOpenAIImageClient(apiKey, model string) *OpenAIImageClient {
	if model == "" {
		model = defaultModel
	}
	return &OpenAIImageClient{
		apiKey: apiKey,
		model:  model,
		// Image generation is slow; allow up to 90s per request.
		client: &http.Client{Timeout: 90 * time.Second},
	}
}

type imageReq struct {
	Model          string `json:"model"`
	Prompt         string `json:"prompt"`
	Size           string `json:"size"`
	N              int    `json:"n"`
	ResponseFormat string `json:"response_format,omitempty"`
}

type imageResp struct {
	Data []struct {
		B64 string `json:"b64_json"`
	} `json:"data"`
}

// Generate posts a single-image request and returns the decoded PNG
// bytes. Returns an error on any non-2xx response or decode failure.
func (c *OpenAIImageClient) Generate(ctx context.Context, prompt string) ([]byte, error) {
	body := imageReq{
		Model:  c.model,
		Prompt: prompt,
		Size:   "1024x1024",
		N:      1,
	}
	// gpt-image-1 always returns b64_json. For dall-e-3 we still want
	// b64 so we ask for it explicitly; harmless on gpt-image-1.
	if c.model != defaultModel {
		body.ResponseFormat = "b64_json"
	}
	buf, err := json.Marshal(body)
	if err != nil {
		return nil, fmt.Errorf("marshal: %w", err)
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, imagesEndpoint, bytes.NewReader(buf))
	if err != nil {
		return nil, fmt.Errorf("build request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+c.apiKey)

	resp, err := c.client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("http: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode/100 != 2 {
		snippet, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
		return nil, fmt.Errorf("openai images status %d: %s", resp.StatusCode, strings.TrimSpace(string(snippet)))
	}

	var parsed imageResp
	if err := json.NewDecoder(resp.Body).Decode(&parsed); err != nil {
		return nil, fmt.Errorf("decode: %w", err)
	}
	if len(parsed.Data) == 0 || parsed.Data[0].B64 == "" {
		return nil, errors.New("openai images: no image in response")
	}
	png, err := base64.StdEncoding.DecodeString(parsed.Data[0].B64)
	if err != nil {
		return nil, fmt.Errorf("decode b64: %w", err)
	}
	return png, nil
}
