package main

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestUTLSClientRejectsUntrustedCertificates(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusOK) }))
	defer server.Close()
	if response, err := newUTLSHTTPClient(3 * time.Second).Get(server.URL); err == nil {
		response.Body.Close()
		t.Fatal("untrusted certificate unexpectedly accepted")
	}
}

func TestUTLSClientCanReadHTTPFixture(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusOK) }))
	defer server.Close()
	response, err := newUTLSHTTPClient(3 * time.Second).Get(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("status: %d", response.StatusCode)
	}
}
