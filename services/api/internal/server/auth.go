package server

import (
	"encoding/json"
	"errors"
	"net/http"

	"github.com/abindlish/streamed-backend/internal/auth"
)

// handlePairInit starts a new pairing flow. Called by the TV on
// launch when it doesn't have a stored JWT. Returns a 6-char code
// the user types on the web page + a poll token the TV uses to
// check for completion.
func (s *Server) handlePairInit(w http.ResponseWriter, r *http.Request) {
	if s.auth == nil {
		writeError(w, http.StatusServiceUnavailable, "auth not configured")
		return
	}
	res, err := s.auth.PairInit(r.Context())
	if err != nil {
		s.logger.Error("pair init", "err", err)
		writeError(w, http.StatusInternalServerError, "could not init pair")
		return
	}
	writeJSON(w, http.StatusOK, res)
}

// handlePairClaim is called by the web pairing page after the user
// signs in with Google. Body: {"code": "AB3K7M", "googleIdToken":
// "<jwt from Google Sign-In>"}. Verifies the token, upserts the
// user, marks the code claimed.
func (s *Server) handlePairClaim(w http.ResponseWriter, r *http.Request) {
	if s.auth == nil {
		writeError(w, http.StatusServiceUnavailable, "auth not configured")
		return
	}
	var body struct {
		Code           string `json:"code"`
		GoogleIDToken  string `json:"googleIdToken"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json body")
		return
	}
	err := s.auth.PairClaim(r.Context(), body.Code, body.GoogleIDToken)
	switch {
	case err == nil:
		writeJSON(w, http.StatusOK, map[string]string{"status": "claimed"})
	case errors.Is(err, auth.ErrCodeNotFound):
		writeError(w, http.StatusNotFound, "pairing code not found")
	case errors.Is(err, auth.ErrCodeExpired):
		writeError(w, http.StatusGone, "pairing code expired")
	case errors.Is(err, auth.ErrCodeClaimed):
		writeError(w, http.StatusConflict, "pairing code already claimed")
	default:
		s.logger.Error("pair claim", "err", err)
		writeError(w, http.StatusBadRequest, err.Error())
	}
}

// handlePairPoll is called by the TV every few seconds with its
// poll token. Returns {"status":"pending"} until claimed, then
// {"status":"complete","jwt":"..."} once. The pairing record is
// deleted on the first successful poll.
func (s *Server) handlePairPoll(w http.ResponseWriter, r *http.Request) {
	if s.auth == nil {
		writeError(w, http.StatusServiceUnavailable, "auth not configured")
		return
	}
	pollToken := r.URL.Query().Get("token")
	if pollToken == "" {
		writeError(w, http.StatusBadRequest, "missing token query param")
		return
	}
	res, err := s.auth.PairPoll(r.Context(), pollToken)
	switch {
	case err == nil:
		writeJSON(w, http.StatusOK, res)
	case errors.Is(err, auth.ErrCodeNotFound):
		// TV stops polling on 404 — either the code expired and was
		// cleaned up, or the user already claimed + we already
		// served the JWT (deletion happens on first claim-poll).
		writeError(w, http.StatusNotFound, "pairing not found")
	default:
		s.logger.Error("pair poll", "err", err)
		writeError(w, http.StatusInternalServerError, "poll failed")
	}
}

// handleMe is the canonical "is my JWT still valid" endpoint. The
// TV calls this on launch to decide whether to show the pairing
// screen or jump straight into the profile picker.
func (s *Server) handleMe(w http.ResponseWriter, r *http.Request) {
	if s.auth == nil {
		writeError(w, http.StatusServiceUnavailable, "auth not configured")
		return
	}
	user, err := s.auth.AuthenticateBearer(r.Context(), r.Header.Get("Authorization"))
	if err != nil {
		writeError(w, http.StatusUnauthorized, "invalid or missing token")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{
		"userId":  user.UserID,
		"email":   user.Email,
		"name":    user.Name,
		"picture": user.Picture,
	})
}
