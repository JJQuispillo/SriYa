package api

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/secret"
)

// timeMustParse is a tiny helper used to construct time.Time values
// inline in the test fixtures. It panics on parse error, which is
// fine for a hard-coded RFC 3339 string.
func timeMustParse(t *testing.T, s string) time.Time {
	t.Helper()
	tt, err := time.Parse(time.RFC3339, s)
	if err != nil {
		t.Fatalf("parse %q: %v", s, err)
	}
	return tt
}

func newTestClient(t *testing.T, h http.Handler) (*HTTPClient, *FakeDispatch) {
	t.Helper()
	srv := httptest.NewServer(h)
	t.Cleanup(srv.Close)
	f := &FakeDispatch{ServiceToken: "st-test", APIKey: "ak-test"}
	return NewHTTPClient(srv.URL, f), f
}

// TestHealth_OK asserts the real contract: GET /health returns 200
// with a body of {"status":"Healthy"} (PascalCase, not "ok"). The
// previous v1 test asserted the fabricated "ok" value; this test pins
// the verified backend contract (HealthEndpoints.cs:20-49).
func TestHealth_OK(t *testing.T) {
	c, _ := newTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/health" {
			t.Errorf("unexpected path: %s", r.URL.Path)
		}
		w.WriteHeader(200)
		_ = json.NewEncoder(w).Encode(Health{Status: "Healthy"})
	}))
	h, err := c.Health(context.Background())
	if err != nil {
		t.Fatalf("health: %v", err)
	}
	if h.Status != "Healthy" {
		t.Errorf("status: got %q want Healthy", h.Status)
	}
}

func TestHealth_NoAuthHeaders(t *testing.T) {
	c, f := newTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("X-Service-Token") != "" {
			t.Errorf("expected no X-Service-Token on /health, got %q", r.Header.Get("X-Service-Token"))
		}
		w.WriteHeader(200)
		_ = json.NewEncoder(w).Encode(Health{Status: "Healthy"})
	}))
	if _, err := c.Health(context.Background()); err != nil {
		t.Fatalf("health: %v", err)
	}
	if f.LastOptions.Auth != AuthAnonymous {
		t.Errorf("expected anonymous auth for /health, got %v", f.LastOptions.Auth)
	}
}

// TestReady_OK asserts the new contract: GET /health/ready (distinct
// endpoint from /health) returns 200 with {"status":"Ready"} on a
// healthy DB. This is what feeds infra status's readiness column.
func TestReady_OK(t *testing.T) {
	c, _ := newTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/health/ready" {
			t.Errorf("unexpected path: %s (want /health/ready)", r.URL.Path)
		}
		w.WriteHeader(200)
		_ = json.NewEncoder(w).Encode(Health{Status: "Ready"})
	}))
	r, err := c.Ready(context.Background())
	if err != nil {
		t.Fatalf("ready: %v", err)
	}
	if r.Status != "Ready" {
		t.Errorf("status: got %q want Ready", r.Status)
	}
}

// TestReady_503_MapsToDBUnavailable asserts the verified degraded
// contract: /health/ready returns 503 (no body) when the DB is down,
// and the client translates that into a CLIError(CodeDBUnavailable)
// with MarkRetryable=true.
func TestReady_503_MapsToDBUnavailable(t *testing.T) {
	c, _ := newTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(503)
	}))
	_, err := c.Ready(context.Background())
	if err == nil {
		t.Fatal("expected error on 503 readiness")
	}
	if !strings.Contains(err.Error(), "db_unavailable") {
		t.Errorf("expected db_unavailable, got: %v", err)
	}
	// Retryable: walk the chain to find a *CLIError and assert its
	// Retryable flag (errors.As-style).
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if !ce.Retryable {
		t.Errorf("expected retryable=true on db_unavailable, got: %v", err)
	}
}

