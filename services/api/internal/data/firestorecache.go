package data

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"time"

	"cloud.google.com/go/firestore"
	"github.com/abindlish/streamed-backend/internal/api"
)

const teamCacheTTL = 24 * time.Hour
const leagueIDsCacheTTL = 30 * 24 * time.Hour
const leagueTableCacheTTL = 6 * time.Hour

// FirestoreCacheSource wraps another Source and caches TeamStandings
// and NextMatch in Firestore under teams/{teamId} with a 24-hour TTL.
//
// Cache hits are served immediately; misses delegate to the inner
// source and the fresh result is written back asynchronously so the
// caller is not blocked by the write.
//
// Firestore errors (read OR write) are logged and suppressed — the
// inner source is always the authoritative fallback.
//
// When readOnly is true the source NEVER delegates to inner on miss
// and NEVER writes back. Misses return ErrNotFound. This is the mode
// used by the live request path when a separate cron is responsible
// for populating Firestore.
type FirestoreCacheSource struct {
	fs       *firestore.Client
	inner    Source
	logger   *slog.Logger
	readOnly bool
}

func NewFirestoreCacheSource(fs *firestore.Client, inner Source, logger *slog.Logger) *FirestoreCacheSource {
	if logger == nil {
		logger = slog.Default()
	}
	return &FirestoreCacheSource{fs: fs, inner: inner, logger: logger}
}

// SetReadOnly toggles read-only mode. In read-only mode cache misses
// return ErrNotFound rather than delegating to the inner source, and no
// writes happen. Wire this true in the request path when a background
// cron is the sole writer.
func (f *FirestoreCacheSource) SetReadOnly(v bool) { f.readOnly = v }

func (f *FirestoreCacheSource) Name() string {
	return "firestore-cache(" + f.inner.Name() + ")"
}

func (f *FirestoreCacheSource) TeamStandings(ctx context.Context, teamID string) (api.TeamStandings, error) {
	if cached, ok := f.getCachedStandings(ctx, teamID); ok {
		f.logger.Info("firestore cache hit", "type", "standings", "team", teamID)
		return *cached, nil
	}
	if f.readOnly {
		return api.TeamStandings{}, ErrNotFound
	}
	result, err := f.inner.TeamStandings(ctx, teamID)
	if err != nil {
		return result, err
	}
	go f.writeStandings(teamID, result)
	return result, nil
}

func (f *FirestoreCacheSource) NextMatch(ctx context.Context, teamID string) (api.NextMatch, error) {
	if cached, ok := f.getCachedNextMatch(ctx, teamID); ok {
		f.logger.Info("firestore cache hit", "type", "next-match", "team", teamID)
		return *cached, nil
	}
	if f.readOnly {
		return api.NextMatch{}, ErrNotFound
	}
	result, err := f.inner.NextMatch(ctx, teamID)
	if err != nil {
		return result, err
	}
	go f.writeNextMatch(teamID, result)
	return result, nil
}

func (f *FirestoreCacheSource) LeagueStandings(ctx context.Context, leagueID string) (api.LeagueStandings, error) {
	if cached, ok := f.getCachedLeagueStandings(ctx, leagueID); ok {
		f.logger.Info("firestore cache hit", "type", "league-standings", "league", leagueID)
		return *cached, nil
	}
	if f.readOnly {
		return api.LeagueStandings{}, ErrNotFound
	}
	result, err := f.inner.LeagueStandings(ctx, leagueID)
	if err != nil {
		return result, err
	}
	go f.writeLeagueStandings(leagueID, result)
	return result, nil
}

func (f *FirestoreCacheSource) TeamLeagues(ctx context.Context, teamID string) ([]string, error) {
	if cached, ok := f.getCachedLeagueIDs(ctx, teamID); ok {
		f.logger.Info("firestore cache hit", "type", "league-ids", "team", teamID)
		return cached, nil
	}
	if f.readOnly {
		return nil, ErrNotFound
	}
	result, err := f.inner.TeamLeagues(ctx, teamID)
	if err != nil {
		return result, err
	}
	go f.writeLeagueIDs(teamID, result)
	return result, nil
}

