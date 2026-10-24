package auth

import (
	"errors"
	"fmt"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

// Claims is what we encode into the session JWT. UserID is the Google
// "sub" — stable across email changes. We don't put email in the JWT
// (it can change, and we'd cache stale data). Lookup the user record
// in Firestore for current email/name when needed.
type Claims struct {
	UserID string `json:"uid"`
	jwt.RegisteredClaims
}

// SignToken produces a session JWT for the given user. Lifetime is
// jwtTTL (1 year) — long enough that the TV practically never has to
// re-pair, but bounded so a forgotten device can't impersonate
// indefinitely.
func SignToken(signingKey []byte, userID string) (string, error) {
	if len(signingKey) == 0 {
		return "", errors.New("auth: signing key is empty")
	}
	now := time.Now().UTC()
	claims := Claims{
		UserID: userID,
		RegisteredClaims: jwt.RegisteredClaims{
			Issuer:    "streamed-backend",
			Subject:   userID,
			IssuedAt:  jwt.NewNumericDate(now),
			NotBefore: jwt.NewNumericDate(now),
			ExpiresAt: jwt.NewNumericDate(now.Add(jwtTTL)),
		},
	}
	t := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signed, err := t.SignedString(signingKey)
	if err != nil {
		return "", fmt.Errorf("sign jwt: %w", err)
	}
	return signed, nil
}

// ParseToken validates the signature + expiry and returns the
// embedded claims. Returns ErrInvalidToken for any failure (signature
// mismatch, malformed, expired, wrong algorithm). Callers don't need
// to distinguish — every failure means "force re-pair".
func ParseToken(signingKey []byte, raw string) (*Claims, error) {
	if len(signingKey) == 0 {
		return nil, errors.New("auth: signing key is empty")
	}
	parsed, err := jwt.ParseWithClaims(raw, &Claims{}, func(t *jwt.Token) (interface{}, error) {
		if _, ok := t.Method.(*jwt.SigningMethodHMAC); !ok {
			return nil, fmt.Errorf("unexpected signing method: %v", t.Header["alg"])
		}
		return signingKey, nil
	})
	if err != nil {
		return nil, ErrInvalidToken
	}
	claims, ok := parsed.Claims.(*Claims)
	if !ok || !parsed.Valid || claims.UserID == "" {
		return nil, ErrInvalidToken
	}
	return claims, nil
}
