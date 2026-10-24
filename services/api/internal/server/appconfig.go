package server

import (
	"net/http"

	"github.com/abindlish/streamed-backend/internal/appconfig"
)

// WithAppConfig wires the remote-config service. When unset
// /v1/config returns the package default — never 503, since the
// TV unconditionally calls this on launch and a 503 would force
// every TV onto the compile-time fallback.
func (s *Server) WithAppConfig(svc *appconfig.Service) *Server {
	s.appConfig = svc
	return s
}

// handleAppConfig serves GET /v1/config. Public (no auth) — the
// TV needs this BEFORE the user signs in so the catalog calls go
// to the right origin. Since the response only contains an origin
// URL and feature flags, there's nothing user-scoped to leak.
func (s *Server) handleAppConfig(w http.ResponseWriter, r *http.Request) {
	if s.appConfig == nil {
		// Hand back a synthesized default rather than 503. The TV
		// always wants SOME answer here — a 503 would make every
		// TV permanently sit on its compile-time default until the
		// next launch.
		writeJSON(w, http.StatusOK, appconfig.Config{
			StreamedBaseURL: appconfig.DefaultStreamedBaseURL,
			FeatureFlags:    map[string]any{},
			Source:          "server-default",
			FetchedAt:       "",
		})
		return
	}
	writeJSON(w, http.StatusOK, s.appConfig.Get(r.Context()))
}
