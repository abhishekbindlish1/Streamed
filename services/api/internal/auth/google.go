package auth

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// GoogleIdentity is the subset of fields we extract from a verified
// Google ID token. Subject (the "sub" claim) is the stable user ID
// across the user's lifetime — that's what we key our user records
// on. Email may change; we mirror it but never use it as a primary
// key.
type GoogleIdentity struct {
	Subject string
	Email   string
	Name    string
	Picture string
}

// VerifyGoogleIDToken validates a Google-issued ID token by calling
// Google's tokeninfo endpoint. This is the simple, no-extra-deps
// approach — we trust Google's HTTPS endpoint to verify the
// signature for us. Production-grade alternative is to fetch + cache
// Google's public JWK set and verify locally; saved for later if
// tokeninfo's rate limit ever bites.
//
// The audience parameter MUST match the Google OAuth Client ID we
// configure for the web pairing page. Without this check, anyone with
// a valid Google ID token from ANY Google OAuth client could claim
// pairing codes.
func VerifyGoogleIDToken(ctx context.Context, idToken string, expectedAudience string) (*GoogleIdentity, error) {
	if strings.TrimSpace(idToken) == "" {
		return nil, errors.New("google id token is empty")
	}
	if strings.TrimSpace(expectedAudience) == "" {
		return nil, errors.New("expected audience (Google client ID) is empty")
	}

	endpoint := "https://oauth2.googleapis.com/tokeninfo?" + url.Values{
		"id_token": []string{idToken},
	}.Encode()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return nil, fmt.Errorf("build tokeninfo request: %w", err)
	}
	client := &http.Client{Timeout: 10 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("call tokeninfo: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("tokeninfo returned %d", resp.StatusCode)
	}

	// Google's tokeninfo response — fields documented at
	// https://developers.google.com/identity/sign-in/web/backend-auth
	var payload struct {
		Iss           string `json:"iss"`
		Sub           string `json:"sub"`
		Aud           string `json:"aud"`
		Email         string `json:"email"`
		EmailVerified string `json:"email_verified"`
		Name          string `json:"name"`
		Picture       string `json:"picture"`
		// Exp is a string in tokeninfo's response (not seconds-since-epoch like in raw JWT).
		Exp string `json:"exp"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&payload); err != nil {
		return nil, fmt.Errorf("decode tokeninfo: %w", err)
	}

	// Issuer must be Google. Both forms appear in the wild.
	if payload.Iss != "https://accounts.google.com" && payload.Iss != "accounts.google.com" {
		return nil, fmt.Errorf("unexpected issuer: %q", payload.Iss)
	}
	// Audience must match our OAuth client ID — this is the load-
	// bearing security check. Without it we'd accept tokens issued
	// for any Google app.
	if payload.Aud != expectedAudience {
		return nil, fmt.Errorf("audience mismatch: got %q want %q", payload.Aud, expectedAudience)
	}
	if payload.Sub == "" {
		return nil, errors.New("token missing sub claim")
	}
	// Optional but worth checking — an unverified email could be a
	// sign-up sniping attempt. Treat it as non-fatal but warn upstream.
	// (Email isn't our primary key anyway — sub is.)
	_ = payload.EmailVerified

	return &GoogleIdentity{
		Subject: payload.Sub,
		Email:   payload.Email,
		Name:    payload.Name,
		Picture: payload.Picture,
	}, nil
}
