// Command streamed-extractor is a single-purpose service: given an
// embed wrapper URL (embedsports.top, etc.), spin up a headless
// chromium, let the wrapper's player JS run, and capture the m3u8
// URL the player loads. Returns it (plus any required Referer) to
// the TV so it can hand the m3u8 directly to Tizen's native
// MediaPlayer instead of trying to render the wrapper in WebView.
//
// Why a separate service from the main backend:
//   - chromedp + chromium adds ~250 MB to the container image.
//   - Cold-start is several seconds longer once chromium is in
//     play; we don't want that latency on every /v1/profiles call.
//   - Different scaling profile — extraction is CPU-bursty; the
//     main backend is mostly I/O-bound. Co-deploying would force
//     us to over-provision the main service.
//
// Scales to zero. Personal-use cost: <$1/month.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"sort"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/chromedp/cdproto/network"
	"github.com/chromedp/cdproto/page"
	"github.com/chromedp/cdproto/runtime"
	"github.com/chromedp/chromedp"
	"github.com/golang-jwt/jwt/v5"
)

// extractTimeout caps each chromium session. Real-world embedsports
// derivation typically completes in 4-8s for the `echo` source class.
// `admin` source pages take longer to settle (heavier player JS, occasional
// click-gated playback), so 30s is the new ceiling — enough headroom
// without wedging the request indefinitely. Cloud Run's per-request limit
// is 300s, so we still have plenty of slack on top.
const extractTimeout = 30 * time.Second

// allowedExtractionHosts is the SSRF guardrail. We only ever drive
// the headless browser at known wrapper hosts. Add hosts here as
// we encounter new wrappers.
var allowedExtractionHosts = map[string]bool{
	"embedsports.top":     true,
	"www.embedsports.top": true,
	"pooembed.eu":         true,
	"www.pooembed.eu":     true,
}

// adBlockPatterns is the CDP-glob blocklist passed to network.SetBlockedURLs
// before each Navigate. Two source rules:
//   1. Hosts we observed firing during failed extractions whose absence
//      shouldn't break the wrapper page (popunder spam, third-party ads).
//   2. A small set of universally safe ad/tracker hosts (DoubleClick,
//      Google Tag Manager, etc.) included pre-emptively because wrapper
//      pages cycle through these constantly and blocking them costs us
//      nothing.
//
// Patterns use CDP's URL-pattern syntax: `*` is the wildcard, anything
// else is a literal substring. We do NOT touch embedsports.top or
// pooembed.eu — same-origin ad iframes (e.g. embedsports.top/ad.html)
// are left alone so we don't accidentally trip an "is the player div
// rendering" check inside the wrapper's own JS.
var adBlockPatterns = []string{
	// Observed during admin-source failures (May 2026 diagnostics):
	"*ndcertainlywhen.com*",
	"*adexchangerapid.com*",
	"*usrpubtrk.com*",
	"*cdn-lab.shop*",
	// Preemptive — broadly known ad/tracker networks that a wrapper
	// page never actually needs:
	"*doubleclick.net*",
	"*googlesyndication.com*",
	"*googletagmanager.com*",
	"*google-analytics.com*",
}

// extractResult is what the TV gets back. Resolved is the proxied
// m3u8 URL the TV's native player should fetch — points at OUR
// proxy, not the upstream CDN, because the upstream URL is IP-
// bound to whichever Cloud Run instance ran the chromium. The
// upstream URL itself is included as RawResolved for diagnostic
// telemetry only — TV should always use Resolved.
type extractResult struct {
	Resolved    string            `json:"resolved"`              // proxied URL — the TV fetches this
	RawResolved string            `json:"raw_resolved,omitempty"` // diagnostic: original CDN URL (don't fetch from TV)
	Referer     string            `json:"referer,omitempty"`
	Cookie      string            `json:"cookie,omitempty"`
	Headers     map[string]string `json:"headers,omitempty"`
	Strategy    string            `json:"strategy"`
	Error       string            `json:"error,omitempty"`
	Elapsed     string            `json:"elapsed,omitempty"`
}

