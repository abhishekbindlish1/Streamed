package posters

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

// pngMagic is the 8-byte PNG signature; we never decode the image, we
// just need a non-empty body that callers will recognize as an image.
var pngMagic = []byte{0x89, 'P', 'N', 'G', '\r', '\n', 0x1a, '\n'}

// withTestServer wires a PollinationsImageClient to point at a fake
// upstream and zeroes the rate limit so tests don't burn 15s per call.
// The handler closure decides what the upstream returns. Returns the
// client and the server (so the test can inspect the captured request).
type capture struct {
	mu       sync.Mutex
	requests []*http.Request
}

func newClientForTest(t *testing.T, h http.HandlerFunc) (*PollinationsImageClient, *httptest.Server, *capture) {
	t.Helper()
	cap := &capture{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		cap.mu.Lock()
		// Clone request so the handler can still read it; we only need URL.
		cloned := r.Clone(context.Background())
		cap.requests = append(cap.requests, cloned)
		cap.mu.Unlock()
		h(w, r)
	}))
	t.Cleanup(srv.Close)

	c := NewPollinationsImageClient("flux")
	// The constructor wires the real Pollinations URL; the client doesn't
	// expose a setter (we don't want production callers to override it),
	// so the test rewrites pollinationsEndpoint via a hook by setting up
	// a sub-client that re-implements the URL build. Easier: use a custom
	// transport that rewrites the host.
	c.client.Transport = &rewriteTransport{target: srv.URL, base: http.DefaultTransport}
	return c, srv, cap
}

// rewriteTransport rewrites every outbound request's scheme + host to
// the test server's URL while preserving the path and query. This lets
// us keep the production URL builder unchanged while pointing at httptest.
type rewriteTransport struct {
	target string // e.g. "http://127.0.0.1:54321"
	base   http.RoundTripper
}

func (rt *rewriteTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	// Replace scheme + host but keep the path + query intact.
	tgt := rt.target
	tgt = strings.TrimRight(tgt, "/")
	// Rebuild a URL from target + req.URL.RequestURI (path + ?query).
	newURL := tgt + req.URL.RequestURI()
	req2, err := http.NewRequestWithContext(req.Context(), req.Method, newURL, req.Body)
	if err != nil {
		return nil, err
	}
	req2.Header = req.Header.Clone()
	return rt.base.RoundTrip(req2)
}

// --- Tests ----------------------------------------------------------

func TestPollinations_Generate_Success(t *testing.T) {
	c, _, cap := newClientForTest(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "image/png")
		_, _ = w.Write(pngMagic)
	})

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	got, err := c.Generate(ctx, "A cinematic cricket stadium at night")
	if err != nil {
		t.Fatalf("Generate: %v", err)
	}
	if string(got) != string(pngMagic) {
		t.Fatalf("body mismatch: got %q want %q", got, pngMagic)
	}
	if len(cap.requests) != 1 {
		t.Fatalf("want 1 upstream request, got %d", len(cap.requests))
	}
}

func TestPollinations_Generate_RateLimit429(t *testing.T) {
	c, _, _ := newClientForTest(t, func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusTooManyRequests)
		_, _ = w.Write([]byte(`{"error":"rate limit exceeded"}`))
	})

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	_, err := c.Generate(ctx, "test prompt")
	if err == nil {
		t.Fatal("want error on 429, got nil")
	}
	if !strings.Contains(err.Error(), "429") {
		t.Errorf("want error mentioning 429, got %v", err)
	}
}

func TestPollinations_Generate_5xx(t *testing.T) {
	for _, status := range []int{500, 502, 503} {
		c, _, _ := newClientForTest(t, func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(status)
			_, _ = w.Write([]byte("upstream blew up"))
		})

		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		_, err := c.Generate(ctx, "test prompt")
		cancel()
		if err == nil {
			t.Fatalf("want error on %d, got nil", status)
		}
	}
}

func TestPollinations_Generate_EmptyBody(t *testing.T) {
	c, _, _ := newClientForTest(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "image/png")
		// 200 with zero bytes.
	})

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	_, err := c.Generate(ctx, "test prompt")
	if err == nil {
		t.Fatal("want error on empty body, got nil")
	}
}