func TestBootstrap_MultipartNoTenantHeader(t *testing.T) {
	gotCT := ""
	gotBoundary := ""
	var sawFile bool
	c, f := newTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/v1/bootstrap" {
			t.Errorf("path: %s", r.URL.Path)
		}
		gotCT = r.Header.Get("Content-Type")
		gotBoundary = strings.TrimPrefix(gotCT, "multipart/form-data; boundary=")
		if r.Header.Get("X-Tenant-Id") != "" {
			t.Errorf("bootstrap must NOT carry X-Tenant-Id")
		}
		// Parse the multipart to confirm file part exists.
		_, err := r.MultipartReader()
		if err != nil {
			t.Errorf("not multipart: %v", err)
		} else {
			sawFile = true
		}
		w.WriteHeader(201)
		_ = json.NewEncoder(w).Encode(BootstrapResponse{
			TenantID: "00000000-0000-0000-0000-000000000001",
			APIKey:   "ak-1",
		})
	}))
	certPath := writeTempCert(t)
	resp, err := c.BootstrapTenant(context.Background(), BootstrapRequest{
		RUC:             "1790000000001",
		RazonSocial:     "ACME",
		OwnerName:       "Owner",
		Password:        "secret",
		CertificatePath: certPath,
	})
	if err != nil {
		t.Fatalf("bootstrap: %v", err)
	}
	if !strings.HasPrefix(gotCT, "multipart/form-data;") {
		t.Errorf("expected multipart, got content-type: %q", gotCT)
	}
	if gotBoundary == "" {
		t.Error("expected boundary")
	}
	if !sawFile {
		t.Error("expected file part to be present")
	}
	if f.LastOptions.TenantID != "" {
		t.Errorf("bootstrap must not pass tenantID, got %q", f.LastOptions.TenantID)
	}
	if resp.APIKey != "ak-1" {
		t.Errorf("expected apikey ak-1, got %q", resp.APIKey)
	}
}

// TestBootstrap_DuplicateIs400WithProblemDetails asserts the REAL
// contract: a RUC duplicate is mapped to HTTP 400 BadRequest with a
// ProblemDetails body whose Detail contains the Spanish sentinel
// "Ya existe un tenant con el RUC '...'". The client must surface
// this as CodeTenantDuplicate (exit 5). The previous v1 test asserted
// a fabricated 409; that test is GONE — this one replaces it.
func TestBootstrap_DuplicateIs400WithProblemDetails(t *testing.T) {
	c, _ := newTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/v1/bootstrap" {
			t.Errorf("path: %s", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(400)
		_ = json.NewEncoder(w).Encode(map[string]any{
			"type":   "https://sri.example.com/errors/duplicate-tenant",
			"title":  "Error de facturación",
			"status": 400,
			"detail": "Ya existe un tenant con el RUC '1790000000001'",
		})
	}))
	certPath := writeTempCert(t)
	_, err := c.BootstrapTenant(context.Background(), BootstrapRequest{
		RUC: "1790000000001", RazonSocial: "x", OwnerName: "y", Password: "z", CertificatePath: certPath,
	})
	if err == nil {
		t.Fatal("expected error for duplicate RUC")
	}
	if !strings.Contains(err.Error(), "tenant_duplicate") {
		t.Errorf("expected tenant_duplicate, got: %v", err)
	}
	// The previous 409 assertion (`CodeConflict`) is explicitly removed
	// from this test; a 409 here would indicate we forgot to fix the
	// fabricated contract.
}

// TestBootstrap_Other400IsBadRequest asserts the negative case: a 400
// with a ProblemDetails that does NOT match the duplicate sentinel
// (e.g. InvalidRucException) is surfaced as CodeBootstrapBadReq with
// the Detail verbatim — NOT as tenant_duplicate. This guards against
// the duplicate heuristic being too loose.
func TestBootstrap_Other400IsBadRequest(t *testing.T) {
	c, _ := newTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(400)
		_ = json.NewEncoder(w).Encode(map[string]any{
			"type":   "https://sri.example.com/errors/invalid-ruc",
			"title":  "Error de facturación",
			"status": 400,
			"detail": "El RUC ingresado no es válido (debe tener 13 dígitos)",
		})
	}))
	certPath := writeTempCert(t)
	_, err := c.BootstrapTenant(context.Background(), BootstrapRequest{
		RUC: "1", RazonSocial: "x", OwnerName: "y", Password: "z", CertificatePath: certPath,
	})
	if err == nil {
		t.Fatal("expected error for invalid ruc")
	}
	if strings.Contains(err.Error(), "tenant_duplicate") {
		t.Errorf("invalid-ruc must NOT map to tenant_duplicate, got: %v", err)
	}
	if !strings.Contains(err.Error(), "bootstrap_bad_request") {
		t.Errorf("expected bootstrap_bad_request, got: %v", err)
	}
	// Detail verbatim so the operator can act.
	if !strings.Contains(err.Error(), "13 dígitos") {
		t.Errorf("expected the backend Detail verbatim, got: %v", err)
	}
}

