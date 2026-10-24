package server

import (
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/abindlish/streamed-backend/internal/api"
	"github.com/abindlish/streamed-backend/internal/data"
)

// When WithFeedCache is never called the route must 503 cleanly so the
// TV doesn't retry against an upstream that doesn't exist.
func TestCricketIntlUpcoming_503WhenCacheUnwired(t *testing.T) {
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	srv := New(data.NewStubSource(), logger) // no WithFeedCache
	ts := httptest.NewServer(srv.Handler())
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/v1/cricket/international/upcoming")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusServiceUnavailable {
		t.Errorf("status = %d, want 503", resp.StatusCode)
	}
	var body api.ErrorResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if body.Error != "feeds disabled" {
		t.Errorf("error = %q, want %q", body.Error, "feeds disabled")
	}
}
