// Package cron, sports.go: league-driven and series-driven background
// pre-seeding of the Firestore cache.
//
// The sports cron is the SOLE writer to Firestore in the new
// architecture. The HTTP request path runs FirestoreCacheSource in
// read-only mode and 404s on cache miss, so this cron's job is to make
// sure the cache is populated for every team the TV app might ask
// about.
//
// Strategy is league-driven (not team-driven) to minimize upstream API
// calls:
//
//   - Football: per league in FootballLeagues, fetch the standings table
//     (1 call) and all scheduled fixtures (1 call). The standings call
//     gives every team's row in one shot; the fixtures call gives every
//     team's next match in one shot. ~12 API calls per run for the
//     top-5 + UCL — well under football-data.org's 100/day cap.
//
//   - Cricket: per series query in CricketSeriesQueries, search for the
//     active series (1 call, 24h-cached) and pull its full match list
//     via /series_info (1 call, 6h-cached). One series_info call covers
//     every team in the series — IPL pulls all 10 franchises with one
//     hit.
//
// All writes go through the public Write* methods on
// FirestoreCacheSource so the schema stays in one place.
package cron

import (
	"context"
	"log/slog"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/abindlish/streamed-backend/internal/api"
	"github.com/abindlish/streamed-backend/internal/data"
)

// DefaultSportsInterval is how often the sports cron fires once the
// initial run completes. 6h is fresh enough for league standings +
// next-match lookahead while staying well under free-tier quotas.
const DefaultSportsInterval = 6 * time.Hour

// SportsConfig wires a SportsCron. Either Football or Cricket may be
// nil — that branch is then skipped each tick.
type SportsConfig struct {
	// Cache is the Firestore writer. Required.
	Cache *data.FirestoreCacheSource

	// Football, when non-nil, enables the football branch. Pre-seeds
	// standings + next-match for every team in each canonical league
	// listed in FootballLeagues.
	Football        *data.FootballDataSource
	FootballLeagues []string // canonical IDs: ["epl","laliga","seriea","bundesliga","ligue1","ucl"]

	// Cricket, when non-nil, enables the cricket branch. For each
	// query in CricketSeriesQueries we search /series, pick the most
	// recent series whose name matches, then call /series_info to
	// derive next-match-per-team.
	Cricket              *data.CricAPISource
	CricketSeriesQueries []string // e.g. ["Indian Premier League 2026"]

	Interval time.Duration
	Logger   *slog.Logger
}

// SportsCron drives the league + series pre-seeding loop.
type SportsCron struct {
	cfg     SportsConfig
	logger  *slog.Logger
	trigger chan struct{}

	mu      sync.Mutex
	running bool
}

func NewSports(cfg SportsConfig) *SportsCron {
	if cfg.Interval <= 0 {
		cfg.Interval = DefaultSportsInterval
	}
	if cfg.Logger == nil {
		cfg.Logger = slog.Default()
	}
	return &SportsCron{
		cfg:     cfg,
		logger:  cfg.Logger,
		trigger: make(chan struct{}, 1),
	}
}

// Start launches the goroutine. Runs once immediately, then ticks every
// Interval. Stops when ctx is cancelled.
func (s *SportsCron) Start(ctx context.Context) {
	go s.loop(ctx)
}

// Trigger kicks an extra run if one isn't already pending. Non-blocking.
func (s *SportsCron) Trigger() bool {
	select {
	case s.trigger <- struct{}{}:
		return true
	default:
		return false
	}
}

func (s *SportsCron) loop(ctx context.Context) {
	s.logger.Info("sports cron starting", "interval", s.cfg.Interval,
		"footballLeagues", len(s.cfg.FootballLeagues),
		"cricketQueries", len(s.cfg.CricketSeriesQueries),
	)
	s.runOnce(ctx)

	ticker := time.NewTicker(s.cfg.Interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			s.logger.Info("sports cron stopped", "reason", ctx.Err())
			return
		case <-ticker.C:
			s.runOnce(ctx)
		case <-s.trigger:
			s.runOnce(ctx)
		}
	}
}

// runOnce executes one full pass: football branch then cricket branch.
// Errors in either branch are logged, never propagated — the loop must
// keep running.
func (s *SportsCron) runOnce(ctx context.Context) {
	s.mu.Lock()
	if s.running {
		s.mu.Unlock()
		s.logger.Info("sports cron run skipped: previous run still in progress")
		return
	}
	s.running = true
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		s.running = false
		s.mu.Unlock()
	}()

	start := time.Now()
	s.logger.Info("sports cron run started")

	if s.cfg.Football != nil && s.cfg.Cache != nil {
		s.runFootball(ctx)
	}
	if s.cfg.Cricket != nil && s.cfg.Cache != nil {
		s.runCricket(ctx)
	}

	s.logger.Info("sports cron run finished", "elapsedMs", time.Since(start).Milliseconds())
}

