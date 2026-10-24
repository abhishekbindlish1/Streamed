// uTLS-based HTTP client that mimics Chrome's TLS ClientHello.
//
// Why this exists: the streamed.top CDN class fingerprints incoming
// TLS handshakes (JA3 hash). Go's net/http uses crypto/tls which
// has its own distinctive ClientHello signature; the CDN sees
// "obviously not chromium" and rejects (returns 500 in our case
// even though our IP would otherwise be allowed).
//
// utls is a fork of crypto/tls that lets us pick a ClientHello
// preset matching what Chrome actually sends. The CDN's JA3-based
// bot detection then sees us as "looks like Chrome" and lets us
// through.
//
// Caveats:
//   - This is fragile. If the CDN updates its anti-bot, this
//     stops working until we update the utls preset.
//   - Speed: utls handshake is comparable to crypto/tls.
//   - HTTP/2: we don't negotiate it. Most CDNs serve HLS just fine
//     over HTTP/1.1, and HTTP/2 + custom dialer needs more
//     wiring (separate http2.Transport). Skip for now.
package main

import (
	"context"
	"fmt"
	"net"
	"net/http"
	"time"

	utls "github.com/refraction-networking/utls"
)

// newUTLSHTTPClient returns an http.Client whose every TLS
// handshake uses utls with a Chrome-current ClientHello.
//
// HelloChrome_Auto picks the latest stable Chrome fingerprint the
// utls library was shipped with — track upstream releases when
// CDNs roll their detection.
func newUTLSHTTPClient(timeout time.Duration) *http.Client {
	transport := &http.Transport{
		// Custom TLS dialer. Cleared net/http's default and let
		// our handshake handle SNI + ALPN.
		DialTLSContext: func(ctx context.Context, network, addr string) (net.Conn, error) {
			host, _, err := net.SplitHostPort(addr)
			if err != nil {
				return nil, fmt.Errorf("split host:port: %w", err)
			}
			// Plain TCP first. Default Dialer respects the
			// caller's context for cancellation.
			d := &net.Dialer{Timeout: 10 * time.Second}
			plain, err := d.DialContext(ctx, network, addr)
			if err != nil {
				return nil, err
			}
			cfg := &utls.Config{
				ServerName: host,
				// Only advertise HTTP/1.1 — keeps Go's stock
				// http.Transport happy without the http2
				// upgrade dance. The streamed CDN serves HLS
				// over HTTP/1.1 just fine.
				NextProtos: []string{"http/1.1"},
				// Validate the certificate chain and hostname using system roots.
				InsecureSkipVerify: false,
			}
			uconn := utls.UClient(plain, cfg, utls.HelloChrome_Auto)
			if err := uconn.HandshakeContext(ctx); err != nil {
				_ = plain.Close()
				return nil, fmt.Errorf("utls handshake: %w", err)
			}
			return uconn, nil
		},
		// Disable HTTP/2 attempt — DialTLSContext bypasses
		// http.Transport's internal h2 negotiation; setting
		// ForceAttemptHTTP2=false avoids the transport trying
		// to be clever with our connection.
		ForceAttemptHTTP2: false,
		// Standard tunings — the proxy is a request-per-segment
		// pump, so we want connection reuse to amortize the
		// utls handshake cost.
		MaxIdleConns:          16,
		MaxIdleConnsPerHost:   8,
		IdleConnTimeout:       90 * time.Second,
		TLSHandshakeTimeout:   10 * time.Second,
		ExpectContinueTimeout: 1 * time.Second,
	}
	return &http.Client{
		Transport: transport,
		Timeout:   timeout,
	}
}