func main() {
	logger := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(logger)

	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}
	signingKey := strings.TrimSpace(os.Getenv("JWT_SIGNING_KEY"))
	if len(signingKey) < 32 {
		logger.Error("JWT_SIGNING_KEY missing or too short (>=32 bytes required)")
		os.Exit(1)
	}
	// publicBaseURL is the URL the TV uses to reach us. Required
	// because the proxied m3u8 contains absolute URLs we mint —
	// they must point at our public hostname, not at localhost or
	// the in-cluster k8s name. Cloud Run injects this as an env
	// var at deploy time (see deploy.sh).
	publicBaseURL := strings.TrimSpace(os.Getenv("PUBLIC_BASE_URL"))
	if publicBaseURL == "" {
		logger.Error("PUBLIC_BASE_URL is required (e.g. https://streamed-extractor-...run.app)")
		os.Exit(1)
	}
	proxy := newProxySvc([]byte(signingKey), publicBaseURL, logger)

	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/health", handleHealth)
	mux.HandleFunc("POST /v1/extract", handleExtract([]byte(signingKey), proxy, logger))
	mux.HandleFunc("GET /v1/proxy/m3u8", proxy.handleProxyM3U8)
	mux.HandleFunc("GET /v1/proxy/seg", proxy.handleProxySeg)

	srv := &http.Server{
		Addr:              ":" + port,
		Handler:           withCORS(withLog(mux, logger)),
		ReadHeaderTimeout: 10 * time.Second,
	}

	// Graceful shutdown so an in-flight extraction doesn't get
	// killed mid-chromium; chromedp leaks chrome processes
	// otherwise.
	errCh := make(chan error, 1)
	go func() {
		logger.Info("listening", "addr", srv.Addr)
		if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			errCh <- err
		}
	}()
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	select {
	case sig := <-sigCh:
		logger.Info("shutdown signal", "signal", sig.String())
	case err := <-errCh:
		logger.Error("server failed", "err", err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	_ = srv.Shutdown(ctx)
}

func handleHealth(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func handleExtract(signingKey []byte, proxy *proxySvc, logger *slog.Logger) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !checkBearer(r, signingKey) {
			writeError(w, http.StatusUnauthorized, "invalid or missing token")
			return
		}
		raw := strings.TrimSpace(r.URL.Query().Get("url"))
		if raw == "" {
			writeError(w, http.StatusBadRequest, "missing url")
			return
		}
		parsed, err := url.Parse(raw)
		if err != nil || parsed.Scheme != "https" {
			writeError(w, http.StatusBadRequest, "url must be https")
			return
		}
		if !allowedExtractionHosts[strings.ToLower(parsed.Host)] {
			writeJSON(w, http.StatusOK, extractResult{
				Strategy: "skip-host-not-allowed",
			})
			return
		}

		started := time.Now()
		sighting, extractErr := extractM3U8(r.Context(), raw, logger)
		elapsed := time.Since(started)

		if extractErr != nil {
			logger.Warn("extract failed",
				"url", raw,
				"err", extractErr,
				"elapsed_ms", elapsed.Milliseconds(),
			)
			writeJSON(w, http.StatusOK, extractResult{
				Strategy: "skip-extract-failed",
				Error:    extractErr.Error(),
				Elapsed:  elapsed.String(),
			})
			return
		}
		if sighting.URL == "" {
			writeJSON(w, http.StatusOK, extractResult{
				Strategy: "skip-no-m3u8-seen",
				Elapsed:  elapsed.String(),
			})
			return
		}

		// Mint the proxied URL the TV will actually fetch. The
		// upstream URL goes into the token body — TV never sees
		// it directly (well, it's also returned as
		// raw_resolved for diagnostics but the TV should fetch
		// `resolved` only).
		ua := sighting.Headers["User-Agent"]
		tok := proxyToken{
			URL:     sighting.URL,
			Referer: sighting.Referer,
			UA:      ua,
			Cookie:  sighting.Cookie,
			Expiry:  time.Now().Add(proxyTokenTTL).Unix(),
		}
		signed, signErr := signProxyToken(tok, signingKey)
		if signErr != nil {
			logger.Error("token sign failed", "err", signErr)
			writeError(w, http.StatusInternalServerError, "token sign failed")
			return
		}
		proxiedURL := strings.TrimRight(proxy.publicBaseURL, "/") + "/v1/proxy/m3u8?t=" + signed

		logger.Info("extracted",
			"wrapper", raw,
			"upstream", sighting.URL,
			"proxied", proxiedURL,
			"referer", sighting.Referer,
			"cookie_len", len(sighting.Cookie),
			"header_count", len(sighting.Headers),
			"elapsed_ms", elapsed.Milliseconds(),
		)
		writeJSON(w, http.StatusOK, extractResult{
			Resolved:    proxiedURL,
			RawResolved: sighting.URL,
			Referer:     sighting.Referer,
			Cookie:      sighting.Cookie,
			Headers:     sighting.Headers,
			Strategy:    "ok",
			Elapsed:     elapsed.String(),
		})
	}
}

