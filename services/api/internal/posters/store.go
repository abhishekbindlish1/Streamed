// Package posters: storage abstraction for cached PNG assets.
//
// Two implementations:
//
//   - localStore — writes / reads from a directory on the container's
//     filesystem. Fine for local dev and for Cloud Run revisions that
//     don't care about cross-instance cache (the on-disk cache lives
//     only as long as one container does).
//   - gcsStore — writes / reads from a Google Cloud Storage bucket.
//     Required on Cloud Run if you want generated posters to survive
//     scale-to-zero or to be visible across multiple concurrent
//     instances.
//
// The choice is wired in main.go based on the STORAGE_BUCKET env var:
// set it and gcsStore is used, leave it unset and localStore is used.
//
// Both implementations key objects by their bare id (e.g. "matchA",
// "northern-xi"); the .png suffix lives on the URL surface only — so
// switching from one store to the other requires no key/path rewrites.
package posters

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"cloud.google.com/go/storage"
	"google.golang.org/api/iterator"
)

// ErrNotExist is returned by Store.Open when the requested key isn't
// present. Callers should treat it as a 404, not a 5xx.
var ErrNotExist = errors.New("posters: not exist")

// StoredObject is the subset of object metadata the manifest cares
// about. ModTime is when the object was last written; Size is content
// length in bytes.
type StoredObject struct {
	Key     string    // bare id, no extension
	Size    int64
	ModTime time.Time
}

// Store is the minimal storage interface. Implemented by localStore
// (directory) and gcsStore (Google Cloud Storage bucket).
type Store interface {
	// Open returns a reader for key. The caller MUST Close it. Returns
	// ErrNotExist when the key isn't present.
	Open(ctx context.Context, key string) (io.ReadCloser, *StoredObject, error)
	// Write replaces the contents of key with data. Atomic where
	// possible (rename for local, single PUT for GCS).
	Write(ctx context.Context, key string, data []byte) error
	// Exists reports whether key is present and non-empty.
	Exists(ctx context.Context, key string) bool
	// List returns every key currently present, sorted lexicographically.
	List(ctx context.Context) ([]StoredObject, error)
	// Describe returns a short string for logs: "local:./data/posters" or "gcs://bucket/posters".
	Describe() string
}

// ----------------------------------------------------------------------
// localStore
// ----------------------------------------------------------------------

// localStore writes PNGs to a directory on the local filesystem.
type localStore struct {
	dir string
}

// NewLocalStore returns a Store rooted at dir. The directory is created
// lazily on first write.
func NewLocalStore(dir string) Store { return &localStore{dir: dir} }

func (s *localStore) Describe() string { return "local:" + s.dir }

func (s *localStore) path(key string) (string, error) {
	clean, err := SanitizeMatchID(key)
	if err != nil {
		return "", err
	}
	return filepath.Join(s.dir, clean+".png"), nil
}

func (s *localStore) Open(ctx context.Context, key string) (io.ReadCloser, *StoredObject, error) {
	p, err := s.path(key)
	if err != nil {
		return nil, nil, err
	}
	f, err := os.Open(p)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, nil, ErrNotExist
		}
		return nil, nil, err
	}
	st, err := f.Stat()
	if err != nil {
		_ = f.Close()
		return nil, nil, err
	}
	return f, &StoredObject{Key: key, Size: st.Size(), ModTime: st.ModTime()}, nil
}

func (s *localStore) Write(ctx context.Context, key string, data []byte) error {
	p, err := s.path(key)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return err
	}
	tmp := p + ".tmp"
	if err := os.WriteFile(tmp, data, 0o644); err != nil {
		return err
	}
	if err := os.Rename(tmp, p); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return nil
}

func (s *localStore) Exists(_ context.Context, key string) bool {
	p, err := s.path(key)
	if err != nil {
		return false
	}
	st, err := os.Stat(p)
	return err == nil && !st.IsDir() && st.Size() > 0
}

func (s *localStore) List(_ context.Context) ([]StoredObject, error) {
	entries, err := os.ReadDir(s.dir)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, nil
		}
		return nil, err
	}
	out := make([]StoredObject, 0, len(entries))
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		if !strings.HasSuffix(name, ".png") {
			continue
		}
		id := strings.TrimSuffix(name, ".png")
		if _, err := SanitizeMatchID(id); err != nil {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		out = append(out, StoredObject{Key: id, Size: info.Size(), ModTime: info.ModTime()})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Key < out[j].Key })
	return out, nil
}

// ----------------------------------------------------------------------
// gcsStore
// ----------------------------------------------------------------------

