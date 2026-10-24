package posters

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
)

// fakeClient is a deterministic in-memory ImageClient for tests. It
// counts calls so dedupe behavior can be asserted, returns a fixed PNG
// signature, and lets a test override the response (e.g. to inject an
// error).
type fakeClient struct {
	calls   atomic.Int32
	prompts []string
	mu      sync.Mutex
	respond func(prompt string) ([]byte, error)
}

func (f *fakeClient) Generate(_ context.Context, prompt string) ([]byte, error) {
	f.calls.Add(1)
	f.mu.Lock()
	f.prompts = append(f.prompts, prompt)
	f.mu.Unlock()
	if f.respond != nil {
		return f.respond(prompt)
	}
	// 8-byte PNG magic header is enough for tests; we never decode.
	return []byte{0x89, 'P', 'N', 'G', '\r', '\n', 0x1a, '\n'}, nil
}

func newGenForTest(t *testing.T, client ImageClient, opts ...Option) *Generator {
	t.Helper()
	root := t.TempDir()
	return NewGenerator(
		filepath.Join(root, "posters"),
		filepath.Join(root, "logos"),
		client,
		slog.New(slog.NewTextHandler(io.Discard, nil)),
		opts...,
	)
}

// --- Slugify ---------------------------------------------------------

func TestSlugify(t *testing.T) {
	cases := []struct {
		in, want string
	}{
		{"Northern XI", "northern-xi"},
		{"Royal Challengers Bangalore", "royal-challengers-bangalore"},
		{"   Coastal   XI   ", "coastal-xi"},
		{"Mumbai_Indians", "mumbai-indians"},
		{"Mumbai/Indians!!", "mumbai-indians"},
		{"--Lions--", "lions"},
		{"", ""},
		{"   ", ""},
		{"!@#$%^", ""},
		{"team-1", "team-1"},
	}
	for _, c := range cases {
		got := Slugify(c.in)
		if got != c.want {
			t.Errorf("Slugify(%q) = %q, want %q", c.in, got, c.want)
		}
	}
}

// --- SanitizeMatchID -------------------------------------------------

func TestSanitizeMatchID(t *testing.T) {
	good := []string{"abc", "abc-123", "match.png-abc", "A1.B2.C3"}
	for _, id := range good {
		if _, err := SanitizeMatchID(id); err != nil {
			t.Errorf("SanitizeMatchID(%q) unexpected err: %v", id, err)
		}
	}
	bad := []string{"", "../etc/passwd", "a/b", "a..b", "name with space", "ünicode"}
	for _, id := range bad {
		if _, err := SanitizeMatchID(id); err == nil {
			t.Errorf("SanitizeMatchID(%q) should reject", id)
		}
	}
}

// --- Generate: happy path + dedupe -----------------------------------

func TestGenerate_PostersAndLogos(t *testing.T) {
	c := &fakeClient{}
	g := newGenForTest(t, c)

	matches := []Match{
		{ID: "m1", HomeName: "Northern XI", AwayName: "Coastal XI"},
		{ID: "m2", HomeName: "Northern XI", AwayName: "Birds XI"},
	}
	res := g.Generate(context.Background(), matches)

	// Expected calls: 2 posters + 3 unique logos (Northern XI dedup'd
	// across matches) = 5.
	if got, want := int(c.calls.Load()), 5; got != want {
		t.Fatalf("openai calls = %d, want %d", got, want)
	}
	if res.Generated != 5 {
		t.Errorf("Generated = %d, want 5; res=%+v", res.Generated, res)
	}
	if res.Failed != 0 {
		t.Errorf("Failed = %d, want 0", res.Failed)
	}

	// Files should exist.
	if !g.HasPoster("m1") || !g.HasPoster("m2") {
		t.Error("expected both posters cached")
	}
	for _, slug := range []string{"northern-xi", "coastal-xi", "birds-xi"} {
		if !g.HasLogo(slug) {
			t.Errorf("expected logo for %s cached", slug)
		}
	}
}

func TestGenerate_DedupeOnRerun(t *testing.T) {
	c := &fakeClient{}
	g := newGenForTest(t, c)

	matches := []Match{
		{ID: "m1", HomeName: "Northern XI", AwayName: "Coastal XI"},
	}
	g.Generate(context.Background(), matches)
	first := c.calls.Load()
	if first == 0 {
		t.Fatalf("first run made no calls")
	}

	// Second run with identical input must make zero new calls.
	res := g.Generate(context.Background(), matches)
	if got := c.calls.Load() - first; got != 0 {
		t.Fatalf("second run made %d additional calls, want 0", got)
	}
	if res.Generated != 0 {
		t.Errorf("Generated on rerun = %d, want 0", res.Generated)
	}
	// 1 poster + 2 logos = 3 cache hits should land in Skipped.
	if res.Skipped != 3 {
		t.Errorf("Skipped on rerun = %d, want 3", res.Skipped)
	}
}