// m3u8Sighting bundles everything we noticed about the m3u8
// request — URL, every request header chromium attached, and a
// formatted Cookie line synthesized from the browser's cookie jar
// at the moment the request fired. The TV native player then
// uses these to replay the URL with the same context the player
// had.
type m3u8Sighting struct {
	URL     string
	Referer string
	Cookie  string
	Headers map[string]string
}

// extractM3U8 navigates a headless chromium to the wrapper URL and
// returns the first m3u8 request it observes, plus everything
// needed to replay it from a different client (Cookie line + all
// non-trivial request headers).
//
// We don't try to BLOCK the page from doing whatever else it does
// (ads, popups, sub-iframes); we only LISTEN for the m3u8 request.
// This makes the extraction robust against page tricks — we don't
// have to model what the page does, only that it eventually fetches
// an m3u8.
func extractM3U8(ctx context.Context, wrapperURL string, logger *slog.Logger) (m3u8Sighting, error) {
	// Allocator: chromium binary + flags. The flags here are the
	// standard "headless on Cloud Run" set — disable sandbox (no
	// kernel namespaces in Cloud Run gen-2), disable GPU (we're
	// headless), etc. dev-shm-usage prevents OOM in 64MB /dev/shm
	// containers.
	opts := append(chromedp.DefaultExecAllocatorOptions[:],
		chromedp.NoFirstRun,
		chromedp.NoDefaultBrowserCheck,
		// Use the NEW headless mode (introduced in Chrome 109+). The
		// legacy --headless=true is a stripped-down chromium with
		// many subtle behavioural differences from a real browser
		// (no font rendering, no WebGL by default, distinctive UA
		// quirks). --headless=new runs the same chromium as the
		// non-headless build with display/windowing stubbed out, so
		// player JS that does feature-detection or fingerprinting
		// behaves the same as in a real Chrome. Costs marginally
		// more memory (~30 MB per session) but worth it for parity.
		chromedp.Flag("headless", "new"),
		chromedp.Flag("disable-gpu", true),
		chromedp.Flag("no-sandbox", true),
		chromedp.Flag("disable-dev-shm-usage", true),
		chromedp.Flag("disable-extensions", true),
		// Mimic a Tizen TV UA so the wrapper serves us the same
		// HTML it serves the actual app. Some wrappers UA-sniff.
		chromedp.UserAgent("Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) 76.0.3809.146 TV Safari/537.36"),
	)

	allocCtx, allocCancel := chromedp.NewExecAllocator(ctx, opts...)
	defer allocCancel()

	browserCtx, browserCancel := chromedp.NewContext(allocCtx)
	defer browserCancel()

	timeoutCtx, timeoutCancel := context.WithTimeout(browserCtx, extractTimeout)
	defer timeoutCancel()

	// Capture the first .m3u8 request we see, AND record every
	// network request the page issues so we have something useful
	// to dump on extraction failure (admin-source pages have
	// historically failed silently — without this telemetry we
	// were just guessing what they were doing).
	sightingCh := make(chan m3u8Sighting, 1)
	var observedMu sync.Mutex
	observed := make([]string, 0, 256)
	hostCounts := make(map[string]int, 16)
	// Captured postMessage events from msgTraceJS — flushed on
	// extraction timeout so we can see the cross-frame chatter that
	// the (possibly silent) admin-source player is waiting on. Bounded
	// at 100 to keep a chatty page from drowning the listener.
	msgTraces := make([]string, 0, 64)
	chromedp.ListenTarget(timeoutCtx, func(ev interface{}) {
		switch e := ev.(type) {
		case *runtime.EventConsoleAPICalled:
			// msgTraceJS funnels its events through console.log. We
			// only care about the ones prefixed with MSGTRACE: —
			// everything else (page's own console.log spam) is
			// dropped to keep the noise out of the diagnostic.
			if e == nil {
				return
			}
			for _, arg := range e.Args {
				if arg == nil {
					continue
				}
				// arg.Value is RawMessage (already JSON-encoded).
				// String values come through quoted; trim the
				// quotes before the prefix check.
				raw := strings.TrimSpace(string(arg.Value))
				raw = strings.Trim(raw, `"`)
				if !strings.HasPrefix(raw, "MSGTRACE:") {
					continue
				}
				observedMu.Lock()
				if len(msgTraces) < 100 {
					msgTraces = append(msgTraces, strings.TrimPrefix(raw, "MSGTRACE:"))
				}
				observedMu.Unlock()
			}
		case *network.EventRequestWillBeSent:
			if e == nil || e.Request == nil {
				return
			}
			u := e.Request.URL
			if u == "" {
				return
			}
			// Tally regardless of whether it's the m3u8 — we want
			// the full picture for the diagnostic dump. Cap at 256
			// so a renegade ad-loaded page doesn't grow this
			// unbounded.
			observedMu.Lock()
			if len(observed) < 256 {
				observed = append(observed, u)
			}
			if host := hostFromURL(u); host != "" {
				hostCounts[host]++
			}
			observedMu.Unlock()

			low := strings.ToLower(u)
			if !strings.Contains(low, ".m3u8") {
				return
			}
			// Capture every header chromium attached to the
			// request. We strip pseudo-headers (`:method` etc.)
			// since those aren't replayable. Referer is also
			// pulled into its own field for ergonomics.
			hdrs := make(map[string]string, len(e.Request.Headers))
			ref := ""
			for k, v := range e.Request.Headers {
				if strings.HasPrefix(k, ":") {
					continue
				}
				s, ok := v.(string)
				if !ok {
					continue
				}
				if strings.EqualFold(k, "Referer") {
					ref = s
				}
				hdrs[k] = s
			}
			// Non-blocking send; ignore if a sighting already landed.
			select {
			case sightingCh <- m3u8Sighting{URL: u, Referer: ref, Headers: hdrs}:
			default:
			}
		}
	})

	if err := chromedp.Run(timeoutCtx,
		network.Enable(),
		// Runtime.Enable lets us receive Runtime.consoleAPICalled
		// events — needed so msgTraceJS's console.log("MSGTRACE:...")
		// entries reach the chromedp-side listener.
		runtime.Enable(),
		// Inject the postMessage tracer BEFORE Navigate so it's
		// installed in every frame's JS realm at document_start
		// (including cross-origin iframes that we can't read from
		// later). chromedp.ActionFunc lets us call the CDP method
		// without needing a separate Action helper.
		chromedp.ActionFunc(func(ctx context.Context) error {
			_, err := page.AddScriptToEvaluateOnNewDocument(msgTraceJS).Do(ctx)
			return err
		}),
		// Drop ad / tracker / popunder requests at the network layer
		// BEFORE Navigate. The admin source class on embedsports.top
		// loads a popunder script (ndcertainlywhen.com) that fires
		// 12 redirects in the first 30s and was likely starving the
		// inner pooembed.eu iframe of its event budget — observed via
		// the failure-path diagnostic log added in this same package.
		// Patterns are CDP "URL pattern" globs; we only block what
		// we've actually observed plus a small set of well-known
		// ad/tracker hosts that are safe blanket blocks. None of the
		// patterns cover same-origin embedsports.top resources, so
		// the player JS itself is unaffected.
		network.SetBlockedURLS(adBlockPatterns),
		chromedp.Navigate(wrapperURL),
	); err != nil {
		return m3u8Sighting{}, fmt.Errorf("chromedp navigate: %w", err)
	}

	// Periodic play-button clicker. Some wrapper pages (notably the
	// `admin` source class on embedsports.top) gate the underlying
	// m3u8 fetch behind a user gesture or a player-init click. Every
	// 1.5s we fire a small JS snippet that:
	//   1. Calls .play() on every <video> in the document.
	//   2. Click()s anything that LOOKS like a play affordance —
	//      common selectors only, no per-site customization.
	// The snippet is idempotent (multiple .play() calls + clicks on
	// already-playing elements are no-ops) and runs in <1ms, so the
	// budget is trivial. We do NOT block on it — chromedp.Evaluate
	// would round-trip to the renderer; we use Go's own ticker plus
	// a fire-and-forget chromedp.Run to keep the listener loop
	// responsive.
	tickerCtx, tickerCancel := context.WithCancel(timeoutCtx)
	defer tickerCancel()
	go func() {
		ticker := time.NewTicker(1500 * time.Millisecond)
		defer ticker.Stop()
		for {
			select {
			case <-tickerCtx.Done():
				return
			case <-ticker.C:
				// Best-effort. Errors here just mean the page
				// hasn't rendered yet or chromium is busy —
				// the next tick will retry.
				_ = chromedp.Run(tickerCtx, chromedp.Evaluate(playClickerJS, nil))
			}
		}
	}()

	var sighting m3u8Sighting
	select {
	case sighting = <-sightingCh:
	case <-timeoutCtx.Done():
		if errors.Is(timeoutCtx.Err(), context.DeadlineExceeded) {
			// Dump what we DID see so operators can diagnose
			// why this wrapper class doesn't yield an m3u8.
			// Sorted by count desc so the dominant hosts
			// (typical: ad networks, the wrapper itself) sort
			// first; tail is usually where the real player
			// signals would be if they existed.
			observedMu.Lock()
			topHosts := topHostCounts(hostCounts, 10)
			urlSample := observed
			if len(urlSample) > 25 {
				urlSample = urlSample[:25]
			}
			msgTraceCopy := append([]string(nil), msgTraces...)
			observedMu.Unlock()
			logger.Warn("extract timeout: observed network requests during failed extraction",
				"wrapper", wrapperURL,
				"timeout", extractTimeout.String(),
				"totalRequests", len(observed),
				"topHosts", topHosts,
				"firstURLs", urlSample,
				"msgTraceCount", len(msgTraceCopy),
				"msgTrace", msgTraceCopy,
			)
			return m3u8Sighting{}, fmt.Errorf("no m3u8 within %s", extractTimeout)
		}
		return m3u8Sighting{}, timeoutCtx.Err()
	}

	// Snapshot the cookie jar AT THE MOMENT we saw the m3u8 — many
	// CDNs gate replay on a session cookie set during the page
	// load. Format as a single Cookie header value (the standard
	// `name=value; name2=value2` shape) so the TV can attach it
	// directly to its own m3u8 request.
	//
	// Use a short timeout: we already extracted what we needed, so
	// don't let a stuck browser block the response.
	cookieCtx, cookieCancel := context.WithTimeout(browserCtx, 2*time.Second)
	defer cookieCancel()
	if cookies, err := getCookiesForURL(cookieCtx, sighting.URL); err == nil {
		sighting.Cookie = cookies
	} else {
		logger.Warn("cookie capture failed", "err", err)
	}
	return sighting, nil
}