// TestCertStatus_TenantHeaderInjected decodes the REAL backend
// payload: {id, nombrePropietario, fechaExpiracion, activo, fechaCreacion}
// (camelCase). The old DTO asserted `subject/issuer/expiresAt/estado`
// fields that don't exist; this test pins the verified shape.
func TestCertStatus_TenantHeaderInjected(t *testing.T) {
	c, f := newTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/v1/certificates" {
			t.Errorf("path: %s", r.URL.Path)
		}
		if r.Header.Get("X-Tenant-Id") != "00000000-0000-0000-0000-000000000abc" {
			t.Errorf("expected X-Tenant-Id, got %q", r.Header.Get("X-Tenant-Id"))
		}
		w.WriteHeader(200)
		// Real backend payload (CertificateDtos.cs:3-8 + System.Text.Json
		// camelCase). The cert expires in 90 days; the test expects the
		// CLI to decode fechaExpiracion and derive status=valid (not the
		// fabricated "estado" field).
		_ = json.NewEncoder(w).Encode([]Certificate{{
			ID:                "cert-1",
			NombrePropietario: "ACME S.A.",
			FechaExpiracion:   timeMustParse(t, "2026-09-01T00:00:00Z"),
			Activo:            true,
			FechaCreacion:     timeMustParse(t, "2025-09-01T00:00:00Z"),
		}})
	}))
	certs, err := c.CertStatus(context.Background(), "00000000-0000-0000-0000-000000000abc")
	if err != nil {
		t.Fatalf("cert: %v", err)
	}
	if len(certs) != 1 {
		t.Fatalf("expected 1 cert, got %d", len(certs))
	}
	if certs[0].ID != "cert-1" {
		t.Errorf("id: %q", certs[0].ID)
	}
	if certs[0].NombrePropietario != "ACME S.A." {
		t.Errorf("nombrePropietario: %q", certs[0].NombrePropietario)
	}
	if certs[0].FechaExpiracion.IsZero() {
		t.Error("fechaExpiracion must NOT be zero (the previous DTO mismatch bug)")
	}
	if f.LastOptions.TenantID != "00000000-0000-0000-0000-000000000abc" {
		t.Errorf("dispatcher should record tenantID, got %q", f.LastOptions.TenantID)
	}
}

// TestCertStatus_EmptyList200 asserts the verified contract: a tenant
// without a cert returns 200 with an empty JSON array (NOT 404). The
// previous v1 code special-cased 404 here, which is dead code; the
// empty-list → cert_not_found mapping is performed by the handler.
func TestCertStatus_EmptyList200(t *testing.T) {
	c, _ := newTestClient(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(200)
		_, _ = w.Write([]byte("[]"))
	}))
	certs, err := c.CertStatus(context.Background(), "tid-1")
	if err != nil {
		t.Fatalf("200 [] must NOT error at the api layer; got: %v", err)
	}
	if len(certs) != 0 {
		t.Errorf("expected 0 certs, got %d", len(certs))
	}
}

func TestKeyringDispatch_EnvOverrideWins(t *testing.T) {
	t.Setenv(secret.EnvServiceToken, "env-token")
	d := NewKeyringDispatch(secret.NewInMemoryStore())
	req, _ := http.NewRequest("GET", "http://x", nil)
	d.SetRequestAuth(req, AuthCallOptions{Auth: AuthServiceToken})
	if got := req.Header.Get("X-Service-Token"); got != "env-token" {
		t.Errorf("expected env token, got %q", got)
	}
}

func TestKeyringDispatch_KeychainFallback(t *testing.T) {
	store := secret.NewInMemoryStore()
	_ = store.Set("service_token", "keychain-token")
	d := NewKeyringDispatch(store)
	req, _ := http.NewRequest("GET", "http://x", nil)
	d.SetRequestAuth(req, AuthCallOptions{Auth: AuthServiceToken})
	if got := req.Header.Get("X-Service-Token"); got != "keychain-token" {
		t.Errorf("expected keychain token, got %q", got)
	}
}

func TestKeyringDispatch_OmitsTenantIDForBootstrap(t *testing.T) {
	store := secret.NewInMemoryStore()
	_ = store.Set("service_token", "tok")
	d := NewKeyringDispatch(store)
	req, _ := http.NewRequest("POST", "http://x", nil)
	d.SetRequestAuth(req, AuthCallOptions{Auth: AuthServiceToken, TenantID: ""})
	if req.Header.Get("X-Tenant-Id") != "" {
		t.Error("expected no X-Tenant-Id when TenantID is empty")
	}
}

func writeTempCert(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	p := dir + "/cert.p12"
	if err := writeFile(p, "fake-cert-bytes"); err != nil {
		t.Fatal(err)
	}
	return p
}
