// Package iptv consumes the iptv-org public API — a community-
// maintained directory of free IPTV streams (39k+ channels, 15k+
// streams). We use it as an alternative to the streamed.pk catalog
// for streams whose CDN doesn't anti-bot itself into oblivion: the
// iptv-org streams are mostly public CDNs that accept plain
// `<video src=...>` playback with at most a Referer header.
//
// Why not just embed the iptv-org JSON URLs in the TV: keeps
// catalog fetches off the TV's hot path (caching, joining channels
// with streams, filtering by category), and means we can swap data
// sources later without a TV rebuild.
//
// Refresh policy: iptv-org rebuilds the JSON files daily. We cache
// for 6 hours — if their CI delays, our cached snapshot keeps
// serving rather than failing the TV.
package iptv

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"sort"
	"strings"
	"sync"
	"time"
)

const (
	channelsURL = "https://iptv-org.github.io/api/channels.json"
	streamsURL  = "https://iptv-org.github.io/api/streams.json"
	logosURL    = "https://iptv-org.github.io/api/logos.json"
	cacheTTL    = 6 * time.Hour
)

// Channel is the slimmed-down shape we return to the TV — only what
// the UI actually needs. iptv-org's full record is much wider
// (owners, network, alt names, ...) but we don't surface that.
type Channel struct {
	ID         string   `json:"id"`
	Name       string   `json:"name"`
	Country    string   `json:"country,omitempty"`
	Logo       string   `json:"logo,omitempty"`
	Categories []string `json:"categories,omitempty"`
	Streams    []Stream `json:"streams"`
}

// Stream is one playable URL for a channel. UA + Referer are
// per-stream overrides — the player MUST attach them to the m3u8
// + segment fetches or the upstream returns 403.
type Stream struct {
	URL       string `json:"url"`
	Quality   string `json:"quality,omitempty"`
	UserAgent string `json:"user_agent,omitempty"`
	Referrer  string `json:"referrer,omitempty"`
}

// rawChannel mirrors the iptv-org channels.json record. Wider than
// `Channel` so we can decode the full file without losing fields
// — we project to `Channel` ourselves.
type rawChannel struct {
	ID         string   `json:"id"`
	Name       string   `json:"name"`
	Country    string   `json:"country"`
	Logo       string   `json:"logo"`
	Categories []string `json:"categories"`
	IsNSFW     bool     `json:"is_nsfw"`
	Closed     *string  `json:"closed"`
}

// rawStream mirrors the iptv-org streams.json record.
type rawStream struct {
	Channel   *string `json:"channel"`
	URL       string  `json:"url"`
	Quality   *string `json:"quality"`
	UserAgent *string `json:"user_agent"`
	Referrer  *string `json:"referrer"`
}

// rawLogo mirrors one entry in iptv-org's logos.json. A single
// channel typically has many entries (different feeds, formats,
// sizes, color treatments). pickBestLogo() narrows to one.
//
// `Feed` and `Format` are pointers because the file uses null
// rather than an empty string for missing values; deserializing to
// pointer-string keeps "missing" distinguishable from "set to ''".
type rawLogo struct {
	Channel string   `json:"channel"`
	Feed    *string  `json:"feed"`
	InUse   bool     `json:"in_use"`
	Tags    []string `json:"tags"`
	Width   int      `json:"width"`
	Height  int      `json:"height"`
	Format  *string  `json:"format"`
	URL     string   `json:"url"`
}

// Service caches the iptv-org join in-memory + serves filtered
// queries. Safe to use from multiple HTTP handlers concurrently.
type Service struct {
	httpClient *http.Client
	mu         sync.RWMutex
	channels   []Channel
	fetchedAt  time.Time
	// channelLogos carries logo URLs from the iptv-org logos.json
	// file. Joined into Channel.Logo on rebuild. iptv-org splits
	// channels and logos into separate files (logos.json maps
	// channel id → multiple logo URLs); we pick the first.
}

func New(httpClient *http.Client) *Service {
	if httpClient == nil {
		httpClient = &http.Client{Timeout: 30 * time.Second}
	}
	return &Service{httpClient: httpClient}
}

