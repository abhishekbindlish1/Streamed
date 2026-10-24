package server

import (
	"encoding/json"
	"errors"
	"net/http"
	"strings"

	"github.com/abindlish/streamed-backend/internal/auth"
	"github.com/abindlish/streamed-backend/internal/profiles"
)

// authRequired wraps a handler that needs an authenticated user.
// On success the user is passed as a 3rd arg, saving every handler
// from re-doing the bearer-token extraction.
func (s *Server) authRequired(handler func(w http.ResponseWriter, r *http.Request, user *auth.AuthenticatedUser)) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if s.auth == nil {
			writeError(w, http.StatusServiceUnavailable, "auth not configured")
			return
		}
		user, err := s.auth.AuthenticateBearer(r.Context(), r.Header.Get("Authorization"))
		if err != nil {
			writeError(w, http.StatusUnauthorized, "invalid or missing token")
			return
		}
		handler(w, r, user)
	}
}

// handleProfilesList — GET /v1/profiles
func (s *Server) handleProfilesList(w http.ResponseWriter, r *http.Request, user *auth.AuthenticatedUser) {
	if s.profiles == nil {
		writeError(w, http.StatusServiceUnavailable, "profiles not configured")
		return
	}
	res, err := s.profiles.List(r.Context(), user.UserID)
	if err != nil {
		s.logger.Error("profiles list", "user", user.UserID, "err", err)
		writeError(w, http.StatusInternalServerError, "list failed")
		return
	}
	writeJSON(w, http.StatusOK, res)
}

// handleProfileCreate — POST /v1/profiles
// Body: full profile object minus id + createdUtcMs (server assigns).
func (s *Server) handleProfileCreate(w http.ResponseWriter, r *http.Request, user *auth.AuthenticatedUser) {
	if s.profiles == nil {
		writeError(w, http.StatusServiceUnavailable, "profiles not configured")
		return
	}
	var p profiles.Profile
	if err := json.NewDecoder(r.Body).Decode(&p); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json body")
		return
	}
	if strings.TrimSpace(p.Name) == "" {
		writeError(w, http.StatusBadRequest, "name is required")
		return
	}
	created, err := s.profiles.Create(r.Context(), user.UserID, p)
	switch {
	case err == nil:
		writeJSON(w, http.StatusCreated, created)
	case errors.Is(err, profiles.ErrLimitReached):
		writeError(w, http.StatusConflict, err.Error())
	case errors.Is(err, profiles.ErrInvalidPayload):
		writeError(w, http.StatusBadRequest, err.Error())
	default:
		s.logger.Error("profile create", "user", user.UserID, "err", err)
		writeError(w, http.StatusInternalServerError, "create failed")
	}
}

// handleProfileUpdate — PATCH /v1/profiles/{id}
// Body: object containing only the fields to update. Recognized
// fields: name, avatarR, avatarG, avatarB, avatarImage,
// favoriteSports, favoriteTeams, lastSelectedSport.
func (s *Server) handleProfileUpdate(w http.ResponseWriter, r *http.Request, user *auth.AuthenticatedUser) {
	if s.profiles == nil {
		writeError(w, http.StatusServiceUnavailable, "profiles not configured")
		return
	}
	id := strings.TrimSpace(r.PathValue("id"))
	if id == "" {
		writeError(w, http.StatusBadRequest, "missing profile id")
		return
	}
	// Use map[string]interface{} so callers can send partial patches
	// without us auto-zeroing missing fields.
	var raw map[string]interface{}
	if err := json.NewDecoder(r.Body).Decode(&raw); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json body")
		return
	}
	patch := filterAllowedProfileFields(raw)
	updated, err := s.profiles.Update(r.Context(), user.UserID, id, patch)
	switch {
	case err == nil:
		writeJSON(w, http.StatusOK, updated)
	case errors.Is(err, profiles.ErrNotFound):
		writeError(w, http.StatusNotFound, "profile not found")
	default:
		s.logger.Error("profile update", "user", user.UserID, "id", id, "err", err)
		writeError(w, http.StatusInternalServerError, "update failed")
	}
}

// handleProfileDelete — DELETE /v1/profiles/{id}
func (s *Server) handleProfileDelete(w http.ResponseWriter, r *http.Request, user *auth.AuthenticatedUser) {
	if s.profiles == nil {
		writeError(w, http.StatusServiceUnavailable, "profiles not configured")
		return
	}
	id := strings.TrimSpace(r.PathValue("id"))
	if id == "" {
		writeError(w, http.StatusBadRequest, "missing profile id")
		return
	}
	err := s.profiles.Delete(r.Context(), user.UserID, id)
	switch {
	case err == nil:
		w.WriteHeader(http.StatusNoContent)
	case errors.Is(err, profiles.ErrNotFound):
		writeError(w, http.StatusNotFound, "profile not found")
	default:
		s.logger.Error("profile delete", "user", user.UserID, "id", id, "err", err)
		writeError(w, http.StatusInternalServerError, "delete failed")
	}
}

// handleSetActiveProfile — POST /v1/profiles/active
// Body: {"profileId": "..."}. Empty string clears the active
// selection (e.g. after delete-all).
func (s *Server) handleSetActiveProfile(w http.ResponseWriter, r *http.Request, user *auth.AuthenticatedUser) {
	if s.profiles == nil {
		writeError(w, http.StatusServiceUnavailable, "profiles not configured")
		return
	}
	var body struct {
		ProfileID string `json:"profileId"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json body")
		return
	}
	if err := s.profiles.SetActiveProfileID(r.Context(), user.UserID, body.ProfileID); err != nil {
		s.logger.Error("set active profile", "user", user.UserID, "id", body.ProfileID, "err", err)
		writeError(w, http.StatusInternalServerError, "set active failed")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"activeProfileId": body.ProfileID})
}

// filterAllowedProfileFields keeps only fields the client is
// allowed to set. Drops "id", "createdUtcMs" (server-managed) and
// any unrecognized keys (defense in depth — Firestore's strict
// types would reject most garbage anyway).
func filterAllowedProfileFields(raw map[string]interface{}) map[string]interface{} {
	allowed := map[string]bool{
		"name":              true,
		"avatarR":           true,
		"avatarG":           true,
		"avatarB":           true,
		"avatarImage":       true,
		"favoriteSports":    true,
		"favoriteTeams":     true,
		"lastSelectedSport": true,
	}
	out := make(map[string]interface{}, len(raw))
	for k, v := range raw {
		if allowed[k] {
			out[k] = v
		}
	}
	return out
}
