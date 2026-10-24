package auth

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"cloud.google.com/go/firestore"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// Service is the auth orchestrator: pair init, claim, poll, plus
// session-token verification. HTTP handlers in the server package
// translate JSON → method calls on this struct.
type Service struct {
	store              *firestoreStore
	signingKey         []byte
	googleClientID     string
	logger             *slog.Logger
	maxCreateRetries   int // collision retries for code generation
}

// Config bundles dependencies + tunables passed once at construction.
type Config struct {
	Firestore      *firestore.Client
	SigningKey     []byte // HS256 key (>=32 bytes recommended)
	GoogleClientID string // OAuth Client ID for the web pairing page (audience check)
	Logger         *slog.Logger
}

func New(cfg Config) (*Service, error) {
	if cfg.Firestore == nil {
		return nil, errors.New("auth: firestore client is required")
	}
	if len(cfg.SigningKey) < 32 {
		return nil, errors.New("auth: signing key must be at least 32 bytes")
	}
	if cfg.GoogleClientID == "" {
		return nil, errors.New("auth: google client id is required")
	}
	logger := cfg.Logger
	if logger == nil {
		logger = slog.Default()
	}
	return &Service{
		store:            newFirestoreStore(cfg.Firestore),
		signingKey:       cfg.SigningKey,
		googleClientID:   cfg.GoogleClientID,
		logger:           logger,
		maxCreateRetries: 5,
	}, nil
}

// GoogleClientID exposes the configured OAuth client ID. Used by the
// web pairing page handler to interpolate the value into the rendered
// HTML so the JS can call google.accounts.id.initialize with it.
func (s *Service) GoogleClientID() string {
	return s.googleClientID
}

// PairInitResult is what the TV gets back when starting a pair flow.
type PairInitResult struct {
	Code      string    `json:"code"`      // user-facing, 6 chars from the unambiguous alphabet
	PollToken string    `json:"pollToken"` // opaque, used by TV to poll
	ExpiresAt time.Time `json:"expiresAt"` // UTC; TV shows a countdown
}

// PairInit creates a fresh pair record and returns the code + poll
// token. Retries on the rare case of code collision so the caller
// doesn't have to. Code is what the user types on the web page; poll
// token is what the TV uses to check for completion (separate so the
// poll endpoint can't be brute-forced from the short code).
func (s *Service) PairInit(ctx context.Context) (*PairInitResult, error) {
	pollToken, err := generatePollToken()
	if err != nil {
		return nil, err
	}
	now := time.Now().UTC()
	doc := pairingDoc{
		PollToken: pollToken,
		CreatedAt: now,
		ExpiresAt: now.Add(pairCodeTTL),
		Claimed:   false,
	}

	for attempt := 0; attempt < s.maxCreateRetries; attempt++ {
		code, err := generateCode()
		if err != nil {
			return nil, err
		}
		err = s.store.createPairing(ctx, code, doc)
		if err == nil {
			return &PairInitResult{
				Code:      code,
				PollToken: pollToken,
				ExpiresAt: doc.ExpiresAt,
			}, nil
		}
		// Firestore returns AlreadyExists on a duplicate doc ID. Any
		// other error is genuine — surface it.
		if status.Code(err) != codes.AlreadyExists {
			return nil, fmt.Errorf("create pairing: %w", err)
		}
		s.logger.Info("pair: code collision, retrying", "attempt", attempt+1)
	}
	return nil, errors.New("auth: exhausted code-collision retries (the alphabet is exhausted, try again)")
}

// PairClaim is the web-page side: user has signed in with Google,
// then submitted the code. We verify Google's ID token, upsert the
// user, mark the code as claimed against that user. The TV's next
// poll picks up the result.
func (s *Service) PairClaim(ctx context.Context, code string, googleIDToken string) error {
	canon := NormalizeCode(code)
	if canon == "" {
		return ErrCodeNotFound
	}
	identity, err := VerifyGoogleIDToken(ctx, googleIDToken, s.googleClientID)
	if err != nil {
		return fmt.Errorf("verify google id token: %w", err)
	}
	if err := s.store.upsertUser(ctx, identity.Subject, identity.Email, identity.Name, identity.Picture); err != nil {
		return fmt.Errorf("upsert user: %w", err)
	}
	if err := s.store.claimPairing(ctx, canon, identity.Subject); err != nil {
		return err
	}
	return nil
}

// PairPollResult is what the TV gets back from a single poll.
type PairPollResult struct {
	Status string `json:"status"`        // "pending" | "complete"
	JWT    string `json:"jwt,omitempty"` // present iff status=="complete"
}

// PairPoll is the TV side: calls every few seconds with the poll
// token until status==complete + jwt is non-empty. We delete the
// pairing record on successful claim so a second poll returns
// not-found (lets the TV stop polling on its own).
func (s *Service) PairPoll(ctx context.Context, pollToken string) (*PairPollResult, error) {
	code, doc, err := s.store.findPairingByPollToken(ctx, pollToken)
	if err != nil {
		return nil, err
	}
	if !doc.Claimed {
		// Don't error on expiry here — let the TV see "pending"
		// until ExpiresAt, then it'll re-init on its own.
		return &PairPollResult{Status: "pending"}, nil
	}
	// Claimed. Sign a JWT for the user, then delete the pairing record
	// so this poll token can't be reused.
	jwt, err := SignToken(s.signingKey, doc.UserID)
	if err != nil {
		return nil, fmt.Errorf("sign token: %w", err)
	}
	// Best-effort delete; if it fails we still return the JWT (the
	// pairing record will TTL-expire from Firestore anyway).
	_, _ = s.store.client.Collection(collectionPairingCodes).Doc(code).Delete(ctx)
	return &PairPollResult{Status: "complete", JWT: jwt}, nil
}

// AuthenticatedUser is what the auth middleware injects into request
// context for protected handlers. UserID is the Google sub.
type AuthenticatedUser struct {
	UserID  string
	Email   string
	Name    string
	Picture string
}

// AuthenticateBearer validates a "Bearer <jwt>" Authorization header
// value and returns the user it represents. Bumps lastSeenAt on
// success (so we have a usable last-active signal for cleanup).
func (s *Service) AuthenticateBearer(ctx context.Context, headerValue string) (*AuthenticatedUser, error) {
	const prefix = "Bearer "
	if len(headerValue) <= len(prefix) || headerValue[:len(prefix)] != prefix {
		return nil, ErrInvalidToken
	}
	raw := headerValue[len(prefix):]
	claims, err := ParseToken(s.signingKey, raw)
	if err != nil {
		return nil, err
	}
	u, err := s.store.getUser(ctx, claims.UserID)
	if err != nil {
		return nil, err
	}
	if u == nil {
		// JWT was signed for a user that no longer exists. Treat as
		// invalid — TV must re-pair.
		return nil, ErrInvalidToken
	}
	return &AuthenticatedUser{
		UserID:  claims.UserID,
		Email:   u.Email,
		Name:    u.Name,
		Picture: u.Picture,
	}, nil
}