// playClickerJS forces playback on any <video> + clicks anything that
// looks like a play button. Idempotent and ~ms to run. Wrapped in a
// catch-all try so a single bad selector or detached node doesn't
// throw out of the eval. Runs every ~1.5s while we wait for an m3u8.
const playClickerJS = `
try {
  var sels = ['video', 'button[class*="play" i]', 'button[id*="play" i]',
              '[aria-label*="play" i]', '[title*="play" i]',
              '.play, .play-button, .vjs-big-play-button, .jw-icon-display'];
  for (var i = 0; i < sels.length; i++) {
    var els = document.querySelectorAll(sels[i]);
    for (var j = 0; j < Math.min(els.length, 4); j++) {
      try { els[j].click(); } catch (e) {}
    }
  }
  var vids = document.querySelectorAll('video');
  for (var k = 0; k < vids.length; k++) {
    try {
      vids[k].muted = true;   // some browsers gate autoplay on muted
      vids[k].play();
    } catch (e) {}
  }
} catch (e) {}
`

// msgTraceJS is injected via Page.addScriptToEvaluateOnNewDocument so
// it runs on EVERY frame as it's created — including cross-origin
// iframes (the install runs in each frame's own JS realm). The hook
// wraps Window.prototype.postMessage to log outbound messages, and
// EventTarget.prototype.addEventListener('message',...) to log
// inbound. Each event is funneled through console.log with a
// MSGTRACE: prefix so the chromedp-side Runtime.consoleAPICalled
// listener can grab it without us having to round-trip JS to read a
// global from each frame (which cross-origin would block anyway).
//
// Truncates payloads to 200 chars to keep the log manageable when a
// page floods us with frequent messages.
const msgTraceJS = `
(function() {
  if (window.__msgTraceInstalled) return;
  window.__msgTraceInstalled = true;
  // Install marker — proves the hook ran in this frame's realm.
  // Without this we can't distinguish "no postMessages were sent"
  // from "hook never installed in this frame", which matter very
  // differently for diagnosis.
  try {
    console.log('MSGTRACE:' + JSON.stringify({
      d: 'install',
      f: (location.href || '').slice(0, 100),
      t: '',
      m: 'hook installed'
    }));
  } catch (e) {}
  var log = function(dir, data, target) {
    try {
      var s;
      if (typeof data === 'string') {
        s = data;
      } else {
        try { s = JSON.stringify(data); } catch(e) { s = String(data); }
      }
      if (s && s.length > 200) s = s.slice(0, 200) + '...';
      console.log('MSGTRACE:' + JSON.stringify({
        d: dir,
        f: (location.href || '').slice(0, 100),
        t: String(target || ''),
        m: s
      }));
    } catch (e) {}
  };
  try {
    var origPost = Window.prototype.postMessage;
    Window.prototype.postMessage = function(msg, targetOrigin) {
      log('send', msg, targetOrigin);
      return origPost.apply(this, arguments);
    };
  } catch (e) {}
  try {
    var origAdd = EventTarget.prototype.addEventListener;
    EventTarget.prototype.addEventListener = function(type, listener, opts) {
      if (type === 'message' && typeof listener === 'function') {
        var wrapped = function(ev) {
          try { log('recv', ev.data, ev.origin); } catch(e) {}
          return listener.apply(this, arguments);
        };
        return origAdd.call(this, type, wrapped, opts);
      }
      return origAdd.apply(this, arguments);
    };
  } catch (e) {}
})();
`

