package installer

import (
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// fakeFetcher records the requested URL and returns canned data or an error.
type fakeFetcher struct {
	body      string
	err       error
	gotURL    string
	callCount int
}

func (f *fakeFetcher) Fetch(_ context.Context, url string) (io.ReadCloser, error) {
	f.callCount++
	f.gotURL = url
	if f.err != nil {
		return nil, f.err
	}
	return io.NopCloser(strings.NewReader(f.body)), nil
}

func TestComposeRef(t *testing.T) {
	cases := map[string]string{
		"1.0.0":  "v1.0.0",
		"1.4.0":  "v1.4.0",
		"v2.0.0": "v2.0.0", // already prefixed → unchanged
		"latest": "main",
		"":       "main",
	}
	for in, want := range cases {
		if got := composeRef(in); got != want {
			t.Errorf("composeRef(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestDownloadCompose_PinsToTagAndWrites(t *testing.T) {
	dir := t.TempDir()
	f := &fakeFetcher{body: "services:\n  billing-api: {}\n"}

	created, err := DownloadCompose(dir, "1.4.0", f)
	if err != nil {
		t.Fatalf("DownloadCompose: %v", err)
	}
	if !created {
		t.Error("expected created=true")
	}
	// Must be pinned to the v-tag, not main/latest.
	if !strings.Contains(f.gotURL, "/v1.4.0/docker-compose.prod.yml") {
		t.Errorf("URL %q is not pinned to v1.4.0", f.gotURL)
	}
	// Saved as docker-compose.yml (not .prod.yml).
	got, err := os.ReadFile(filepath.Join(dir, "docker-compose.yml"))
	if err != nil {
		t.Fatalf("read compose: %v", err)
	}
	if string(got) != f.body {
		t.Errorf("compose body = %q, want %q", got, f.body)
	}
}

func TestDownloadCompose_NoClobber(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "docker-compose.yml")
	const sentinel = "edited: by-operator\n"
	if err := os.WriteFile(path, []byte(sentinel), 0o644); err != nil {
		t.Fatal(err)
	}
	f := &fakeFetcher{body: "should-not-be-written"}

	created, err := DownloadCompose(dir, "1.0.0", f)
	if err != nil {
		t.Fatalf("DownloadCompose: %v", err)
	}
	if created {
		t.Error("expected created=false when compose already exists")
	}
	if f.callCount != 0 {
		t.Errorf("fetcher was called %d times; expected 0 (no-clobber should short-circuit)", f.callCount)
	}
	got, _ := os.ReadFile(path)
	if string(got) != sentinel {
		t.Errorf("existing compose was clobbered: %q", got)
	}
}

func TestDownloadCompose_FetchFailLeavesNoPartial(t *testing.T) {
	dir := t.TempDir()
	f := &fakeFetcher{err: errors.New("404 Not Found")}

	created, err := DownloadCompose(dir, "9.9.9", f)
	if err == nil {
		t.Fatal("expected error on fetch failure")
	}
	if created {
		t.Error("created should be false on failure")
	}
	if _, statErr := os.Stat(filepath.Join(dir, "docker-compose.yml")); statErr == nil {
		t.Error("a partial compose file was left on disk after a failed download")
	}
}

func TestDownloadCompose_LatestResolvesToMain(t *testing.T) {
	dir := t.TempDir()
	f := &fakeFetcher{body: "x: 1"}
	if _, err := DownloadCompose(dir, "latest", f); err != nil {
		t.Fatalf("DownloadCompose: %v", err)
	}
	if !strings.Contains(f.gotURL, "/main/docker-compose.prod.yml") {
		t.Errorf("latest should resolve to main; got URL %q", f.gotURL)
	}
}