// runFootball iterates the configured leagues. For each: write the
// league's full standings table, append the league ID to every team's
// leagueIds (ArrayUnion), write each team's primary standings row, and
// derive + write next-match for every team in the league's upcoming
// fixtures.
//
// Iteration order matters for the "primary standings" doc: the LAST
// league wins, so list continental competitions FIRST and domestic
// leagues LAST in FootballLeagues.
func (s *SportsCron) runFootball(ctx context.Context) {
	for _, leagueID := range s.cfg.FootballLeagues {
		ls, err := s.cfg.Football.LeagueStandings(ctx, leagueID)
		if err != nil {
			s.logger.Warn("sports cron: league standings fetch failed", "league", leagueID, "err", err)
		} else {
			if err := s.cfg.Cache.WriteLeagueTable(ctx, leagueID, ls); err != nil {
				s.logger.Warn("sports cron: write league table failed", "league", leagueID, "err", err)
			}
			for _, row := range ls.Table {
				if row.TeamID == "" {
					continue
				}
				if err := s.cfg.Cache.AppendTeamLeague(ctx, row.TeamID, leagueID); err != nil {
					s.logger.Warn("sports cron: append team league failed", "team", row.TeamID, "league", leagueID, "err", err)
				}
				// Primary standings doc — last write wins, so domestic-league
				// iteration order in FootballLeagues controls which row sticks.
				if err := s.cfg.Cache.WriteTeamStandings(ctx, row.TeamID, teamStandingsFromLeagueRow(row, ls)); err != nil {
					s.logger.Warn("sports cron: write team standings failed", "team", row.TeamID, "league", leagueID, "err", err)
				}
			}
		}

		nextByTeam, err := s.cfg.Football.LeagueNextMatchPerTeam(ctx, leagueID)
		if err != nil {
			s.logger.Warn("sports cron: league fixtures fetch failed", "league", leagueID, "err", err)
			continue
		}
		for teamID, nm := range nextByTeam {
			if err := s.cfg.Cache.WriteTeamNextMatch(ctx, teamID, nm); err != nil {
				s.logger.Warn("sports cron: write next-match failed", "team", teamID, "league", leagueID, "err", err)
			}
		}
		s.logger.Info("sports cron: football league done", "league", leagueID, "tableRows", len(ls.Table), "nextMatches", len(nextByTeam))
	}
}

// runCricket iterates the configured series queries. For each:
//
//  1. Search /series for matching catalog entries (24h-cached).
//  2. Pick the most recent active series (start <= now <= end, or the
//     most recently-started if none active right now).
//  3. Pull /series_info for that series (6h-cached) and write per-team
//     next-match for each team that maps to a canonical ID.
//  4. For IPL queries (and other league-format tournaments), additionally
//     derive a standings table by parsing match results, write it to
//     leagues/ipl, and per-team standings rows for every IPL franchise.
//  5. For non-IPL international tour queries, gather upcoming matches
//     across queries; after the loop we publish the top-N as a single
//     prebuilt feed at feeds/intl-cricket-upcoming.
func (s *SportsCron) runCricket(ctx context.Context) {
	now := time.Now().UTC()
	var intlMatches []api.LiveCricketMatch // accumulator for the international feed
	for _, query := range s.cfg.CricketSeriesQueries {
		list, err := s.cfg.Cricket.SearchSeries(ctx, query)
		if err != nil {
			s.logger.Warn("sports cron: cricket series search failed", "query", query, "err", err)
			continue
		}
		active := pickActiveSeries(list, now)
		if active == "" {
			s.logger.Info("sports cron: no active cricket series for query", "query", query, "candidates", len(list))
			continue
		}
		nextByTeam, err := s.cfg.Cricket.SeriesNextMatchPerTeam(ctx, active, now)
		if err != nil {
			s.logger.Warn("sports cron: cricket series_info failed", "query", query, "seriesId", active, "err", err)
			continue
		}
		for teamID, nm := range nextByTeam {
			if err := s.cfg.Cache.WriteTeamNextMatch(ctx, teamID, nm); err != nil {
				s.logger.Warn("sports cron: write cricket next-match failed", "team", teamID, "err", err)
			}
		}

		var standingsRows int
		if isIPLQuery(query) {
			standings, sErr := s.cfg.Cricket.SeriesStandings(ctx, active)
			if sErr != nil {
				s.logger.Warn("sports cron: cricket series standings failed", "query", query, "seriesId", active, "err", sErr)
			} else if len(standings.Table) > 0 {
				const leagueID = "ipl"
				if err := s.cfg.Cache.WriteLeagueTable(ctx, leagueID, standings); err != nil {
					s.logger.Warn("sports cron: write ipl table failed", "err", err)
				}
				for _, row := range standings.Table {
					if row.TeamID == "" {
						continue
					}
					if err := s.cfg.Cache.AppendTeamLeague(ctx, row.TeamID, leagueID); err != nil {
						s.logger.Warn("sports cron: append ipl league failed", "team", row.TeamID, "err", err)
					}
					if err := s.cfg.Cache.WriteTeamStandings(ctx, row.TeamID, teamStandingsFromLeagueRow(row, standings)); err != nil {
						s.logger.Warn("sports cron: write ipl team standings failed", "team", row.TeamID, "err", err)
					}
				}
				standingsRows = len(standings.Table)
			}
		} else {
			// International tour: gather upcoming for the global feed.
			upcoming, uErr := s.cfg.Cricket.SeriesUpcomingLive(ctx, active, now)
			if uErr != nil {
				s.logger.Warn("sports cron: cricket series upcoming failed", "query", query, "seriesId", active, "err", uErr)
			} else {
				intlMatches = append(intlMatches, upcoming...)
			}
		}

		s.logger.Info("sports cron: cricket series done", "query", query, "seriesId", active, "nextMatches", len(nextByTeam), "standingsRows", standingsRows)
	}

	// Publish the prebuilt top-N international upcoming feed.
	if len(intlMatches) > 0 {
		// Re-sort because we appended across queries; cap at 10.
		sort.SliceStable(intlMatches, func(i, j int) bool { return intlMatches[i].StartTime < intlMatches[j].StartTime })
		if len(intlMatches) > intlCricketFeedLimit {
			intlMatches = intlMatches[:intlCricketFeedLimit]
		}
		feed := api.CricketUpcomingFeed{
			Matches:   intlMatches,
			UpdatedAt: now.Format(time.RFC3339),
			Source:    "cricapi",
		}
		if err := s.cfg.Cache.WriteFeed(ctx, intlCricketFeedID, feed); err != nil {
			s.logger.Warn("sports cron: write intl-cricket feed failed", "err", err)
		} else {
			s.logger.Info("sports cron: intl-cricket feed written", "matches", len(intlMatches))
		}
	}
}