func (f *FirestoreCacheSource) AllTeamStandings(ctx context.Context, teamID string) ([]api.TeamStandings, error) {
	leagueIDs, err := f.TeamLeagues(ctx, teamID)
	if err != nil || len(leagueIDs) == 0 {
		if f.readOnly {
			// Fall back to the per-team standings doc directly. If that
			// also misses we 404 — no upstream call.
			if cached, ok := f.getCachedStandings(ctx, teamID); ok {
				return []api.TeamStandings{*cached}, nil
			}
			return nil, ErrNotFound
		}
		primary, fErr := f.inner.TeamStandings(ctx, teamID)
		if fErr != nil {
			return nil, fErr
		}
		return []api.TeamStandings{primary}, nil
	}

	var result []api.TeamStandings
	for _, lid := range leagueIDs {
		table, err := f.LeagueStandings(ctx, lid)
		if err != nil {
			f.logger.Warn("cache: all-standings: league fetch failed, skipping", "team", teamID, "league", lid, "err", err)
			continue
		}
		row := findTeamRow(table.Table, teamID)
		if row == nil {
			f.logger.Warn("cache: all-standings: team not in league table, skipping", "team", teamID, "league", lid)
			continue
		}
		result = append(result, api.TeamStandings{
			TeamID:     teamID,
			League:     table.Name,
			Position:   row.Position,
			Played:     row.Played,
			Wins:       row.Wins,
			Draws:      row.Draws,
			Losses:     row.Losses,
			Points:     row.Points,
			RecentForm: row.RecentForm,
			Source:     table.Source,
		})
	}

	if len(result) == 0 {
		if f.readOnly {
			if cached, ok := f.getCachedStandings(ctx, teamID); ok {
				return []api.TeamStandings{*cached}, nil
			}
			return nil, ErrNotFound
		}
		primary, fErr := f.inner.TeamStandings(ctx, teamID)
		if fErr != nil {
			return nil, fErr
		}
		return []api.TeamStandings{primary}, nil
	}

	return result, nil
}

func (f *FirestoreCacheSource) LookupTeamByName(ctx context.Context, name string) (*api.TeamLookup, error) {
	return f.inner.LookupTeamByName(ctx, name)
}

// teamDoc is the flat Firestore document stored at teams/{teamId}.
// JSON is stored as strings so we don't fight Firestore's type system
// with nested structs that have Go-specific json tags.
type teamDoc struct {
	StandingsJSON      string    `firestore:"standingsJson"`
	NextMatchJSON      string    `firestore:"nextMatchJson"`
	StandingsUpdatedAt time.Time `firestore:"standingsUpdatedAt"`
	NextMatchUpdatedAt time.Time `firestore:"nextMatchUpdatedAt"`
	LeagueIDs          []string  `firestore:"leagueIds"`
	LeagueIDsUpdatedAt time.Time `firestore:"leagueIdsUpdatedAt"`
}

// leagueDoc is the Firestore document stored at leagues/{leagueId},
// shared across all teams in a league.
type leagueDoc struct {
	TableJSON string    `firestore:"tableJson"`
	Name      string    `firestore:"name"`
	UpdatedAt time.Time `firestore:"updatedAt"`
}

func (f *FirestoreCacheSource) getCachedStandings(ctx context.Context, teamID string) (*api.TeamStandings, bool) {
	snap, err := f.fs.Collection("teams").Doc(teamID).Get(ctx)
	if err != nil {
		return nil, false
	}
	var doc teamDoc
	if err := snap.DataTo(&doc); err != nil {
		return nil, false
	}
	if doc.StandingsJSON == "" || time.Since(doc.StandingsUpdatedAt) > teamCacheTTL {
		return nil, false
	}
	var s api.TeamStandings
	if err := json.Unmarshal([]byte(doc.StandingsJSON), &s); err != nil {
		return nil, false
	}
	s.Source = api.SourceFirestoreCache
	return &s, true
}

func (f *FirestoreCacheSource) getCachedNextMatch(ctx context.Context, teamID string) (*api.NextMatch, bool) {
	snap, err := f.fs.Collection("teams").Doc(teamID).Get(ctx)
	if err != nil {
		return nil, false
	}
	var doc teamDoc
	if err := snap.DataTo(&doc); err != nil {
		return nil, false
	}
	if doc.NextMatchJSON == "" || time.Since(doc.NextMatchUpdatedAt) > teamCacheTTL {
		return nil, false
	}
	var n api.NextMatch
	if err := json.Unmarshal([]byte(doc.NextMatchJSON), &n); err != nil {
		return nil, false
	}
	n.Source = api.SourceFirestoreCache
	return &n, true
}