// hostFromURL pulls the bare host out of a URL. Returns "" on any
// parse failure — the caller treats that as "skip this URL for the
// host tally" rather than aborting the whole observability path.
func hostFromURL(raw string) string {
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" {
		return ""
	}
	return strings.ToLower(u.Host)
}

// topHostCounts returns the top-N hosts as a "host(count)" string
// slice, sorted by count descending. Used in the failure-path
// diagnostic log; never feeds back into client responses.
func topHostCounts(counts map[string]int, n int) []string {
	type kv struct {
		host  string
		count int
	}
	pairs := make([]kv, 0, len(counts))
	for h, c := range counts {
		pairs = append(pairs, kv{h, c})
	}
	sort.Slice(pairs, func(i, j int) bool { return pairs[i].count > pairs[j].count })
	if len(pairs) > n {
		pairs = pairs[:n]
	}
	out := make([]string, len(pairs))
	for i, p := range pairs {
		out[i] = fmt.Sprintf("%s(%d)", p.host, p.count)
	}
	return out
}

// getCookiesForURL synthesizes the Cookie header value the browser
// would send for `targetURL` — i.e. the subset of cookies whose
// domain + path match. Saves the TV from having to reimplement
// cookie matching itself.
func getCookiesForURL(ctx context.Context, targetURL string) (string, error) {
	parsed, err := url.Parse(targetURL)
	if err != nil {
		return "", err
	}
	var cookies []*network.Cookie
	if err := chromedp.Run(ctx, chromedp.ActionFunc(func(ctx context.Context) error {
		c, err := network.GetCookies().WithUrls([]string{targetURL}).Do(ctx)
		if err != nil {
			return err
		}
		cookies = c
		return nil
	})); err != nil {
		return "", err
	}
	if len(cookies) == 0 {
		return "", nil
	}
	parts := make([]string, 0, len(cookies))
	for _, c := range cookies {
		if c == nil || c.Name == "" {
			continue
		}
		// Belt-and-suspenders domain match (chromedp should have
		// already filtered, but in case we ever swap impls).
		if c.Domain != "" && !strings.HasSuffix(parsed.Host, strings.TrimPrefix(c.Domain, ".")) {
			continue
		}
		parts = append(parts, c.Name+"="+c.Value)
	}
	return strings.Join(parts, "; "), nil
}