// isIPLQuery returns true if the query refers to the Indian Premier
// League. Used to gate the standings derivation — only league-format
// tournaments get a meaningful standings table from match results.
func isIPLQuery(q string) bool {
	return strings.HasPrefix(strings.ToLower(strings.TrimSpace(q)), "indian premier league")
}

// intlCricketFeedID is the Firestore feed key for the prebuilt "top
// upcoming international cricket matches" view.
const intlCricketFeedID = "intl-cricket-upcoming"

// intlCricketFeedLimit caps the published top-N feed size.
const intlCricketFeedLimit = 10

// pickActiveSeries chooses the best series ID from a search result.
// Prefers a series whose [startDate, endDate] window contains `now`.
// Falls back to the most recently-started series if none is currently
// running (handles the off-season gap before next year's tournament).
//
// Returns empty if the list is empty or no parseable series found.
func pickActiveSeries(list []data.CricSeries, now time.Time) string {
	const layout = "2006-01-02"
	var bestActiveID string
	var bestActiveStart time.Time
	var fallbackID string
	var fallbackStart time.Time

	for _, s := range list {
		if s.ID == "" {
			continue
		}
		start, err := time.Parse(layout, s.StartDate)
		if err != nil {
			continue
		}
		end, err := time.Parse(layout, s.EndDate)
		if err != nil {
			// EndDate occasionally comes back as "Mar 14" without a year
			// for not-yet-finalised series. Treat as far-future so the
			// active check still considers the start date.
			end = start.AddDate(1, 0, 0)
		}
		if !now.Before(start) && !now.After(end.AddDate(0, 0, 1)) {
			if bestActiveID == "" || start.After(bestActiveStart) {
				bestActiveID = s.ID
				bestActiveStart = start
			}
		}
		if start.Before(now) && (fallbackID == "" || start.After(fallbackStart)) {
			fallbackID = s.ID
			fallbackStart = start
		}
	}
	if bestActiveID != "" {
		return bestActiveID
	}
	return fallbackID
}

// teamStandingsFromLeagueRow lifts a LeagueTableRow into the per-team
// TeamStandings shape used by the /standings endpoint. The League name
// comes from the parent table so the row knows which competition it
// belongs to.
func teamStandingsFromLeagueRow(row api.LeagueTableRow, ls api.LeagueStandings) api.TeamStandings {
	return api.TeamStandings{
		TeamID:     row.TeamID,
		League:     ls.Name,
		Position:   row.Position,
		Played:     row.Played,
		Wins:       row.Wins,
		Draws:      row.Draws,
		Losses:     row.Losses,
		Points:     row.Points,
		RecentForm: row.RecentForm,
		Source:     ls.Source,
	}
}