func (f *FirestoreCacheSource) getCachedLeagueIDs(ctx context.Context, teamID string) ([]string, bool) {
	snap, err := f.fs.Collection("teams").Doc(teamID).Get(ctx)
	if err != nil {
		return nil, false
	}
	var doc teamDoc
	if err := snap.DataTo(&doc); err != nil {
		return nil, false
	}
	if len(doc.LeagueIDs) == 0 || time.Since(doc.LeagueIDsUpdatedAt) > leagueIDsCacheTTL {
		return nil, false
	}
	return doc.LeagueIDs, true
}

func (f *FirestoreCacheSource) getCachedLeagueStandings(ctx context.Context, leagueID string) (*api.LeagueStandings, bool) {
	snap, err := f.fs.Collection("leagues").Doc(leagueID).Get(ctx)
	if err != nil {
		return nil, false
	}
	var doc leagueDoc
	if err := snap.DataTo(&doc); err != nil {
		return nil, false
	}
	if doc.TableJSON == "" || time.Since(doc.UpdatedAt) > leagueTableCacheTTL {
		return nil, false
	}
	var table []api.LeagueTableRow
	if err := json.Unmarshal([]byte(doc.TableJSON), &table); err != nil {
		return nil, false
	}
	ls := &api.LeagueStandings{
		LeagueID: leagueID,
		Name:     doc.Name,
		Table:    table,
		Source:   api.SourceFirestoreCache,
	}
	return ls, true
}

