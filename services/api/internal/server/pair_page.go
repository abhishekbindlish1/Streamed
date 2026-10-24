package server

import (
	"embed"
	"io/fs"
	"net/http"
	"strings"
)

// pairWebFS bundles the web pairing page into the binary so we don't
// need a separate static-hosting service. Single page; we serve it
// from /pair via handlePairPage so we can substitute the Google Client
// ID at request time.
//
//go:embed assets
var pairWebFS embed.FS

// handlePairPage serves the pairing page with the configured Google
// OAuth Client ID interpolated. Doing the substitution server-side
// keeps the client ID in one place (deploy.sh env var) and avoids
// hardcoding it into the bundled HTML.
//
// Returns 404 when auth isn't configured — there's nothing to pair
// against, so the page would just confuse users.
func (s *Server) handlePairPage(w http.ResponseWriter, r *http.Request) {
	if s.auth == nil {
		http.NotFound(w, r)
		return
	}
	raw, err := fs.ReadFile(pairWebFS, "assets/pair.html")
	if err != nil {
		http.Error(w, "pair page not bundled", http.StatusInternalServerError)
		return
	}
	out := strings.ReplaceAll(string(raw), "{{GOOGLE_CLIENT_ID}}", s.auth.GoogleClientID())
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	// No caching: we want clients to always pick up the latest copy
	// after a deploy (the file is small).
	w.Header().Set("Cache-Control", "no-cache, no-store, must-revalidate")
	_, _ = w.Write([]byte(out))
}