// gcsStore writes PNGs to a single GCS bucket under a configurable
// object-name prefix. Object names are "<prefix>/<key>.png".
//
// Auth uses the standard ADC chain (Application Default Credentials):
//   - Cloud Run: the runtime service account is used automatically
//   - Local dev: `gcloud auth application-default login` first
//
// Reads stream straight through the Cloud Run instance back to the
// client; with the bucket in the same region, latency is single-digit
// ms which is fine for our request volume.
type gcsStore struct {
	client *storage.Client
	bucket string
	prefix string // "posters" or "logos"; no leading or trailing slash
}

// NewGCSStore opens a long-lived storage.Client and returns a Store
// rooted at gs://bucket/prefix. The client is created here so callers
// don't have to thread one through.
func NewGCSStore(ctx context.Context, bucket, prefix string) (Store, error) {
	client, err := storage.NewClient(ctx)
	if err != nil {
		return nil, fmt.Errorf("gcs new client: %w", err)
	}
	return &gcsStore{client: client, bucket: bucket, prefix: strings.Trim(prefix, "/")}, nil
}

func (s *gcsStore) Describe() string {
	if s.prefix == "" {
		return "gcs://" + s.bucket
	}
	return "gcs://" + s.bucket + "/" + s.prefix
}

func (s *gcsStore) objectName(key string) (string, error) {
	clean, err := SanitizeMatchID(key)
	if err != nil {
		return "", err
	}
	if s.prefix == "" {
		return clean + ".png", nil
	}
	return s.prefix + "/" + clean + ".png", nil
}

func (s *gcsStore) Open(ctx context.Context, key string) (io.ReadCloser, *StoredObject, error) {
	name, err := s.objectName(key)
	if err != nil {
		return nil, nil, err
	}
	obj := s.client.Bucket(s.bucket).Object(name)
	attrs, err := obj.Attrs(ctx)
	if err != nil {
		if errors.Is(err, storage.ErrObjectNotExist) {
			return nil, nil, ErrNotExist
		}
		return nil, nil, fmt.Errorf("gcs attrs %s: %w", name, err)
	}
	r, err := obj.NewReader(ctx)
	if err != nil {
		if errors.Is(err, storage.ErrObjectNotExist) {
			return nil, nil, ErrNotExist
		}
		return nil, nil, fmt.Errorf("gcs reader %s: %w", name, err)
	}
	return r, &StoredObject{Key: key, Size: attrs.Size, ModTime: attrs.Updated}, nil
}

func (s *gcsStore) Write(ctx context.Context, key string, data []byte) error {
	name, err := s.objectName(key)
	if err != nil {
		return err
	}
	w := s.client.Bucket(s.bucket).Object(name).NewWriter(ctx)
	w.ContentType = "image/png"
	// Browsers + CDNs cache for a day; we never overwrite a key that
	// won't change content (poster/logo regeneration is opt-in).
	w.CacheControl = "public, max-age=86400"
	if _, err := w.Write(data); err != nil {
		_ = w.Close()
		return fmt.Errorf("gcs write %s: %w", name, err)
	}
	if err := w.Close(); err != nil {
		return fmt.Errorf("gcs close %s: %w", name, err)
	}
	return nil
}

func (s *gcsStore) Exists(ctx context.Context, key string) bool {
	name, err := s.objectName(key)
	if err != nil {
		return false
	}
	attrs, err := s.client.Bucket(s.bucket).Object(name).Attrs(ctx)
	return err == nil && attrs != nil && attrs.Size > 0
}

func (s *gcsStore) List(ctx context.Context) ([]StoredObject, error) {
	prefixForList := ""
	if s.prefix != "" {
		prefixForList = s.prefix + "/"
	}
	it := s.client.Bucket(s.bucket).Objects(ctx, &storage.Query{Prefix: prefixForList})
	var out []StoredObject
	for {
		attrs, err := it.Next()
		if err == iterator.Done {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("gcs list: %w", err)
		}
		name := attrs.Name
		// Trim the prefix so we only return the bare id portion.
		if prefixForList != "" {
			name = strings.TrimPrefix(name, prefixForList)
		}
		if !strings.HasSuffix(name, ".png") {
			continue
		}
		id := strings.TrimSuffix(name, ".png")
		if _, err := SanitizeMatchID(id); err != nil {
			continue
		}
		out = append(out, StoredObject{Key: id, Size: attrs.Size, ModTime: attrs.Updated})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Key < out[j].Key })
	return out, nil
}
