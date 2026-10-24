package server

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// crashLogMaxBytes caps a single upload body. The TV side caps its
// local file at 64KB and truncates from the front; this is a server-
// side defence against a misbehaving client (or a fuzz test) sending
// MB of data and filling our bucket. 128KB is 2× the TV cap with
// headroom for trailing newlines / multiple stack traces.
const crashLogMaxBytes = 128 * 1024

// crashLogStore is the storage interface the crash-log handler uses.
// Implemented by gcsCrashStore in crashstore.go — kept as an
// interface here so tests can inject a fake.
type crashLogStore = CrashLogStore

// WithCrashLogStore wires the storage backend for crash logs. When
// unset the /v1/crash-logs route returns 503 (logged, not fatal —
// crash reporting is opt-in). Returns the receiver for chainable
// configuration in main.go.
func (s *Server) WithCrashLogStore(store CrashLogStore) *Server {
	s.crashLogStore = store
	return s
}

// handleCrashLogPost serves POST /v1/crash-logs.
//
// Auth-required. Body is plain text (UTF-8) — we don't parse it,
// we just blob-store it under {userId}/{timestamp}-{nanos}.txt
// inside the configured store (typically a GCS prefix like
// `crash-logs/`). Headers: X-App-Version is mirrored into a header
// banner on the stored body.
//
// Why text not JSON: we want to be able to `gsutil cat` a crash log
// and immediately read the stack trace. JSON would force us through
// jq for every inspection.
func (s *Server) handleCrashLogPost(w http.ResponseWriter, r *http.Request) {
	if s.crashLogStore == nil {
		writeError(w, http.StatusServiceUnavailable, "crash logs not configured")
		return
	}
	if s.auth == nil {
		writeError(w, http.StatusServiceUnavailable, "auth not configured")
		return
	}
	authHeader := r.Header.Get("Authorization")
	user, err := s.auth.AuthenticateBearer(r.Context(), authHeader)
	if err != nil {
		// Log a fingerprint of the header so we can tell empty-header
		// from wrong-prefix from token-rejected. Never log the full
		// token. We log the LENGTH and the first 8 chars of the
		// scheme — enough to confirm "Bearer " arrived without
		// leaking secrets.
		hdrLen := len(authHeader)
		hdrPrefix := authHeader
		if len(hdrPrefix) > 16 {
			hdrPrefix = hdrPrefix[:16]
		}
		s.logger.Warn("crash-log auth failed",
			"err", err.Error(),
			"hdr_len", hdrLen,
			"hdr_prefix", hdrPrefix,
			"content_type", r.Header.Get("Content-Type"),
			"content_length", r.ContentLength,
			"user_agent", r.Header.Get("User-Agent"),
		)
		writeError(w, http.StatusUnauthorized, "invalid or missing token")
		return
	}

	// Cap the read so a misbehaving client can't blow our memory.
	limited := http.MaxBytesReader(w, r.Body, crashLogMaxBytes)
	body, err := io.ReadAll(limited)
	if err != nil {
		// MaxBytesReader returns a 413-shaped error; pass through.
		writeError(w, http.StatusRequestEntityTooLarge, "crash log too large")
		return
	}
	if len(body) == 0 {
		writeError(w, http.StatusBadRequest, "empty body")
		return
	}

	// Prefix the stored blob with a small banner of server-observed
	// metadata so a `gsutil cat` reader doesn't need to cross-reference
	// anywhere else to know who/when/what-version.
	appVersion := strings.TrimSpace(r.Header.Get("X-App-Version"))
	if appVersion == "" {
		appVersion = "unknown"
	}
	now := time.Now().UTC()
	header := fmt.Sprintf(
		"# server-received: %s\n# user: %s (%s)\n# app-version: %s\n# remote: %s\n\n",
		now.Format(time.RFC3339),
		user.UserID,
		user.Email,
		appVersion,
		clientAddr(r),
	)
	out := append([]byte(header), body...)

	// Key: per-user folder for easy `gsutil ls gs://bucket/{prefix}/{userId}/`.
	// Timestamp prefix sorts naturally in `ls` output. UnixNano suffix
	// disambiguates same-second uploads from a crash loop.
	key := fmt.Sprintf("%s/%s-%d.txt",
		user.UserID,
		now.Format("20060102T150405Z"),
		now.UnixNano()%1000000000,
	)

	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()
	if err := s.crashLogStore.Write(ctx, key, out); err != nil {
		s.logger.Error("crash-log write failed",
			"user", user.UserID,
			"key", key,
			"size", len(out),
			"err", err,
		)
		writeError(w, http.StatusInternalServerError, "store failed")
		return
	}
	s.logger.Info("crash-log stored",
		"user", user.UserID,
		"key", key,
		"size", len(out),
		"app", appVersion,
	)
	writeJSON(w, http.StatusAccepted, map[string]string{"key": key})
}

// clientAddr returns a best-effort client identifier for log entries.
// X-Forwarded-For wins on Cloud Run (load balancer adds it).
func clientAddr(r *http.Request) string {
	if v := r.Header.Get("X-Forwarded-For"); v != "" {
		// First entry is the original client.
		if i := strings.Index(v, ","); i >= 0 {
			return strings.TrimSpace(v[:i])
		}
		return strings.TrimSpace(v)
	}
	return r.RemoteAddr
}
