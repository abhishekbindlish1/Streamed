package server

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"

	"github.com/abindlish/streamed-backend/internal/api"
	"github.com/abindlish/streamed-backend/internal/data"
	"github.com/abindlish/streamed-backend/internal/posters"
)

type fakeClient struct{}

func (fakeClient) Generate(_ context.Context, _ string) ([]byte, error) {
	return []byte{0x89, 'P', 'N', 'G', '\r', '\n', 0x1a, '\n'}, nil
}

type noopTrigger struct{ queued bool }

func (n *noopTrigger) Trigger() bool {
	q := n.queued
	n.queued = true
	return !q
}

func newServerWithPosters(t *testing.T) (*httptest.Server, *posters.Generator, *noopTrigger) {
	t.Helper()
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	root := t.TempDir()
	gen := posters.NewGenerator(filepath.Join(root, "posters"), filepath.Join(root, "logos"), fakeClient{}, logger)
	// Pre-populate so the manifest has something to return.
	gen.Generate(context.Background(), []posters.Match{
		{ID: "m1", HomeName: "Northern XI", AwayName: "Coastal XI"},
	})
	tr := &noopTrigger{}
	srv := New(data.NewStubSource(), logger).WithPosters(gen, tr)
	return httptest.NewServer(srv.Handler()), gen, tr
}

func TestAssetsManifest_Shape(t *testing.T) {
	ts, _, _ := newServerWithPosters(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/posters")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d", resp.StatusCode)
	}
	var got api.AssetsManifest
	if err := json.NewDecoder(resp.Body).Decode(&got); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if len(got.Posters) != 1 || got.Posters[0].MatchID != "m1" {
		t.Errorf("posters = %+v", got.Posters)
	}
	if len(got.Logos) != 2 {
		t.Fatalf("logos len = %d, want 2", len(got.Logos))
	}
	// Logos should round-trip the human-readable team name.
	wantNames := map[string]string{
		"northern-xi": "Northern XI",
		"coastal-xi":  "Coastal XI",
	}
	for _, l := range got.Logos {
		if wantNames[l.TeamSlug] != l.TeamName {
			t.Errorf("logo %s name = %q, want %q", l.TeamSlug, l.TeamName, wantNames[l.TeamSlug])
		}
		if l.URL != "/v1/teams/"+l.TeamSlug+"/logo.png" {
			t.Errorf("logo url = %q", l.URL)
		}
	}
}

func TestPosterImageServed(t *testing.T) {
	ts, _, _ := newServerWithPosters(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/posters/m1.png")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d", resp.StatusCode)
	}
	if got := resp.Header.Get("Content-Type"); got != "image/png" {
		t.Errorf("content-type = %q", got)
	}
	if got := resp.Header.Get("Cache-Control"); got != "public, max-age=86400" {
		t.Errorf("cache-control = %q", got)
	}
	body, _ := io.ReadAll(resp.Body)
	if len(body) == 0 {
		t.Errorf("empty body")
	}
}

func TestPosterImageNotFound(t *testing.T) {
	ts, _, _ := newServerWithPosters(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/posters/missing.png")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusNotFound {
		t.Errorf("status = %d, want 404", resp.StatusCode)
	}
}

func TestPosterImagePathTraversalRejected(t *testing.T) {
	ts, _, _ := newServerWithPosters(t)
	defer ts.Close()

	// %2F prevents the router from splitting the path. The handler
	// should refuse the matchId outright.
	resp, err := http.Get(ts.URL + "/v1/posters/" + "..%2Fetc%2Fpasswd.png")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusOK {
		t.Errorf("traversal accepted, status = %d", resp.StatusCode)
	}
}

func TestTeamLogoServed(t *testing.T) {
	ts, _, _ := newServerWithPosters(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/teams/northern-xi/logo.png")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d", resp.StatusCode)
	}
	if got := resp.Header.Get("Content-Type"); got != "image/png" {
		t.Errorf("content-type = %q", got)
	}
}

func TestRefreshTriggersOnce(t *testing.T) {
	ts, _, tr := newServerWithPosters(t)
	defer ts.Close()

	resp, err := http.Post(ts.URL+"/v1/posters/refresh", "application/json", nil)
	if err != nil {
		t.Fatalf("post: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusAccepted {
		t.Errorf("status = %d, want 202", resp.StatusCode)
	}
	if !tr.queued {
		t.Errorf("trigger was not invoked")
	}

	// Second call: trigger already pending → status "already-running".
	resp2, err := http.Post(ts.URL+"/v1/posters/refresh", "application/json", nil)
	if err != nil {
		t.Fatalf("post: %v", err)
	}
	defer resp2.Body.Close()
	var body api.PosterRefreshResponse
	if err := json.NewDecoder(resp2.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if body.Status != "already-running" {
		t.Errorf("status = %q, want already-running", body.Status)
	}
}

func TestPostersDisabledWhenNotWired(t *testing.T) {
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	srv := New(data.NewStubSource(), logger) // no WithPosters
	ts := httptest.NewServer(srv.Handler())
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/posters")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusServiceUnavailable {
		t.Errorf("status = %d, want 503", resp.StatusCode)
	}
}