func TestPollinations_URLEncoding(t *testing.T) {
	c, _, cap := newClientForTest(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "image/png")
		_, _ = w.Write(pngMagic)
	})

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	prompt := "Cricket: India vs. Pakistan & friends? 100% drama!"
	if _, err := c.Generate(ctx, prompt); err != nil {
		t.Fatalf("Generate: %v", err)
	}

	if len(cap.requests) != 1 {
		t.Fatalf("want 1 request, got %d", len(cap.requests))
	}
	gotPath := cap.requests[0].URL.Path
	// Path should be /prompt/<escaped>. The unescaped form must round-trip.
	if !strings.HasPrefix(gotPath, "/prompt/") {
		t.Fatalf("path missing /prompt/ prefix: %q", gotPath)
	}
	// The path is URL-encoded on the wire; the stdlib's *http.Request
	// already decodes it into URL.Path. So compare to the raw prompt.
	encoded := strings.TrimPrefix(gotPath, "/prompt/")
	if encoded != prompt {
		// One acceptable fallback: stdlib may keep some chars escaped in
		// Path. Compare against URL-decoded RawPath if Path mismatched.
		if cap.requests[0].URL.RawPath != "" {
			t.Logf("RawPath=%q Path=%q", cap.requests[0].URL.RawPath, gotPath)
		}
		t.Errorf("decoded prompt mismatch:\n got: %q\nwant: %q", encoded, prompt)
	}

	// Required query params must be present.
	q := cap.requests[0].URL.Query()
	for _, want := range []string{"width", "height", "model", "seed", "nologo"} {
		if q.Get(want) == "" {
			t.Errorf("missing query param %q in %v", want, q)
		}
	}
	if q.Get("model") != "flux" {
		t.Errorf("model=%q want flux", q.Get("model"))
	}
	if q.Get("nologo") != "true" {
		t.Errorf("nologo=%q want true", q.Get("nologo"))
	}
	if q.Get("width") != "1024" || q.Get("height") != "1024" {
		t.Errorf("size=%sx%s want 1024x1024", q.Get("width"), q.Get("height"))
	}
}

func TestPollinations_DeterministicSeed(t *testing.T) {
	// Same prompt twice must produce the same seed; different prompts
	// should produce different seeds (with overwhelming probability).
	a := promptSeed("A cinematic cricket poster, India vs Pakistan")
	b := promptSeed("A cinematic cricket poster, India vs Pakistan")
	if a != b {
		t.Errorf("seed not deterministic: %d != %d", a, b)
	}
	c := promptSeed("A different prompt entirely")
	if a == c {
		t.Errorf("seeds collide for different prompts: %d", a)
	}
	// Seed must fit in positive int range so URL construction stays sane.
	if a > 0x7fffffff {
		t.Errorf("seed exceeds 31 bits: %d", a)
	}
}

func TestBuildPollinationsURL(t *testing.T) {
	got := buildPollinationsURL("hello world", "flux")
	if !strings.HasPrefix(got, pollinationsEndpoint) {
		t.Errorf("missing endpoint prefix: %q", got)
	}
	if !strings.Contains(got, "hello%20world") {
		t.Errorf("prompt not URL-escaped: %q", got)
	}
	if !strings.Contains(got, "model=flux") {
		t.Errorf("missing model param: %q", got)
	}
}

func TestPollinations_RateLimit_Spacing(t *testing.T) {
	// Sanity: with a fresh client the first call goes through immediately.
	// The second call would normally wait pollinationsRateLimit, but
	// that's 15s — too slow for a unit test. Just verify lastRequest is
	// updated so the rate limiter is engaged.
	c, _, _ := newClientForTest(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "image/png")
		_, _ = w.Write(pngMagic)
	})

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	if !c.lastRequest.IsZero() {
		t.Fatal("lastRequest should be zero before first call")
	}
	if _, err := c.Generate(ctx, "x"); err != nil {
		t.Fatalf("Generate: %v", err)
	}
	if c.lastRequest.IsZero() {
		t.Error("lastRequest not updated after Generate")
	}
}

func TestPollinations_RateLimit_RespectsContextCancellation(t *testing.T) {
	c, _, _ := newClientForTest(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "image/png")
		_, _ = w.Write(pngMagic)
	})

	// Burn the first slot immediately.
	ctx1, cancel1 := context.WithTimeout(context.Background(), 5*time.Second)
	if _, err := c.Generate(ctx1, "first"); err != nil {
		t.Fatalf("first Generate: %v", err)
	}
	cancel1()

	// Now the next call should block on the 15s spacer; cancel the
	// context after a tiny delay and verify Generate returns promptly.
	ctx2, cancel2 := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel2()
	start := time.Now()
	_, err := c.Generate(ctx2, "second")
	elapsed := time.Since(start)
	if err == nil {
		t.Fatal("want context-deadline error, got nil")
	}
	if elapsed > 1*time.Second {
		t.Errorf("rate limiter ignored ctx cancellation; waited %v", elapsed)
	}
}
