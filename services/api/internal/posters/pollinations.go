package posters

import (
	"context"
	"errors"
	"fmt"
	"hash/fnv"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"sync"
	"time"
)

// pollinationsEndpoint is the base path for the free, no-auth image
// generation API at https://pollinations.ai. Append the URL-escaped
// prompt as a path segment, then query params (width, height, model,
// seed, nologo).
//
// Verified by curl:
//
//	curl -o test.png "https://image.pollinations.ai/prompt/A%20cinematic%20stadium?width=1024&height=1024&model=flux&seed=42&nologo=true"
//
// Returns the raw image bytes (image/jpeg or image/png) directly in the
// response body — no JSON wrapper.
const pollinationsEndpoint = "https://image.pollinations.ai/prompt/"

// pollinationsDefaultModel is the highest-quality free model. "turbo"
// is faster but lower quality; "flux" is the documented default.
const pollinationsDefaultModel = "flux"

// pollinationsRateLimit is the documented anonymous-tier minimum spacing
// between requests (~1 req per 15s). We block client-side to stay under
// the limit instead of relying on retries against 429s.
const pollinationsRateLimit = 15 * time.Second

// pollinationsMaxBytes caps the response body so a misbehaving upstream
// can't OOM the container. 10 MB is well above any sane PNG/JPEG.
const pollinationsMaxBytes = 10 * 1024 * 1024

// PollinationsImageClient is an ImageClient that talks to
// https://image.pollinations.ai. Free, no API key required, ~1 req per
// 15 s on the anonymous tier — so we serialize requests behind a mutex
// and a simple time-based gate.
//
// Reruns are deterministic: the seed is derived from a hash of the
// prompt, so the same poster prompt always produces the same image.
// That makes the on-disk cache idempotent and lets users reproduce a
// poster they liked.
type PollinationsImageClient struct {
	model  string
	client *http.Client

	// mu serializes requests so the per-15s rate limit is honored even
	// when the Generator's concurrency=4 fires four jobs at once. The
	// generator's semaphore lets us into Generate, this lock gates the
	// actual HTTP call.
	mu          sync.Mutex
	lastRequest time.Time
}

// NewPollinationsImageClient returns a client. model defaults to "flux"
// (best quality on the free tier); pass "turbo" for the fast variant.
func NewPollinationsImageClient(model string) *PollinationsImageClient {
	if model == "" {
		model = pollinationsDefaultModel
	}
	return &PollinationsImageClient{
		model: model,
		// Image generation is slow on the free tier — observed cold flux
		// requests at ~73 s, which was uncomfortably close to the previous
		// 90 s budget. 180 s gives breathing room without risking the
		// Cloud Run request from being cut by its own outer 300 s timeout.
		client: &http.Client{Timeout: 180 * time.Second},
	}
}

// Generate calls Pollinations and returns the raw image bytes. Honors
// the rate limit by blocking until at least pollinationsRateLimit has
// elapsed since the previous request. The caller's context can cancel
// the wait.
func (c *PollinationsImageClient) Generate(ctx context.Context, prompt string) ([]byte, error) {
	if err := c.waitForSlot(ctx); err != nil {
		return nil, err
	}

	u := buildPollinationsURL(prompt, c.model)

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return nil, fmt.Errorf("build request: %w", err)
	}

	resp, err := c.client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("http: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode/100 != 2 {
		// Read a short snippet of the body for diagnostics. Pollinations
		// can return 429 (rate limit) or 5xx during model warmups; the
		// caller's per-job error handler decides whether to retry on the
		// next cron tick — we never retry inline.
		snippet, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
		return nil, fmt.Errorf("pollinations status %d: %s", resp.StatusCode, string(snippet))
	}

	body, err := io.ReadAll(io.LimitReader(resp.Body, pollinationsMaxBytes))
	if err != nil {
		return nil, fmt.Errorf("read body: %w", err)
	}
	if len(body) == 0 {
		return nil, errors.New("pollinations: empty image bytes")
	}
	return body, nil
}

// waitForSlot blocks until at least pollinationsRateLimit has elapsed
// since the previous successful or attempted request, or until ctx is
// done. The lock is held across the wait so a second caller arriving
// during the sleep also waits its turn.
func (c *PollinationsImageClient) waitForSlot(ctx context.Context) error {
	c.mu.Lock()
	defer c.mu.Unlock()

	if !c.lastRequest.IsZero() {
		elapsed := time.Since(c.lastRequest)
		if elapsed < pollinationsRateLimit {
			wait := pollinationsRateLimit - elapsed
			t := time.NewTimer(wait)
			defer t.Stop()
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-t.C:
			}
		}
	}
	c.lastRequest = time.Now()
	return nil
}

// buildPollinationsURL constructs the GET URL for the given prompt and
// model. The prompt goes in the path (URL-escaped), and width / height
// / model / seed / nologo are query params. Seed is deterministic per
// prompt via fnv-32a so repeat runs are idempotent.
func buildPollinationsURL(prompt, model string) string {
	q := url.Values{}
	q.Set("width", "1024")
	q.Set("height", "1024")
	q.Set("model", model)
	q.Set("seed", strconv.FormatUint(uint64(promptSeed(prompt)), 10))
	q.Set("nologo", "true")
	return pollinationsEndpoint + url.PathEscape(prompt) + "?" + q.Encode()
}

// promptSeed derives a stable 31-bit non-negative integer from the
// prompt so the same prompt always produces the same image. Using FNV
// keeps this stdlib-only and avoids a crypto-grade hash we don't need.
func promptSeed(prompt string) uint32 {
	h := fnv.New32a()
	_, _ = h.Write([]byte(prompt))
	// Mask to 31 bits so the value always fits in a positive int and
	// stays well inside the seed range every model accepts.
	return h.Sum32() & 0x7fffffff
}
