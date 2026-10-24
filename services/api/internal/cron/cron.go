// Package cron runs the daily poster-generation pipeline.
//
// The cron is opt-in: it only runs when POSTER_CRON_ENABLED=true.
// Inside the loop we fetch the cricket listings from streamed.pk,
// translate them into posters.Match values, and hand the batch to a
// posters.Generator that does the OpenAI calls + disk caching.
package cron

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/abindlish/streamed-backend/internal/posters"
)

// SportFilter narrows the streamed.pk fetch to cricket. Wired as a
// constant for now so a follow-up can extend to a slice of sports.
const SportFilter = "cricket"

// DefaultMatchesURL is the streamed.pk cricket endpoint.
const DefaultMatchesURL = "https://streamed.pk/api/matches/" + SportFilter

// DefaultInterval is how often the cron fires once the initial run
// completes. Override with the CRON_INTERVAL env var.
const DefaultInterval = 24 * time.Hour

// EnabledEnv is the env var that gates the entire cron. The cron is
// off unless this is exactly "true" (case-insensitive).
const EnabledEnv = "POSTER_CRON_ENABLED"

// IntervalEnv is the env var that overrides the loop interval. Parsed
// with time.ParseDuration ("24h", "30m", etc).
const IntervalEnv = "CRON_INTERVAL"

// Config tunes a Cron. All fields have sensible defaults and most
// callers only need to set Generator.
type Config struct {
	Generator  *posters.Generator
	MatchesURL string
	Interval   time.Duration
	HTTPClient *http.Client
	Logger     *slog.Logger
}

// Cron owns the goroutine that drives the daily pipeline. Use Start
// to launch and Trigger to kick off an extra run on demand.
type Cron struct {
	cfg     Config
	logger  *slog.Logger
	trigger chan struct{}

	mu      sync.Mutex
	running bool
}

// New constructs a Cron with defaults applied.
func New(cfg Config) *Cron {
	if cfg.MatchesURL == "" {
		cfg.MatchesURL = DefaultMatchesURL
	}
	if cfg.Interval <= 0 {
		cfg.Interval = DefaultInterval
	}
	if cfg.HTTPClient == nil {
		cfg.HTTPClient = &http.Client{Timeout: 20 * time.Second}
	}
	if cfg.Logger == nil {
		cfg.Logger = slog.Default()
	}
	return &Cron{
		cfg:     cfg,
		logger:  cfg.Logger,
		trigger: make(chan struct{}, 1),
	}
}

// Enabled returns true if POSTER_CRON_ENABLED is set to "true".
func Enabled() bool {
	return strings.EqualFold(strings.TrimSpace(os.Getenv(EnabledEnv)), "true")
}

// IntervalFromEnv returns the configured interval, falling back to
// DefaultInterval if CRON_INTERVAL is unset or unparseable.
func IntervalFromEnv() time.Duration {
	v := os.Getenv(IntervalEnv)
	if v == "" {
		return DefaultInterval
	}
	d, err := time.ParseDuration(v)
	if err != nil || d <= 0 {
		return DefaultInterval
	}
	return d
}

// Start launches the goroutine. It runs once immediately so we get
// signal on startup, then ticks every Interval. Stops when ctx is
// cancelled.
func (c *Cron) Start(ctx context.Context) {
	go c.loop(ctx)
}

// Trigger kicks an extra run if one isn't already pending. Safe to
// call at any time; non-blocking. Returns true if the trigger was
// queued, false if a trigger was already pending.
func (c *Cron) Trigger() bool {
	select {
	case c.trigger <- struct{}{}:
		return true
	default:
		return false
	}
}

func (c *Cron) loop(ctx context.Context) {
	c.logger.Info("poster cron starting", "interval", c.cfg.Interval, "url", c.cfg.MatchesURL)

	// Run once immediately so we can verify the wiring without
	// waiting a full interval.
	c.runOnce(ctx)

	ticker := time.NewTicker(c.cfg.Interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			c.logger.Info("poster cron stopped", "reason", ctx.Err())
			return
		case <-ticker.C:
			c.runOnce(ctx)
		case <-c.trigger:
			c.runOnce(ctx)
		}
	}
}

// runOnce performs one fetch + generate pass. Errors are logged, not
// propagated; the cron must keep running across transient failures.
func (c *Cron) runOnce(ctx context.Context) {
	// Don't overlap runs. If a previous tick is still in flight (e.g.
	// the OpenAI calls took longer than the interval) just skip this
	// one.
	c.mu.Lock()
	if c.running {
		c.mu.Unlock()
		c.logger.Info("poster cron run skipped: previous run still in progress")
		return
	}
	c.running = true
	c.mu.Unlock()
	defer func() {
		c.mu.Lock()
		c.running = false
		c.mu.Unlock()
	}()

	c.logger.Info("poster cron run started", "url", c.cfg.MatchesURL)
	start := time.Now()

	matches, err := c.fetchMatches(ctx)
	if err != nil {
		c.logger.Error("poster cron fetch failed", "err", err, "elapsedMs", time.Since(start).Milliseconds())
		return
	}

	res := c.cfg.Generator.Generate(ctx, matches)
	c.logger.Info("poster cron run finished",
		"found", res.Found,
		"generated", res.Generated,
		"skipped", res.Skipped,
		"failed", res.Failed,
		"elapsedMs", res.Elapsed.Milliseconds(),
	)
}

// apiTeam mirrors the streamed.pk team subobject we care about.
type apiTeam struct {
	Name  string `json:"name"`
	Badge string `json:"badge"`
}

// apiMatch is the subset of the streamed.pk match record we use here.
// The upstream payload has more fields (sources, poster, popular, etc)
// but we ignore them.
type apiMatch struct {
	ID       string `json:"id"`
	Title    string `json:"title"`
	Category string `json:"category"`
	Date     int64  `json:"date"`
	Teams    struct {
		Home apiTeam `json:"home"`
		Away apiTeam `json:"away"`
	} `json:"teams"`
}

// fetchMatches GETs the streamed.pk listing and returns it as a flat
// list of posters.Match values. Empty/invalid records are dropped here
// so the generator's skip counters only reflect real skips.
func (c *Cron) fetchMatches(ctx context.Context) ([]posters.Match, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.cfg.MatchesURL, nil)
	if err != nil {
		return nil, fmt.Errorf("build request: %w", err)
	}
	req.Header.Set("Accept", "application/json")
	resp, err := c.cfg.HTTPClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("http: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode/100 != 2 {
		snippet, _ := io.ReadAll(io.LimitReader(resp.Body, 256))
		return nil, fmt.Errorf("upstream status %d: %s", resp.StatusCode, strings.TrimSpace(string(snippet)))
	}

	var raw []apiMatch
	if err := json.NewDecoder(resp.Body).Decode(&raw); err != nil {
		return nil, fmt.Errorf("decode: %w", err)
	}

	out := make([]posters.Match, 0, len(raw))
	for _, m := range raw {
		home := strings.TrimSpace(m.Teams.Home.Name)
		away := strings.TrimSpace(m.Teams.Away.Name)
		id := strings.TrimSpace(m.ID)
		if id == "" || home == "" || away == "" {
			continue
		}
		out = append(out, posters.Match{
			ID:       id,
			HomeName: home,
			AwayName: away,
		})
	}
	return out, nil
}
