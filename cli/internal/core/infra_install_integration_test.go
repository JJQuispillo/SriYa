package core

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/compose"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// ---------------------------------------------------------------------------
// Integration tests for InfraInstallHandler against a REAL HTTP endpoint
// (httptest), NOT fakes. These validate the handler↔api.Client contract:
// readiness polling, bootstrap POST, JSON envelope, and secret isolation.
//
// Unlike the unit tests in infra_install_test.go which use fake closures
// and in-memory seams, these tests wire the real api.NewHTTPClient and
// validate end-to-end HTTP semantics (status codes, headers, body encoding).
// ---------------------------------------------------------------------------

// readyHandler responds to GET /health/ready with configurable status.
type readyHandler struct {
	statusCode int
	body       string
}

func (h *readyHandler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	w.WriteHeader(h.statusCode)
	_, _ = w.Write([]byte(h.body))
}

// bootstrapHandler responds to POST /api/v1/bootstrap with a canned response.
type bootstrapHandler struct {
	statusCode int
	body       string
	called     bool
	lastBody   string
}

func (h *bootstrapHandler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	h.called = true
	if r.Method != http.MethodPost {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	// Read body for assertion
	buf := make([]byte, 4096)
	n, _ := r.Body.Read(buf)
	h.lastBody = string(buf[:n])
	r.Body.Close()

	w.WriteHeader(h.statusCode)
	_, _ = w.Write([]byte(h.body))
}

func TestInfraInstall_Integration_HealthReadyAndBootstrap(t *testing.T) {
	dir := t.TempDir()

	// Seed .env + compose so ValidateInstallDir passes.
	if err := os.WriteFile(filepath.Join(dir, ".env"), []byte("BILLING_IMAGE_TAG=v1.0.0\nSERVICE_AUTH_TOKEN=svc_test_token\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "docker-compose.yml"), []byte("services: {}\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	// Create a dummy cert so the real api.Client can open it for the
	// multipart upload to the test server.
	certPath := filepath.Join(t.TempDir(), "test.p12")
	if err := os.WriteFile(certPath, []byte("fake-p12-bytes"), 0o600); err != nil {
		t.Fatal(err)
	}

	// Real HTTP server: /health/ready returns Ready on first probe.
	ready := &readyHandler{statusCode: http.StatusOK, body: `{"status":"Ready"}`}
	boot := &bootstrapHandler{
		statusCode: http.StatusOK,
		body:       `{"tenantId":"tnt_integration","apiKey":"ak_integration_secret","ruc":"1790012345001","razonSocial":"INTEGRATION S.A.","certificadoId":"cert_001","apiKeyId":"ak_001","fechaCreacion":"2026-06-08T00:00:00Z"}`,
	}
	mux := http.NewServeMux()
	mux.Handle("/health/ready", ready)
	mux.Handle("/api/v1/bootstrap", boot)
	srv := httptest.NewServer(mux)
	defer srv.Close()

	// Real api.Client pointed at the test server.
	apiClient := api.NewHTTPClient(srv.URL, &api.FakeDispatch{ServiceToken: "svc_test_token"})

	// Fake compose runner — we skip pull/up by seeding the dir.
	fc := &fakeComposeRunner{installDir: dir, runFn: func(args ...string) (compose.Result, error) {
		// The doctor's post-install check runs `compose ps`.
		if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
			return compose.Result{Stdout: `{"Name":"sriya-billing-1","State":"running","Service":"billing"}` + "\n"}, nil
		}
		return compose.Result{}, nil
	}}

	deps := InfraInstallDeps{
		Fetcher:            &fakeFetcher{body: "services: {}\n"},
		Probe:              fakeDockerProbe{},
		NewCompose:         newComposeFactory(fc),
		Ready:              apiClient.Ready,
		BootstrapAPI:       apiClient,
		Seeder:             &fakeSeeder{},
		Interactive:        func() bool { return false },
		HealthTimeout:      5 * time.Second,
		HealthPollInterval: 10 * time.Millisecond,
	}

	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{
		Version:     "1.0.0",
		Dir:         dir,
		NoBootstrap: false,
		ContextName: "integration",
		LocalURL:    srv.URL,
		Boot: api.BootstrapRequest{
			RUC:             "1790012345001",
			RazonSocial:     "INTEGRATION S.A.",
			OwnerName:       "Test Owner",
			Password:        "pw",
			CertificatePath: certPath,
		},
	})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if !out.Data.Healthy {
		t.Error("expected Healthy=true")
	}
	if !boot.called {
		t.Error("expected /api/v1/bootstrap to be called")
	}
	if out.Data.TenantID != "tnt_integration" {
		t.Errorf("TenantID: got %q want tnt_integration", out.Data.TenantID)
	}
	if errs.ExitCode(err) != 0 {
		t.Errorf("expected exit 0, got %d", errs.ExitCode(err))
	}

	// Validate JSON contract: marshal and check no secret leak.
	b, _ := json.Marshal(out)
	if strings.Contains(string(b), "ak_integration_secret") {
		t.Error("APIKey leaked into JSON output")
	}
}

func TestInfraInstall_Integration_HealthTimeout(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, ".env"), []byte("BILLING_IMAGE_TAG=v1.0.0\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "docker-compose.yml"), []byte("services: {}\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	// Server returns 503 (not Ready) on every probe.
	ready := &readyHandler{statusCode: http.StatusServiceUnavailable, body: `{"status":"Unavailable"}`}
	mux := http.NewServeMux()
	mux.Handle("/health/ready", ready)
	srv := httptest.NewServer(mux)
	defer srv.Close()

	apiClient := api.NewHTTPClient(srv.URL, &api.FakeDispatch{})
	fc := &fakeComposeRunner{installDir: dir}

	deps := InfraInstallDeps{
		Fetcher:            &fakeFetcher{body: "services: {}\n"},
		Probe:              fakeDockerProbe{},
		NewCompose:         newComposeFactory(fc),
		Ready:              apiClient.Ready,
		HealthTimeout:      50 * time.Millisecond,
		HealthPollInterval: 5 * time.Millisecond,
	}

	h := InfraInstallHandler(deps)
	_, err := h(context.Background(), InfraInstallRequest{
		Version:     "1.0.0",
		Dir:         dir,
		NoBootstrap: true,
	})
	if err == nil {
		t.Fatal("expected install_health_timeout error")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeInstallHealthTimeout {
		t.Errorf("code: got %s want install_health_timeout", ce.Code)
	}
	if got := errs.ExitCode(err); got != 10 {
		t.Errorf("exit: got %d want 10", got)
	}
}

func TestInfraInstall_Integration_NoBootstrapSkips(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, ".env"), []byte("BILLING_IMAGE_TAG=v1.0.0\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "docker-compose.yml"), []byte("services: {}\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	ready := &readyHandler{statusCode: http.StatusOK, body: `{"status":"Ready"}`}
	boot := &bootstrapHandler{}
	mux := http.NewServeMux()
	mux.Handle("/health/ready", ready)
	mux.Handle("/api/v1/bootstrap", boot)
	srv := httptest.NewServer(mux)
	defer srv.Close()

	apiClient := api.NewHTTPClient(srv.URL, &api.FakeDispatch{})
	fc := &fakeComposeRunner{installDir: dir}

	deps := InfraInstallDeps{
		Fetcher:            &fakeFetcher{body: "services: {}\n"},
		Probe:              fakeDockerProbe{},
		NewCompose:         newComposeFactory(fc),
		Ready:              apiClient.Ready,
		HealthTimeout:      2 * time.Second,
		HealthPollInterval: 10 * time.Millisecond,
	}

	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{
		Version:     "1.0.0",
		Dir:         dir,
		NoBootstrap: true,
	})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if boot.called {
		t.Error("--no-bootstrap must NOT call /api/v1/bootstrap")
	}
	if !out.Data.Healthy {
		t.Error("expected Healthy=true")
	}
	if out.Data.TenantID != "" {
		t.Error("expected empty TenantID with --no-bootstrap")
	}
}