// ---- HTTP helpers ----------------------------------------------------------

func writeJSON(w http.ResponseWriter, code int, body interface{}) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(body)
}

func writeError(w http.ResponseWriter, code int, msg string) {
	writeJSON(w, code, map[string]string{"error": msg})
}

func withLog(next http.Handler, logger *slog.Logger) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		sw := &statusWriter{ResponseWriter: w, code: http.StatusOK}
		next.ServeHTTP(sw, r)
		logger.Info("http",
			"method", r.Method,
			"path", r.URL.Path,
			"status", sw.code,
			"durMs", time.Since(start).Milliseconds(),
		)
	})
}

func withCORS(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type, Authorization")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}

type statusWriter struct {
	http.ResponseWriter
	code int
}

func (sw *statusWriter) WriteHeader(code int) {
	sw.code = code
	sw.ResponseWriter.WriteHeader(code)
}

// ---- Auth ------------------------------------------------------------------

// checkBearer validates the JWT against the SAME signing key the
// main backend uses. Re-uses the user's existing pairing token —
// no separate auth flow for the extractor.
func checkBearer(r *http.Request, signingKey []byte) bool {
	authz := r.Header.Get("Authorization")
	const prefix = "Bearer "
	if len(authz) <= len(prefix) || !strings.EqualFold(authz[:len(prefix)], prefix) {
		return false
	}
	tokenStr := authz[len(prefix):]
	tok, err := jwt.Parse(tokenStr, func(t *jwt.Token) (interface{}, error) {
		if _, ok := t.Method.(*jwt.SigningMethodHMAC); !ok {
			return nil, fmt.Errorf("unexpected alg: %v", t.Header["alg"])
		}
		return signingKey, nil
	}, jwt.WithValidMethods([]string{"HS256"}))
	if err != nil || tok == nil || !tok.Valid {
		return false
	}
	return true
}
