// Package profiles owns viewer-profile + per-profile preferences
// storage. Each authenticated user has 0..MaxProfiles profile docs;
// each profile bundles display info (name, avatar) and the user's
// per-profile preferences (favorite sports/teams, last viewed sport).
//
// Firestore layout:
//   users/{userId}/profiles/{profileId} → profile doc
//   users/{userId}.activeProfileId      → currently-selected profile
//
// All operations are scoped by userId; the HTTP layer extracts userId
// from the auth middleware.
package profiles

import (
	"context"
	"errors"
	"fmt"
	"time"

	"cloud.google.com/go/firestore"
	"github.com/google/uuid"
	"google.golang.org/api/iterator"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// MaxProfiles mirrors the TV-side ProfileService cap. Enforced
// server-side on Create — the TV cap is for UX (don't show "Add
// profile" tile); this is the load-bearing one.
const MaxProfiles = 5

// Profile is one viewer slot under a user account. Mirrors the TV's
// Profile model 1:1 so the JSON shape is directly consumable by the
// TV without translation.
type Profile struct {
	// ID is the Firestore doc ID. Tagged "-" so it isn't written
	// as a field; populated when reading via snap.Ref.ID.
	ID                string   `firestore:"-" json:"id"`
	Name              string   `firestore:"name" json:"name"`
	AvatarR           float64  `firestore:"avatarR" json:"avatarR"`
	AvatarG           float64  `firestore:"avatarG" json:"avatarG"`
	AvatarB           float64  `firestore:"avatarB" json:"avatarB"`
	AvatarImage       string   `firestore:"avatarImage,omitempty" json:"avatarImage"`
	CreatedUtcMs      int64    `firestore:"createdUtcMs" json:"createdUtcMs"`
	FavoriteSports    []string `firestore:"favoriteSports,omitempty" json:"favoriteSports"`
	FavoriteTeams     []string `firestore:"favoriteTeams,omitempty" json:"favoriteTeams"`
	LastSelectedSport string   `firestore:"lastSelectedSport,omitempty" json:"lastSelectedSport"`
}

// ListResult is what GET /v1/profiles returns. ActiveProfileID may
// be empty when the user has no profiles, or when the persisted
// active id no longer resolves to a profile (e.g. after delete).
type ListResult struct {
	Profiles        []Profile `json:"profiles"`
	ActiveProfileID string    `json:"activeProfileId"`
}

// Common sentinel errors. The HTTP layer maps these to status codes.
var (
	ErrNotFound       = errors.New("profile not found")
	ErrLimitReached   = fmt.Errorf("profile limit reached (max %d)", MaxProfiles)
	ErrInvalidPayload = errors.New("invalid profile payload")
)

// Service wraps a Firestore client with the user-scoped CRUD ops the
// HTTP handlers need.
type Service struct {
	client *firestore.Client
}

func New(client *firestore.Client) *Service {
	return &Service{client: client}
}

// ---- Collection helpers ----

const (
	collectionUsers    = "users"
	subcollectionProfs = "profiles"
)

func (s *Service) profilesRef(userID string) *firestore.CollectionRef {
	return s.client.Collection(collectionUsers).Doc(userID).Collection(subcollectionProfs)
}

func (s *Service) userRef(userID string) *firestore.DocumentRef {
	return s.client.Collection(collectionUsers).Doc(userID)
}

// ---- CRUD ----

// List returns all profiles for the user plus their currently-active
// profile id. Empty list + empty active id is a valid first-launch
// state; the TV will show the picker with just the "Add profile"
// tile.
func (s *Service) List(ctx context.Context, userID string) (*ListResult, error) {
	if userID == "" {
		return nil, errors.New("profiles: userID is required")
	}

	iter := s.profilesRef(userID).Documents(ctx)
	defer iter.Stop()
	out := make([]Profile, 0, 4)
	for {
		snap, err := iter.Next()
		if errors.Is(err, iterator.Done) {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("list profiles: %w", err)
		}
		var p Profile
		if err := snap.DataTo(&p); err != nil {
			return nil, fmt.Errorf("decode profile %s: %w", snap.Ref.ID, err)
		}
		p.ID = snap.Ref.ID
		out = append(out, p)
	}

	activeID, err := s.GetActiveProfileID(ctx, userID)
	if err != nil {
		return nil, err
	}
	// Validate the persisted active id still resolves — drop it on
	// the floor when it doesn't (the TV will pick a fresh one on
	// next interaction).
	if activeID != "" {
		found := false
		for _, p := range out {
			if p.ID == activeID {
				found = true
				break
			}
		}
		if !found {
			activeID = ""
		}
	}
	return &ListResult{Profiles: out, ActiveProfileID: activeID}, nil
}

// Create persists a new profile. If p.ID is empty the server assigns
// a UUID; otherwise the client-provided ID is honored (the TV
// pre-generates UUIDs so it can do optimistic local insertion before
// the backend round-trip completes — no "renamed by server" sync
// hell). Sets CreatedUtcMs. Enforces MaxProfiles via a transaction
// so two concurrent creates can't push a user past the cap. Uses
// Firestore Create (not Set) so a duplicate ID fails with
// AlreadyExists rather than silently overwriting.
func (s *Service) Create(ctx context.Context, userID string, p Profile) (*Profile, error) {
	if userID == "" {
		return nil, errors.New("profiles: userID is required")
	}
	if p.Name == "" {
		return nil, ErrInvalidPayload
	}
	id := p.ID
	if id == "" {
		id = uuid.NewString()
	}
	now := time.Now().UTC().UnixMilli()
	p.ID = ""
	p.CreatedUtcMs = now

	err := s.client.RunTransaction(ctx, func(ctx context.Context, tx *firestore.Transaction) error {
		// Cheap count: query Documents and tally. Profiles are
		// capped at 5 so the read is bounded.
		iter := tx.Documents(s.profilesRef(userID))
		defer iter.Stop()
		count := 0
		for {
			_, err := iter.Next()
			if errors.Is(err, iterator.Done) {
				break
			}
			if err != nil {
				return err
			}
			count++
		}
		if count >= MaxProfiles {
			return ErrLimitReached
		}
		return tx.Create(s.profilesRef(userID).Doc(id), p)
	})
	if status.Code(err) == codes.AlreadyExists {
		return nil, fmt.Errorf("profile id %s already exists: %w", id, ErrInvalidPayload)
	}
	if err != nil {
		return nil, err
	}
	p.ID = id
	return &p, nil
}

// Update applies a partial patch to a profile. Only fields present
// in the patch map are touched; pass nil/empty to leave a field
// unchanged. Returns the post-update profile so the TV can refresh
// its in-memory copy without a follow-up read.
func (s *Service) Update(ctx context.Context, userID, profileID string, patch map[string]interface{}) (*Profile, error) {
	if userID == "" || profileID == "" {
		return nil, errors.New("profiles: userID + profileID required")
	}
	if len(patch) == 0 {
		// No-op: just fetch + return current.
		return s.Get(ctx, userID, profileID)
	}
	ref := s.profilesRef(userID).Doc(profileID)

	updates := make([]firestore.Update, 0, len(patch))
	for k, v := range patch {
		updates = append(updates, firestore.Update{Path: k, Value: v})
	}

	_, err := ref.Update(ctx, updates)
	if status.Code(err) == codes.NotFound {
		return nil, ErrNotFound
	}
	if err != nil {
		return nil, fmt.Errorf("update profile %s: %w", profileID, err)
	}
	return s.Get(ctx, userID, profileID)
}

// Get fetches one profile by id.
func (s *Service) Get(ctx context.Context, userID, profileID string) (*Profile, error) {
	snap, err := s.profilesRef(userID).Doc(profileID).Get(ctx)
	if status.Code(err) == codes.NotFound {
		return nil, ErrNotFound
	}
	if err != nil {
		return nil, fmt.Errorf("get profile %s: %w", profileID, err)
	}
	var p Profile
	if err := snap.DataTo(&p); err != nil {
		return nil, fmt.Errorf("decode profile %s: %w", profileID, err)
	}
	p.ID = snap.Ref.ID
	return &p, nil
}

// Delete removes a profile. If the deleted profile was the active
// one, also clears activeProfileId so the next list returns "" and
// the TV picks a fresh one.
func (s *Service) Delete(ctx context.Context, userID, profileID string) error {
	ref := s.profilesRef(userID).Doc(profileID)
	_, err := ref.Delete(ctx)
	if status.Code(err) == codes.NotFound {
		return ErrNotFound
	}
	if err != nil {
		return fmt.Errorf("delete profile %s: %w", profileID, err)
	}
	// Clear active if it pointed at the deleted profile.
	currentActive, _ := s.GetActiveProfileID(ctx, userID)
	if currentActive == profileID {
		_ = s.SetActiveProfileID(ctx, userID, "")
	}
	return nil
}

// SetActiveProfileID stamps the user doc with which profile is
// currently active. Empty string clears the field. The doc must
// already exist (created via auth.upsertUser at first login); we
// use Update so a stale write can't accidentally overwrite the
// other user fields.
func (s *Service) SetActiveProfileID(ctx context.Context, userID, profileID string) error {
	if userID == "" {
		return errors.New("profiles: userID is required")
	}
	_, err := s.userRef(userID).Update(ctx, []firestore.Update{
		{Path: "activeProfileId", Value: profileID},
	})
	if err != nil {
		return fmt.Errorf("set active profile: %w", err)
	}
	return nil
}

// GetActiveProfileID returns the user's persisted active profile id.
// Empty string when unset OR when the user doc doesn't exist (which
// shouldn't happen post-login but we don't want to 500 on it).
func (s *Service) GetActiveProfileID(ctx context.Context, userID string) (string, error) {
	snap, err := s.userRef(userID).Get(ctx)
	if status.Code(err) == codes.NotFound {
		return "", nil
	}
	if err != nil {
		return "", fmt.Errorf("get user %s: %w", userID, err)
	}
	val, err := snap.DataAt("activeProfileId")
	if err != nil {
		// Field absent — treat as empty.
		return "", nil
	}
	if s, ok := val.(string); ok {
		return s, nil
	}
	return "", nil
}