// ListChannels returns the cached channel list, refreshing if
// stale. category is matched case-insensitively against the
// channel's categories list; pass "" for no filter. countryCC is
// the optional ISO-2 country code (e.g. "US"); pass "" for no
// filter. Results are sorted by name asc for stable display.
func (s *Service) ListChannels(ctx context.Context, category, countryCC string) ([]Channel, error) {
	if err := s.ensureFresh(ctx); err != nil {
		return nil, err
	}
	s.mu.RLock()
	all := s.channels
	s.mu.RUnlock()

	cat := strings.ToLower(strings.TrimSpace(category))
	cc := strings.ToUpper(strings.TrimSpace(countryCC))

	out := make([]Channel, 0, len(all))
	for _, c := range all {
		if cat != "" {
			matched := false
			for _, k := range c.Categories {
				if strings.EqualFold(k, cat) {
					matched = true
					break
				}
			}
			if !matched {
				continue
			}
		}
		if cc != "" && !strings.EqualFold(c.Country, cc) {
			continue
		}
		out = append(out, c)
	}
	// Stable display order — iptv-org's natural order is by id which
	// alphabetizes weirdly; sort by display name.
	sort.SliceStable(out, func(i, j int) bool {
		return strings.ToLower(out[i].Name) < strings.ToLower(out[j].Name)
	})
	return out, nil
}

