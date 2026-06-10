package installer

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// composeRepoRaw is the raw.githubusercontent.com base for the SriYa repo.
// The compose file lives at <base>/<ref>/docker-compose.prod.yml. Matches
// install.sh's REPO_RAW.
const composeRepoRaw = "https://raw.githubusercontent.com/JJQuispillo/SriYa"

// composeFileName is the local filename we save the compose under. The
// stack (and the install-dir detector in the compose package) expects
// `docker-compose.yml`, NOT the upstream `docker-compose.prod.yml`.
const composeFileName = "docker-compose.yml"

// Fetcher abstracts the HTTP GET so DownloadCompose can be unit-tested with
// a fake instead of hitting the network. The production impl is HTTPFetcher.
type Fetcher interface {
	// Fetch performs a GET and returns the body reader on a 2xx response.
	// On a non-2xx status it returns a non-nil error and a nil reader; the
	// caller must NOT write any file in that case.
	Fetch(ctx context.Context, url string) (io.ReadCloser, error)
}

// HTTPFetcher is the production Fetcher backed by net/http.
type HTTPFetcher struct {
	Client *http.Client
}

// NewHTTPFetcher returns a Fetcher with a sane timeout.
func NewHTTPFetcher() *HTTPFetcher {
	return &HTTPFetcher{Client: &http.Client{Timeout: 30 * time.Second}}
}

// Fetch implements Fetcher.
func (h *HTTPFetcher) Fetch(ctx context.Context, url string) (io.ReadCloser, error) {
	client := h.Client
	if client == nil {
		client = &http.Client{Timeout: 30 * time.Second}
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		_ = resp.Body.Close()
		return nil, fmt.Errorf("GET %s: unexpected status %s", url, resp.Status)
	}
	return resp.Body, nil
}

// composeRef maps a version to the git ref the compose is pinned to.
//
//   - "latest" (or empty) → "main" (track the bleeding edge), mirroring
//     install.sh's `VERSION=latest + REF=main` escape hatch.
//   - any other version → "v<version>" so a pinned image always gets the
//     matching compose layout (install.sh's `REF=v${VERSION}` default).
func composeRef(version string) string {
	v := strings.TrimSpace(version)
	if v == "" || v == "latest" {
		return "main"
	}
	if strings.HasPrefix(v, "v") {
		return v
	}
	return "v" + v
}

// DownloadCompose fetches docker-compose.prod.yml from JJQuispillo/SriYa,
// pinned to the git tag matching version, and writes it as
// dir/docker-compose.yml.
//
// Behavior contract:
//   - PIN-A-TAG: the URL targets `v<version>` (never a moving branch),
//     unless version is "latest"/empty which resolves to main.
//   - NO-CLOBBER: if dir/docker-compose.yml already exists, returns
//     (false, nil) and downloads nothing — idempotent re-runs keep the
//     operator's edited compose.
//   - NO PARTIAL ON FAILURE: the body is buffered fully and only then
//     written. If the fetch fails (e.g. the tag has no compose) the
//     function returns CodeGeneric and NO file is created. If the write
//     itself fails mid-way, the partial file is removed.
func DownloadCompose(dir, version string, f Fetcher) (created bool, err error) {
	path := filepath.Join(dir, composeFileName)
	if _, statErr := os.Stat(path); statErr == nil {
		return false, nil
	}

	ref := composeRef(version)
	url := fmt.Sprintf("%s/%s/docker-compose.prod.yml", composeRepoRaw, ref)

	ctx := context.Background()
	return downloadComposeCtx(ctx, dir, path, url, f)
}

// downloadComposeCtx is the context-aware core, split out so a future
// handler can pass its own cancellable context.
func downloadComposeCtx(ctx context.Context, dir, path, url string, f Fetcher) (bool, error) {
	body, err := f.Fetch(ctx, url)
	if err != nil {
		return false, errs.Wrap(
			errs.CodeGeneric, err,
			fmt.Sprintf("download compose from %s", url),
			"verify the version tag is published and the SriYa repo is reachable",
		)
	}
	defer body.Close()

	// Buffer fully before touching the filesystem: a streamed write that
	// fails mid-flight would leave a truncated, valid-looking compose. The
	// compose file is small (KBs), so buffering is cheap and the
	// no-partial guarantee is worth it.
	data, err := io.ReadAll(body)
	if err != nil {
		return false, errs.Wrap(errs.CodeGeneric, err, "read compose response body", "the download was interrupted; retry")
	}

	if mkErr := os.MkdirAll(dir, 0o755); mkErr != nil {
		return false, errs.Wrap(errs.CodeGeneric, mkErr, "create install dir", "check permissions on the parent directory")
	}

	wf, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o644)
	if err != nil {
		if os.IsExist(err) {
			return false, nil
		}
		return false, errs.Wrap(errs.CodeGeneric, err, "create compose file", "check permissions on the install dir")
	}
	if _, werr := wf.Write(data); werr != nil {
		_ = wf.Close()
		_ = os.Remove(path) // no partial compose on disk
		return false, errs.Wrap(errs.CodeGeneric, werr, "write compose file", "check disk space and permissions")
	}
	if cerr := wf.Close(); cerr != nil {
		_ = os.Remove(path)
		return false, errs.Wrap(errs.CodeGeneric, cerr, "close compose file", "check disk space")
	}
	return true, nil
}