func TestGenerate_RespectsCap(t *testing.T) {
	c := &fakeClient{}
	g := newGenForTest(t, c, WithMaxPerRun(2), WithConcurrency(1))

	matches := []Match{
		{ID: "m1", HomeName: "A", AwayName: "B"},
		{ID: "m2", HomeName: "C", AwayName: "D"},
		{ID: "m3", HomeName: "E", AwayName: "F"},
	}
	// Each match: 1 poster + 2 logos = 3 jobs. 3 matches = 9 jobs.
	// Cap of 2 means only 2 calls land.
	res := g.Generate(context.Background(), matches)
	if got, want := int(c.calls.Load()), 2; got != want {
		t.Fatalf("calls = %d, want %d (cap)", got, want)
	}
	if res.Generated != 2 {
		t.Errorf("Generated = %d, want 2", res.Generated)
	}
	if res.Skipped == 0 {
		t.Errorf("Skipped should account for over-cap items, got %d", res.Skipped)
	}
}

func TestGenerate_SkipsBadInputs(t *testing.T) {
	c := &fakeClient{}
	g := newGenForTest(t, c)

	matches := []Match{
		{ID: "", HomeName: "A", AwayName: "B"},          // empty id
		{ID: "ok", HomeName: "", AwayName: "B"},         // empty home
		{ID: "ok2", HomeName: "A", AwayName: ""},        // empty away
		{ID: "../bad", HomeName: "A", AwayName: "B"},    // unsafe id
		{ID: "real", HomeName: "Lions", AwayName: "Tigers"}, // good
	}
	res := g.Generate(context.Background(), matches)
	if res.Generated == 0 {
		t.Errorf("expected at least one generation for the good match")
	}
	// The good match still produces logos for both teams.
	if !g.HasLogo("lions") || !g.HasLogo("tigers") {
		t.Errorf("expected lions+tigers logos cached")
	}
}

func TestGenerate_NoClient(t *testing.T) {
	g := newGenForTest(t, nil)
	res := g.Generate(context.Background(), []Match{{ID: "m1", HomeName: "A", AwayName: "B"}})
	if res.Generated != 0 {
		t.Errorf("Generated = %d, want 0 when client is nil", res.Generated)
	}
}

func TestGenerate_PartialFailures(t *testing.T) {
	c := &fakeClient{respond: func(prompt string) ([]byte, error) {
		// Fail any logo prompt; succeed on posters.
		if contains(prompt, "Minimalist sports team crest") {
			return nil, errors.New("boom")
		}
		return []byte{0x89, 'P', 'N', 'G'}, nil
	}}
	g := newGenForTest(t, c)
	res := g.Generate(context.Background(), []Match{
		{ID: "m1", HomeName: "Lions", AwayName: "Tigers"},
	})
	if res.Generated != 1 {
		t.Errorf("Generated = %d, want 1", res.Generated)
	}
	if res.Failed != 2 {
		t.Errorf("Failed = %d, want 2 (both logos)", res.Failed)
	}
}

// --- List* + manifest semantics --------------------------------------

func TestListPostersAndLogos(t *testing.T) {
	c := &fakeClient{}
	g := newGenForTest(t, c)

	g.Generate(context.Background(), []Match{
		{ID: "m1", HomeName: "Northern XI", AwayName: "Coastal XI"},
	})

	posters, err := g.ListPosters()
	if err != nil {
		t.Fatalf("ListPosters: %v", err)
	}
	if len(posters) != 1 || posters[0].MatchID != "m1" {
		t.Errorf("posters = %+v", posters)
	}
	if posters[0].URL != "/v1/posters/m1.png" {
		t.Errorf("poster url = %q", posters[0].URL)
	}

	logos, err := g.ListLogos()
	if err != nil {
		t.Fatalf("ListLogos: %v", err)
	}
	if len(logos) != 2 {
		t.Fatalf("logos len = %d, want 2", len(logos))
	}
	bySlug := map[string]LogoEntry{}
	for _, l := range logos {
		bySlug[l.TeamSlug] = l
	}
	if l, ok := bySlug["northern-xi"]; !ok || l.TeamName != "Northern XI" {
		t.Errorf("northern-xi entry = %+v", l)
	}
	if l, ok := bySlug["coastal-xi"]; !ok {
		t.Errorf("missing coastal-xi entry: %+v", logos)
	} else if l.URL != "/v1/teams/coastal-xi/logo.png" {
		t.Errorf("coastal-xi url = %q", l.URL)
	}
}

func TestListEmpty(t *testing.T) {
	g := newGenForTest(t, &fakeClient{})
	posters, err := g.ListPosters()
	if err != nil {
		t.Fatalf("ListPosters: %v", err)
	}
	if len(posters) != 0 {
		t.Errorf("posters not empty: %+v", posters)
	}
	logos, err := g.ListLogos()
	if err != nil {
		t.Fatalf("ListLogos: %v", err)
	}
	if len(logos) != 0 {
		t.Errorf("logos not empty: %+v", logos)
	}
}

func contains(s, sub string) bool {
	return len(s) >= len(sub) && indexOf(s, sub) >= 0
}

func indexOf(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}
