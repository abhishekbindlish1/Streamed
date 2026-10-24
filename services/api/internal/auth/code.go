// Package auth implements TV-app login via a pairing-code handshake:
// the TV asks the backend for a short code, the user opens a web page
// on their phone, signs in with Google, and enters the code. The
// backend verifies Google's ID token, marks the code as claimed
// against the user's account, and the TV (which has been polling)
// receives a long-lived JWT it stores locally.
//
// Storage is Firestore. We sign JWTs ourselves (HS256) keyed off a
// per-deployment secret in JWT_SIGNING_KEY. The user identity is the
// Google "sub" claim — stable across email changes, never reused
// across accounts.
package auth

import (
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"strings"
	"time"
)

// ---- Pairing code generation -------------------------------------------

// codeAlphabet excludes 0/O, 1/I/L (visually ambiguous on TV from a
// distance). 31 chars × 6 positions = ~887M codes — collision risk
// is negligible at <1000 active codes.
const codeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"

// CodeLength is exposed so the TV view can size its display.
const CodeLength = 6

// PollTokenLength is opaque (not user-typed) so we use a longer
// random base64 string. 24 bytes = ~32 chars URL-safe base64.
const pollTokenBytes = 24

func generateCode() (string, error) {
	buf := make([]byte, CodeLength)
	if _, err := rand.Read(buf); err != nil {
		return "", fmt.Errorf("rand: %w", err)
	}
	out := make([]byte, CodeLength)
	for i, b := range buf {
		out[i] = codeAlphabet[int(b)%len(codeAlphabet)]
	}
	return string(out), nil
}

func generatePollToken() (string, error) {
	buf := make([]byte, pollTokenBytes)
	if _, err := rand.Read(buf); err != nil {
		return "", fmt.Errorf("rand: %w", err)
	}
	return base64.RawURLEncoding.EncodeToString(buf), nil
}

// NormalizeCode upper-cases + trims a code submitted by the user (the
// web page might let them type lowercase). Returns the canonical form
// stored in Firestore. Empty if the input doesn't look like a code.
func NormalizeCode(s string) string {
	s = strings.ToUpper(strings.TrimSpace(s))
	if len(s) != CodeLength {
		return ""
	}
	for _, r := range s {
		if !strings.ContainsRune(codeAlphabet, r) {
			return ""
		}
	}
	return s
}

// ---- Time helpers ------------------------------------------------------

// pairCodeTTL is how long a freshly-issued code is valid before the
// claim endpoint refuses it. Long enough that the user can fish out
// their phone, short enough that abandoned codes don't pollute the
// collection.
const pairCodeTTL = 5 * time.Minute

// jwtTTL is the lifetime of a session JWT. The user explicitly asked
// for "long live with explicit logout" — 1 year balances "rarely
// expires" with "still bounded for a forgotten device". Logout deletes
// the JWT on the TV; we don't track sessions server-side.
const jwtTTL = 365 * 24 * time.Hour

// errCodeExpired and friends are the user-facing errors returned by
// the pairing endpoints. Wrapping them keeps the HTTP layer simple
// (one switch in the handler).
var (
	ErrCodeNotFound = errors.New("pairing code not found")
	ErrCodeExpired  = errors.New("pairing code expired")
	ErrCodeClaimed  = errors.New("pairing code already claimed")
	ErrInvalidToken = errors.New("invalid token")
)
