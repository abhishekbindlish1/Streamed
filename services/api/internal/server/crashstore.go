package server

import (
	"bytes"
	"context"
	"errors"
	"fmt"

	"cloud.google.com/go/storage"
)

// CrashLogStore is a focused, slash-tolerant blob writer for the
// crash-log endpoint. We could have reused posters.Store but it was
// designed for image storage — it sanitizes keys to disallow '/' and
// hardcodes a '.png' suffix on every object. Crash logs need
// per-user folders ({userId}/{timestamp}.txt) and a '.txt' suffix
// for human-readable `gsutil cat` output, so a tiny dedicated impl
// keeps both abstractions clean.
type CrashLogStore interface {
	Write(ctx context.Context, key string, data []byte) error
	Describe() string
}

// ---- GCS implementation ----------------------------------------------------

type gcsCrashStore struct {
	client *storage.Client
	bucket string
	prefix string
}

// NewGCSCrashStore returns a CrashLogStore that writes objects to
// gs://{bucket}/{prefix}/{key}. Caller passes the per-blob key (which
// may include slashes for organisational hierarchy).
func NewGCSCrashStore(ctx context.Context, bucket, prefix string) (CrashLogStore, error) {
	if bucket == "" {
		return nil, errors.New("crash store: bucket required")
	}
	c, err := storage.NewClient(ctx)
	if err != nil {
		return nil, fmt.Errorf("crash store: gcs client: %w", err)
	}
	return &gcsCrashStore{client: c, bucket: bucket, prefix: prefix}, nil
}

func (s *gcsCrashStore) Write(ctx context.Context, key string, data []byte) error {
	if key == "" {
		return errors.New("crash store: empty key")
	}
	// Defence-in-depth: GCS object names are bytes, but `..` traversal
	// and leading slashes can confuse `gsutil ls` and bucket policy
	// inspection. Reject anything obviously malformed; the handler
	// already constructs keys from controlled values (UserID + UTC
	// timestamp + nanos), so this is belt-and-suspenders.
	if hasParentEscape(key) {
		return fmt.Errorf("crash store: invalid key %q", key)
	}
	objName := key
	if s.prefix != "" {
		objName = s.prefix + "/" + key
	}
	w := s.client.Bucket(s.bucket).Object(objName).NewWriter(ctx)
	w.ContentType = "text/plain; charset=utf-8"
	if _, err := w.Write(data); err != nil {
		_ = w.Close()
		return fmt.Errorf("crash store write: %w", err)
	}
	if err := w.Close(); err != nil {
		return fmt.Errorf("crash store close: %w", err)
	}
	return nil
}

func (s *gcsCrashStore) Describe() string {
	if s.prefix == "" {
		return "gcs://" + s.bucket
	}
	return "gcs://" + s.bucket + "/" + s.prefix
}

func hasParentEscape(s string) bool {
	if len(s) > 0 && (s[0] == '/' || s[0] == '\\') {
		return true
	}
	return bytes.Contains([]byte(s), []byte(".."))
}
