// Signed-URL tokens for the streaming proxy.
//
// Threat model: a token is the only thing that lets the proxy fetch
// upstream + serve bytes. Anyone with a valid token (within its TTL)
// can pull bytes through us. Tokens are minted ONLY by /v1/extract
// (which itself requires the user's pairing JWT), so the only way
// to get one is to be authenticated.
//
// Format on the wire:
//   <base64url(payload_json)>.<base64url(hmac_sha256(payload_json, key))>
//
// Payload carries everything the proxy needs to recreate the
// browser's request: target URL, Referer, User-Agent, Cookie line,
// expiry. Self-contained — no server-side cache lookup, so this
// works across Cloud Run instances without a shared store.
package main

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"time"
)

// proxyToken is the payload we sign + ship to the TV. Field names
// are short to keep the URL short — these things appear in the
// Tizen player's network stack at every segment fetch.
type proxyToken struct {
	URL     string `json:"u"`           // target absolute URL
	Referer string `json:"r,omitempty"` // upstream Referer header
	UA      string `json:"a,omitempty"` // upstream User-Agent
	Cookie  string `json:"c,omitempty"` // upstream Cookie header value
	Expiry  int64  `json:"e"`           // unix seconds
}

// signProxyToken produces the wire token for a given target URL.
// expiry is the deadline AFTER which the proxy refuses to mint a
// new fetch from this token.
func signProxyToken(t proxyToken, key []byte) (string, error) {
	body, err := json.Marshal(t)
	if err != nil {
		return "", err
	}
	bodyB64 := base64.RawURLEncoding.EncodeToString(body)
	mac := hmac.New(sha256.New, key)
	mac.Write([]byte(bodyB64))
	sigB64 := base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
	return bodyB64 + "." + sigB64, nil
}

// verifyProxyToken parses + verifies the wire token. Returns the
// payload on success or an error describing which check failed
// (caller maps to 401/403/410 as appropriate).
func verifyProxyToken(token string, key []byte) (*proxyToken, error) {
	if token == "" {
		return nil, errors.New("empty token")
	}
	// Format: <body>.<sig>
	dot := -1
	for i := len(token) - 1; i >= 0; i-- {
		if token[i] == '.' {
			dot = i
			break
		}
	}
	if dot <= 0 || dot == len(token)-1 {
		return nil, errors.New("malformed token")
	}
	bodyB64, sigB64 := token[:dot], token[dot+1:]

	// Constant-time signature check.
	expectedMac := hmac.New(sha256.New, key)
	expectedMac.Write([]byte(bodyB64))
	expectedSig := base64.RawURLEncoding.EncodeToString(expectedMac.Sum(nil))
	if !hmac.Equal([]byte(sigB64), []byte(expectedSig)) {
		return nil, errors.New("bad signature")
	}

	body, err := base64.RawURLEncoding.DecodeString(bodyB64)
	if err != nil {
		return nil, fmt.Errorf("decode body: %w", err)
	}
	var t proxyToken
	if err := json.Unmarshal(body, &t); err != nil {
		return nil, fmt.Errorf("unmarshal payload: %w", err)
	}
	if t.URL == "" {
		return nil, errors.New("token missing url")
	}
	if time.Now().Unix() > t.Expiry {
		return nil, errors.New("token expired")
	}
	return &t, nil
}
