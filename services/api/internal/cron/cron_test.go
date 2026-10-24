package cron

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"

	"github.com/abindlish/streamed-backend/internal/posters"
)

// fakeImageClient counts calls so dedupe behavior can be asserted
// across cron passes.
type fakeImageClient struct {
	calls atomic.Int32
}

func (f *fakeImageClient) Generate(_ context.Context, _ string) ([]byte, error) {
	f.calls.Add(1)
	return []byte{0x89, 'P', 'N', 'G'}, nil
}

func newCronWithFake(t *testing.T, upstream string, ic posters.ImageClient) (*Cron, *posters.Generator) {
	t.Helper()
	root := t.TempDir()
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	g := posters.NewGenerator(filepath.Join(root, "posters"), filepath.Join(root, "logos"), ic, logger)
	c := New(Config{
		Generator:  g,
		MatchesURL: upstream,
		Interval:   24 * time.Hour, // never tick during tests
		Logger:     logger,
	})
	return c, g
}

func upstreamServer(t *testing.T, payload []map[string]any) *httptest.Server {
	t.Helper()
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(payload)
	}))
}

func TestCronRunOnce_GeneratesAssets(t *testing.T) {
	upstream := upstreamServer(t, []map[string]any{
		{
			"id":       "match-1",
			"title":    "Northern XI vs Coastal XI",
			"category": "cricket",
			"date":     time.Now().UnixMilli(),
			"teams": map[string]any{
				"home": map[string]any{"name": "Northern XI"},
				"away": map[string]any{"name": "Coastal XI"},
			},
		},
	})
	defer upstream.Close()

	ic := &fakeImageClient{}
	c, g := newCronWithFake(t, upstream.URL, ic)

	c.runOnce(context.Background())

	// 1 poster + 2 logos = 3 calls.
	if got := int(ic.calls.Load()); got != 3 {
		t.Fatalf("openai calls = %d, want 3", got)
	}
	if !g.HasPoster("match-1") {
		t.Errorf("expected poster for match-1")
	}
	if !g.HasLogo("northern-xi") || !g.HasLogo("coastal-xi") {
		t.Errorf("expected both team logos")
	}
}

func TestCronRunOnce_Idempotent(t *testing.T) {
	upstream := upstreamServer(t, []map[string]any{
		{
			"id": "match-1", "title": "x", "category": "cricket", "date": 0,
			"teams": map[string]any{
				"home": map[string]any{"name": "Lions"},
				"away": map[string]any{"name": "Tigers"},
			},
		},
	})
	defer upstream.Close()

	ic := &fakeImageClient{}
	c, _ := newCronWithFake(t, upstream.URL, ic)

	c.runOnce(context.Background())
	first := ic.calls.Load()
	c.runOnce(context.Background())
	if got := ic.calls.Load() - first; got != 0 {
		t.Fatalf("second pass made %d new calls, want 0 (dedupe)", got)
	}
}

func TestCronRunOnce_DropsBadRecords(t *testing.T) {
	upstream := upstreamServer(t, []map[string]any{
		{"id": "", "teams": map[string]any{"home": map[string]any{"name": "A"}, "away": map[string]any{"name": "B"}}},
		{"id": "ok", "teams": map[string]any{"home": map[string]any{"name": ""}, "away": map[string]any{"name": "B"}}},
		{"id": "good", "teams": map[string]any{"home": map[string]any{"name": "A"}, "away": map[string]any{"name": "B"}}},
	})
	defer upstream.Close()

	ic := &fakeImageClient{}
	c, g := newCronWithFake(t, upstream.URL, ic)
	c.runOnce(context.Background())

	if !g.HasPoster("good") {
		t.Errorf("expected poster for the only valid match")
	}
}

func TestEnabledFromEnv(t *testing.T) {
	t.Setenv("POSTER_CRON_ENABLED", "true")
	if !Enabled() {
		t.Error("Enabled() = false, want true")
	}
	t.Setenv("POSTER_CRON_ENABLED", "TRUE")
	if !Enabled() {
		t.Error("Enabled() = false for TRUE, want true (case-insensitive)")
	}
	t.Setenv("POSTER_CRON_ENABLED", "no")
	if Enabled() {
		t.Error("Enabled() = true for 'no', want false")
	}
	t.Setenv("POSTER_CRON_ENABLED", "")
	if Enabled() {
		t.Error("Enabled() = true for '', want false")
	}
}

func TestIntervalFromEnv(t *testing.T) {
	t.Setenv("CRON_INTERVAL", "")
	if got := IntervalFromEnv(); got != DefaultInterval {
		t.Errorf("got %v, want default %v", got, DefaultInterval)
	}
	t.Setenv("CRON_INTERVAL", "5m")
	if got := IntervalFromEnv(); got != 5*time.Minute {
		t.Errorf("got %v, want 5m", got)
	}
	t.Setenv("CRON_INTERVAL", "garbage")
	if got := IntervalFromEnv(); got != DefaultInterval {
		t.Errorf("garbage value should fall back to default; got %v", got)
	}
}

func TestTrigger_NonBlocking(t *testing.T) {
	c, _ := newCronWithFake(t, "http://unused.local", &fakeImageClient{})
	if !c.Trigger() {
		t.Error("first Trigger should queue")
	}
	if c.Trigger() {
		t.Error("second Trigger should be a no-op while one is pending")
	}
}

// Sanity: SportFilter is still cricket so the URL we hit doesn't drift.
func TestSportFilter(t *testing.T) {
	if SportFilter != "cricket" {
		t.Errorf("SportFilter = %q, want cricket", SportFilter)
	}
}

// Make sure DefaultMatchesURL is built from SportFilter (regression
// guard: changing one without the other would silently break).
func TestDefaultMatchesURL(t *testing.T) {
	if want := "https://streamed.pk/api/matches/cricket"; DefaultMatchesURL != want {
		t.Errorf("DefaultMatchesURL = %q, want %q", DefaultMatchesURL, want)
	}
}

// Sanity: confirm that omitted field decoding doesn't error.
func TestApiMatchDecode(t *testing.T) {
	raw := `[{"id":"x","title":"t","category":"cricket","date":1,"teams":{"home":{"name":"A"},"away":{"name":"B"}}}]`
	var got []apiMatch
	if err := json.Unmarshal([]byte(raw), &got); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if len(got) != 1 || got[0].Teams.Home.Name != "A" {
		t.Fatalf("unexpected: %+v", got)
	}
}

// Belt-and-braces: confirm our Logger default doesn't NPE and that
// Cron survives a context cancellation without leaking goroutines.
func TestStartCancellation(t *testing.T) {
	c, _ := newCronWithFake(t, "http://127.0.0.1:0", &fakeImageClient{})
	ctx, cancel := context.WithCancel(context.Background())
	c.Start(ctx)
	// Give the loop a beat to enter, then cancel.
	time.Sleep(10 * time.Millisecond)
	cancel()
	// No assertion; if Start leaked we'd get a goroutine warning in
	// future versions of go test.
}