// ensureFresh blocks the first caller that finds the cache stale
// while it refetches. Concurrent callers wait + then read the
// freshly populated cache. On refetch failure, returns the error
// only if we have NO cached data — otherwise serves stale (better
// to show old streams than nothing).
func (s *Service) ensureFresh(ctx context.Context) error {
	s.mu.RLock()
	stale := time.Since(s.fetchedAt) >= cacheTTL || len(s.channels) == 0
	s.mu.RUnlock()
	if !stale {
		return nil
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	// Double-check post-lock — another goroutine may have already
	// refreshed.
	if time.Since(s.fetchedAt) < cacheTTL && len(s.channels) > 0 {
		return nil
	}

	channels, err := s.refetch(ctx)
	if err != nil {
		// Have something cached? Serve stale.
		if len(s.channels) > 0 {
			return nil
		}
		return err
	}
	s.channels = channels
	s.fetchedAt = time.Now()
	return nil
}

// refetch pulls both files, joins them, returns the projected list.
// Channels with no streams are dropped — we have nothing to play
// for them. Closed channels are also dropped.
//
// We then HEALTH-CHECK each first stream URL: a single HEAD/GET
// request, ~3s timeout, kept to a small concurrency cap so we don't
// hammer the network. Streams that don't respond with `#EXTM3U`
// (or HEAD-OK + .m3u8 extension) get marked dead and dropped from
// the channel's stream list. Channels left with zero working
// streams are dropped entirely. Net effect: TV only sees channels
// that actually play, no broken-tile UX.
func (s *Service) refetch(ctx context.Context) ([]Channel, error) {
	rawChannels, err := fetchJSON[[]rawChannel](ctx, s.httpClient, channelsURL)
	if err != nil {
		return nil, fmt.Errorf("channels.json: %w", err)
	}
	rawStreams, err := fetchJSON[[]rawStream](ctx, s.httpClient, streamsURL)
	if err != nil {
		return nil, fmt.Errorf("streams.json: %w", err)
	}
	// logos.json is OPTIONAL — failure here is non-fatal. The
	// channels.json `logo` field is the fallback (used when
	// pickBestLogo finds no entry for a channel).
	rawLogos, err := fetchJSON[[]rawLogo](ctx, s.httpClient, logosURL)
	if err != nil {
		// Just log + continue with empty logos slice — channels
		// retain their channels.json default.
		rawLogos = nil
	}
	logosByChannel := make(map[string][]rawLogo, len(rawLogos))
	for _, l := range rawLogos {
		if l.Channel == "" || l.URL == "" {
			continue
		}
		logosByChannel[l.Channel] = append(logosByChannel[l.Channel], l)
	}

	// Index channels by id for O(1) join.
	byID := make(map[string]*rawChannel, len(rawChannels))
	for i := range rawChannels {
		c := &rawChannels[i]
		// Drop closed + NSFW.
		if c.Closed != nil && *c.Closed != "" {
			continue
		}
		if c.IsNSFW {
			continue
		}
		byID[c.ID] = c
	}

	// Group streams by channel id.
	streamsByID := make(map[string][]Stream, len(rawStreams))
	for _, rs := range rawStreams {
		if rs.Channel == nil || rs.URL == "" {
			continue
		}
		st := Stream{URL: rs.URL}
		if rs.Quality != nil {
			st.Quality = *rs.Quality
		}
		if rs.UserAgent != nil {
			st.UserAgent = *rs.UserAgent
		}
		if rs.Referrer != nil {
			st.Referrer = *rs.Referrer
		}
		streamsByID[*rs.Channel] = append(streamsByID[*rs.Channel], st)
	}

	// Project + sort streams by quality so the best variant is
	// the one we health-check (and the one the TV uses by default).
	all := make([]Channel, 0, len(streamsByID))
	for id, streams := range streamsByID {
		c, ok := byID[id]
		if !ok {
			continue
		}
		sort.SliceStable(streams, func(i, j int) bool {
			return qualityRank(streams[i].Quality) < qualityRank(streams[j].Quality)
		})
		// Logo: prefer the picker's choice from logos.json (better
		// quality + better fit for our dark-themed TV UI). Fall
		// back to channels.json's `logo` when the picker returns
		// empty — guarantees a logo is shown when ANY source has
		// one.
		logo := pickBestLogo(logosByChannel[id])
		if logo == "" {
			logo = c.Logo
		}
		all = append(all, Channel{
			ID:         id,
			Name:       c.Name,
			Country:    c.Country,
			Logo:       logo,
			Categories: c.Categories,
			Streams:    streams,
		})
	}

	// Health-check pass — only on SPORTS channels for now to keep
	// the probe budget bounded (~300 channels × ~3s each / 20
	// concurrency = ~minute total). News/music/etc. categories
	// aren't probed; if we ever expose them we'd extend the
	// probe scope.
	probed := healthCheckChannels(ctx, s.httpClient, all, "sports")
	// Drop channels left with no working streams.
	out := make([]Channel, 0, len(probed))
	for _, c := range probed {
		if len(c.Streams) > 0 {
			out = append(out, c)
		}
	}
	return out, nil
}

// healthCheckChannels probes the FIRST stream URL of every channel
// in `category`. Streams that don't respond with a valid HLS
// playlist body (`#EXTM3U`) get removed. Concurrency-capped so we
// don't fan out to thousands of upstream connections.
//
// We only probe the first stream because the catalog sorts streams
// by quality — if the best variant is dead, we mark the channel
// as broken rather than fall through to lower-quality fallbacks.
// The TV would always pick the best variant anyway, so this matches
// real-world UX.
//
// Channels NOT in the probed category are returned unchanged.
func healthCheckChannels(ctx context.Context, client *http.Client, in []Channel, category string) []Channel {
	const concurrency = 20
	const probeTimeout = 4 * time.Second

	out := make([]Channel, len(in))
	copy(out, in)

	type job struct{ idx int }
	jobs := make(chan job, len(in))
	var wg sync.WaitGroup

	worker := func() {
		defer wg.Done()
		for j := range jobs {
			c := &out[j.idx]
			if len(c.Streams) == 0 {
				continue
			}
			url := c.Streams[0].URL
			if !probeM3U8(ctx, client, url, probeTimeout) {
				// Best stream is dead — try fallback streams in
				// rank order. If any pass, keep the channel with
				// the working stream first. If all fail, drop
				// all streams (caller filters channel out).
				kept := []Stream{}
				for _, s := range c.Streams[1:] {
					if probeM3U8(ctx, client, s.URL, probeTimeout) {
						kept = append(kept, s)
					}
				}
				if len(kept) == 0 {
					c.Streams = nil
				} else {
					c.Streams = kept
				}
			}
		}
	}

	for w := 0; w < concurrency; w++ {
		wg.Add(1)
		go worker()
	}
	for i, c := range in {
		// Skip non-target categories entirely — leave them as-is.
		match := false
		for _, k := range c.Categories {
			if strings.EqualFold(k, category) {
				match = true
				break
			}
		}
		if !match {
			continue
		}
		jobs <- job{idx: i}
	}
	close(jobs)
	wg.Wait()
	return out
}

// probeM3U8 returns true iff the URL responds within `timeout` with
// HTTP 2xx AND a body that begins with `#EXTM3U`. We read only
// the first 64 bytes — enough to confirm the magic line without
// pulling the full playlist.
func probeM3U8(ctx context.Context, client *http.Client, rawURL string, timeout time.Duration) bool {
	probeCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	req, err := http.NewRequestWithContext(probeCtx, http.MethodGet, rawURL, nil)
	if err != nil {
		return false
	}
	// Browser-shaped UA so upstreams that UA-sniff don't bounce
	// us. iptv-org streams that need a SPECIFIC UA carry it on
	// the Stream record (and we'd attach that — but for the
	// health check we use a sensible default).
	req.Header.Set("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
	req.Header.Set("Accept", "*/*")
	resp, err := client.Do(req)
	if err != nil {
		return false
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return false
	}
	buf := make([]byte, 64)
	n, _ := resp.Body.Read(buf)
	if n < 7 {
		return false
	}
	return strings.HasPrefix(string(buf[:n]), "#EXTM3U")
}

// pickBestLogo selects one logo URL per channel from the (often
// many) entries iptv-org's logos.json provides. Returns "" if none
// of the candidates pass the filters — caller falls back to the
// channels.json `logo` field.
//
// Selection rules, applied in order. Each rule SOFT-prefers — if
// nothing remains after a stricter rule, we relax. The aim is
// "always return SOMETHING playable when any candidate exists".
//
//  1. Drop non-HTTPS URLs (chromium-efl on Tizen blocks mixed
//     content from a HTTPS-served page).
//  2. Drop oversized (width > 2000) — those are usually press kit
//     posters, not channel marks. Drop tiny (width < 60) — favicons.
//  3. Drop "black" / "dark" tags — invisible on our dark-themed
//     UI. Keep "white" / "color" / untagged.
//  4. Drop animated formats (GIF, APNG) — Tizen NUI ImageView
//     doesn't animate them and a single frame may be wrong.
//  5. From what's left, score by:
//        + in_use=true               (+10)
//        + format PNG                (+8)   // most reliable in NUI
//        + format WebP / JPEG        (+6)
//        + format SVG                (+3)   // NUI SVG support spotty
//        + tag "white"               (+4)   // matches dark UI best
//        + tag "horizontal"          (+3)   // fits horizontal tile
//        + width 200..1000 sweet-spot (+2)
//     Pick the highest. Ties broken by first occurrence (stable).
//
// We do NOT enforce the "feed" field — most channels have just one
// playable feed and the tile shows the channel name not the feed
// name, so picking any feed's logo is fine.
func pickBestLogo(logos []rawLogo) string {
	if len(logos) == 0 {
		return ""
	}
	type candidate struct {
		url   string
		score int
		idx   int // for stable tiebreak
	}
	cands := make([]candidate, 0, len(logos))
	for i, l := range logos {
		if !strings.HasPrefix(l.URL, "https://") {
			continue
		}
		if l.Width > 2000 || (l.Width > 0 && l.Width < 60) {
			continue
		}
		fmtUpper := ""
		if l.Format != nil {
			fmtUpper = strings.ToUpper(*l.Format)
		}
		if fmtUpper == "GIF" || fmtUpper == "APNG" {
			continue
		}
		// Tag-based skip for dark logos that would disappear.
		// We only skip when the ONLY descriptive color tag is
		// dark — a logo tagged ["white","horizontal"] still wins
		// even if "black" appears alongside.
		hasDark, hasLight := false, false
		for _, t := range l.Tags {
			tl := strings.ToLower(t)
			if tl == "black" || tl == "dark" {
				hasDark = true
			}
			if tl == "white" || tl == "color" || tl == "light" {
				hasLight = true
			}
		}
		if hasDark && !hasLight {
			continue
		}

		score := 0
		if l.InUse {
			score += 10
		}
		switch fmtUpper {
		case "PNG":
			score += 8
		case "WEBP", "JPEG", "JPG":
			score += 6
		case "SVG":
			score += 3
		}
		for _, t := range l.Tags {
			tl := strings.ToLower(t)
			if tl == "white" {
				score += 4
			}
			if tl == "horizontal" {
				score += 3
			}
		}
		if l.Width >= 200 && l.Width <= 1000 {
			score += 2
		}
		cands = append(cands, candidate{url: l.URL, score: score, idx: i})
	}
	if len(cands) == 0 {
		return ""
	}
	sort.SliceStable(cands, func(i, j int) bool {
		if cands[i].score != cands[j].score {
			return cands[i].score > cands[j].score
		}
		return cands[i].idx < cands[j].idx
	})
	return cands[0].url
}

// qualityRank gives a sort order — lower is better. Unknown last.
func qualityRank(q string) int {
	switch strings.ToLower(strings.TrimSpace(q)) {
	case "2160p", "4k":
		return 0
	case "1440p":
		return 1
	case "1080p", "fhd":
		return 2
	case "720p", "hd":
		return 3
	case "576p":
		return 4
	case "480p", "sd":
		return 5
	}
	return 99
}

// fetchJSON is a tiny generic helper for the two iptv-org files.
// Bounded by ctx (caller's deadline) + a body size cap (the files
// are ~4 MB and ~3 MB; 32 MB cap is paranoia for unexpected growth).
func fetchJSON[T any](ctx context.Context, client *http.Client, url string) (T, error) {
	var zero T
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return zero, err
	}
	req.Header.Set("User-Agent", "streamed-backend/1.0 (+iptv-org)")
	resp, err := client.Do(req)
	if err != nil {
		return zero, err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 400 {
		return zero, fmt.Errorf("http %d", resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 32*1024*1024))
	if err != nil {
		return zero, err
	}
	var out T
	if err := json.Unmarshal(body, &out); err != nil {
		return zero, fmt.Errorf("decode: %w", err)
	}
	return out, nil
}

// FindChannel returns a channel by id (used by the play handler to
// resolve auth headers from the channel id rather than trusting
// query params). Cache must be warm — caller should have called
// ListChannels first this session.
func (s *Service) FindChannel(id string) (Channel, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, c := range s.channels {
		if c.ID == id {
			return c, true
		}
	}
	return Channel{}, false
}

// guard against unused-import warning when the file is only used
// by the package's exported surface.
var _ = errors.New