func (f *FirestoreCacheSource) writeStandings(teamID string, s api.TeamStandings) {
	b, err := json.Marshal(s)
	if err != nil {
		f.logger.Warn("cache: marshal standings failed", "team", teamID, "err", err)
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	_, err = f.fs.Collection("teams").Doc(teamID).Set(ctx, map[string]any{
		"standingsJson":      string(b),
		"standingsUpdatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	if err != nil {
		f.logger.Warn("cache: write standings failed", "team", teamID, "err", err)
	}
}

func (f *FirestoreCacheSource) writeNextMatch(teamID string, n api.NextMatch) {
	b, err := json.Marshal(n)
	if err != nil {
		f.logger.Warn("cache: marshal next-match failed", "team", teamID, "err", err)
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	_, err = f.fs.Collection("teams").Doc(teamID).Set(ctx, map[string]any{
		"nextMatchJson":      string(b),
		"nextMatchUpdatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	if err != nil {
		f.logger.Warn("cache: write next-match failed", "team", teamID, "err", err)
	}
}

func (f *FirestoreCacheSource) writeLeagueIDs(teamID string, ids []string) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	_, err := f.fs.Collection("teams").Doc(teamID).Set(ctx, map[string]any{
		"leagueIds":          ids,
		"leagueIdsUpdatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	if err != nil {
		f.logger.Warn("cache: write league IDs failed", "team", teamID, "err", err)
	}
}

func (f *FirestoreCacheSource) writeLeagueStandings(leagueID string, ls api.LeagueStandings) {
	b, err := json.Marshal(ls.Table)
	if err != nil {
		f.logger.Warn("cache: marshal league table failed", "league", leagueID, "err", err)
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	_, err = f.fs.Collection("leagues").Doc(leagueID).Set(ctx, map[string]any{
		"tableJson": string(b),
		"name":      ls.Name,
		"updatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	if err != nil {
		f.logger.Warn("cache: write league standings failed", "league", leagueID, "err", err)
	}
}

// --- Public synchronous writers used by the cron --------------------------
// These mirror the private async writeX methods but accept a caller-owned
// ctx and surface errors so the cron can log per-write outcomes. They use
// the same schema (teams/{id}, leagues/{id}) so reads via this source see
// the writes regardless of who wrote them.

// WriteTeamStandings sets the team's primary standings doc.
func (f *FirestoreCacheSource) WriteTeamStandings(ctx context.Context, teamID string, s api.TeamStandings) error {
	b, err := json.Marshal(s)
	if err != nil {
		return fmt.Errorf("marshal standings: %w", err)
	}
	_, err = f.fs.Collection("teams").Doc(teamID).Set(ctx, map[string]any{
		"standingsJson":      string(b),
		"standingsUpdatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	return err
}

// WriteTeamNextMatch sets the team's next-match doc.
func (f *FirestoreCacheSource) WriteTeamNextMatch(ctx context.Context, teamID string, n api.NextMatch) error {
	b, err := json.Marshal(n)
	if err != nil {
		return fmt.Errorf("marshal next-match: %w", err)
	}
	_, err = f.fs.Collection("teams").Doc(teamID).Set(ctx, map[string]any{
		"nextMatchJson":      string(b),
		"nextMatchUpdatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	return err
}

// WriteLeagueTable replaces the league's standings doc.
func (f *FirestoreCacheSource) WriteLeagueTable(ctx context.Context, leagueID string, ls api.LeagueStandings) error {
	b, err := json.Marshal(ls.Table)
	if err != nil {
		return fmt.Errorf("marshal league table: %w", err)
	}
	_, err = f.fs.Collection("leagues").Doc(leagueID).Set(ctx, map[string]any{
		"tableJson": string(b),
		"name":      ls.Name,
		"updatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	return err
}

// AppendTeamLeague adds leagueID to the team's leagueIds array using
// ArrayUnion (idempotent — duplicate adds are no-ops). Also bumps the
// leagueIdsUpdatedAt timestamp so cache freshness checks pass.
func (f *FirestoreCacheSource) AppendTeamLeague(ctx context.Context, teamID, leagueID string) error {
	_, err := f.fs.Collection("teams").Doc(teamID).Set(ctx, map[string]any{
		"leagueIds":          firestore.ArrayUnion(leagueID),
		"leagueIdsUpdatedAt": time.Now().UTC(),
	}, firestore.MergeAll)
	return err
}

// --- Generic feed read/write ----------------------------------------------
// Feeds are prebuilt views (e.g. top-N upcoming international cricket
// matches) the cron writes once per tick, the HTTP layer reads on every
// request. Schema: feeds/{feedID} = { payloadJson: string, updatedAt: ts }.
// Caller picks the payload type; this wrapper just JSON-marshals into a
// string column so we don't have to teach Firestore each new shape.

// feedDoc is the Firestore document stored at feeds/{feedID}.
type feedDoc struct {
	PayloadJSON string    `firestore:"payloadJson"`
	UpdatedAt   time.Time `firestore:"updatedAt"`
}

// WriteFeed sets the feed doc at feeds/{feedID}. Payload is JSON-marshalled
// and stored under "payloadJson" with an "updatedAt" timestamp. Used by
// the cron to publish prebuilt views (e.g. top-N upcoming matches) the
// HTTP layer reads back via GetFeed.
//
// Bypasses the readOnly flag — this is the writer-side method the cron
// calls; the readOnly gate exists for the request-path Source methods.
func (f *FirestoreCacheSource) WriteFeed(ctx context.Context, feedID string, payload any) error {
	b, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("marshal feed %q: %w", feedID, err)
	}
	_, err = f.fs.Collection("feeds").Doc(feedID).Set(ctx, map[string]any{
		"payloadJson": string(b),
		"updatedAt":   time.Now().UTC(),
	}, firestore.MergeAll)
	return err
}

// GetFeed reads feeds/{feedID} and unmarshals payloadJson into out.
// Returns ErrNotFound if the doc is missing OR payloadJson is empty.
// Caller is responsible for the type assertion shape (out must be a pointer).
//
// Always serves regardless of readOnly — readers don't care who wrote.
func (f *FirestoreCacheSource) GetFeed(ctx context.Context, feedID string, out any) error {
	snap, err := f.fs.Collection("feeds").Doc(feedID).Get(ctx)
	if err != nil {
		return ErrNotFound
	}
	var doc feedDoc
	if err := snap.DataTo(&doc); err != nil {
		return ErrNotFound
	}
	if doc.PayloadJSON == "" {
		return ErrNotFound
	}
	if err := json.Unmarshal([]byte(doc.PayloadJSON), out); err != nil {
		return fmt.Errorf("unmarshal feed %q: %w", feedID, err)
	}
	return nil
}
