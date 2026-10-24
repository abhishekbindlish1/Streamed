package auth

import (
	"context"
	"errors"
	"fmt"
	"time"

	"cloud.google.com/go/firestore"
	"google.golang.org/api/iterator"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// Firestore collection names. Centralized so they're easy to change
// in one place if we ever want to namespace by environment.
const (
	collectionPairingCodes = "pairing_codes"
	collectionUsers        = "users"
)

// pairingDoc is the Firestore representation of an outstanding pair
// request. Doc ID = the user-facing code (e.g. "AB3K7M"). Once
// claimed, UserID is set and the TV's poll returns success.
type pairingDoc struct {
	PollToken string    `firestore:"pollToken"`
	CreatedAt time.Time `firestore:"createdAt"`
	ExpiresAt time.Time `firestore:"expiresAt"`
	Claimed   bool      `firestore:"claimed"`
	UserID    string    `firestore:"userId,omitempty"` // Google sub of the claimant
}

// userDoc is the per-user record. Doc ID = Google sub. Profile data
// will live in a subcollection (Phase 4) — for now this is identity
// only.
type userDoc struct {
	Email      string    `firestore:"email"`
	Name       string    `firestore:"name"`
	Picture    string    `firestore:"picture"`
	GoogleSub  string    `firestore:"googleSub"`
	CreatedAt  time.Time `firestore:"createdAt"`
	LastSeenAt time.Time `firestore:"lastSeenAt"`
}

// firestoreStore wraps a *firestore.Client with the auth-specific
// CRUD ops the service needs. Kept small + boring so swapping in an
// in-memory test store later is trivial.
type firestoreStore struct {
	client *firestore.Client
}

func newFirestoreStore(client *firestore.Client) *firestoreStore {
	return &firestoreStore{client: client}
}

// createPairing writes a fresh, unclaimed pairing record. Caller
// must retry with a new code on collision (we use the doc ID as the
// code so collisions surface as AlreadyExists).
func (s *firestoreStore) createPairing(ctx context.Context, code string, doc pairingDoc) error {
	_, err := s.client.Collection(collectionPairingCodes).Doc(code).Create(ctx, doc)
	return err
}

// getPairing fetches a pairing record by user-facing code. Returns
// ErrCodeNotFound when the doc doesn't exist (vs. transport errors).
func (s *firestoreStore) getPairing(ctx context.Context, code string) (*pairingDoc, error) {
	snap, err := s.client.Collection(collectionPairingCodes).Doc(code).Get(ctx)
	if status.Code(err) == codes.NotFound {
		return nil, ErrCodeNotFound
	}
	if err != nil {
		return nil, fmt.Errorf("get pairing %s: %w", code, err)
	}
	var d pairingDoc
	if err := snap.DataTo(&d); err != nil {
		return nil, fmt.Errorf("decode pairing %s: %w", code, err)
	}
	return &d, nil
}

// findPairingByPollToken locates a pairing record by its opaque
// poll token (the TV polls with this; never with the user-facing
// code). Returns ErrCodeNotFound if there's no match.
//
// We could maintain a secondary index but a query on a small,
// short-lived collection is cheap. ~1 doc per outstanding pair.
func (s *firestoreStore) findPairingByPollToken(ctx context.Context, pollToken string) (string, *pairingDoc, error) {
	iter := s.client.Collection(collectionPairingCodes).
		Where("pollToken", "==", pollToken).
		Limit(1).
		Documents(ctx)
	defer iter.Stop()

	snap, err := iter.Next()
	if errors.Is(err, iterator.Done) {
		return "", nil, ErrCodeNotFound
	}
	if err != nil {
		return "", nil, fmt.Errorf("query pairing by poll token: %w", err)
	}
	var d pairingDoc
	if err := snap.DataTo(&d); err != nil {
		return "", nil, fmt.Errorf("decode pairing: %w", err)
	}
	return snap.Ref.ID, &d, nil
}

// claimPairing flips a pairing record to claimed and stores the
// userId. Atomic via Firestore transaction so two concurrent claims
// can't both succeed.
func (s *firestoreStore) claimPairing(ctx context.Context, code, userID string) error {
	ref := s.client.Collection(collectionPairingCodes).Doc(code)
	return s.client.RunTransaction(ctx, func(ctx context.Context, tx *firestore.Transaction) error {
		snap, err := tx.Get(ref)
		if status.Code(err) == codes.NotFound {
			return ErrCodeNotFound
		}
		if err != nil {
			return err
		}
		var d pairingDoc
		if err := snap.DataTo(&d); err != nil {
			return err
		}
		if time.Now().UTC().After(d.ExpiresAt) {
			return ErrCodeExpired
		}
		if d.Claimed {
			return ErrCodeClaimed
		}
		return tx.Update(ref, []firestore.Update{
			{Path: "claimed", Value: true},
			{Path: "userId", Value: userID},
		})
	})
}

// upsertUser creates or updates a user doc keyed on Google sub.
// CreatedAt is preserved on update; LastSeenAt is bumped each time.
func (s *firestoreStore) upsertUser(ctx context.Context, googleSub, email, name, picture string) error {
	ref := s.client.Collection(collectionUsers).Doc(googleSub)
	return s.client.RunTransaction(ctx, func(ctx context.Context, tx *firestore.Transaction) error {
		now := time.Now().UTC()
		snap, err := tx.Get(ref)
		if status.Code(err) == codes.NotFound {
			return tx.Create(ref, userDoc{
				Email:      email,
				Name:       name,
				Picture:    picture,
				GoogleSub:  googleSub,
				CreatedAt:  now,
				LastSeenAt: now,
			})
		}
		if err != nil {
			return err
		}
		_ = snap // exists; we just need to update mutable fields
		return tx.Update(ref, []firestore.Update{
			{Path: "email", Value: email},
			{Path: "name", Value: name},
			{Path: "picture", Value: picture},
			{Path: "lastSeenAt", Value: now},
		})
	})
}

// getUser fetches a user record by Google sub. Returns nil, nil when
// the user doesn't exist (the auth-required handlers use this to
// detect a stale JWT signed for a deleted account).
func (s *firestoreStore) getUser(ctx context.Context, userID string) (*userDoc, error) {
	snap, err := s.client.Collection(collectionUsers).Doc(userID).Get(ctx)
	if status.Code(err) == codes.NotFound {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("get user %s: %w", userID, err)
	}
	var u userDoc
	if err := snap.DataTo(&u); err != nil {
		return nil, fmt.Errorf("decode user %s: %w", userID, err)
	}
	return &u, nil
}
