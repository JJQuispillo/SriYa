package core

import (
	"context"
	"errors"
	"path/filepath"
	"testing"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/config"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// fakeCertAPI is the in-process api.Client stub used by the cert
// handler tests. It records nothing beyond what the handler needs;
// test code drives behavior by populating `respond` and `err`.
type fakeCertAPI struct {
	respond []api.Certificate
	err     error
}

func (f *fakeCertAPI) Health(context.Context) (api.Health, error) {
	return api.Health{Status: "Healthy"}, nil
}
func (f *fakeCertAPI) Ready(context.Context) (api.Health, error) {
	return api.Health{Status: "Ready"}, nil
}
func (f *fakeCertAPI) BootstrapTenant(context.Context, api.BootstrapRequest) (api.BootstrapResponse, error) {
	return api.BootstrapResponse{}, nil
}
func (f *fakeCertAPI) CertStatus(_ context.Context, gotTenantID string) ([]api.Certificate, error) {
	if gotTenantID != "tid-acme" {
		// t is a *testing.T scoped to the enclosing test; we use the
		// outer testingT pointer passed via certDepsWithTenant.
		if certTestT != nil {
			certTestT.Errorf("expected tenantID=tid-acme, got %q", gotTenantID)
		}
	}
	return f.respond, f.err
}

// certTestT is a package-level handle to the most recently created test.
// Avoids passing *testing.T into the fake impl. Tests that don't use
// fakeCertAPI can ignore this.
var certTestT *testing.T

func certDepsWithTenant(t *testing.T, a api.Client) CertDeps {
	certTestT = t
	t.Helper()
	dir := t.TempDir()
	c, _ := config.LoadFrom(filepath.Join(dir, "config.toml"))
	c.UpsertContext("prod", config.Context{URL: "https://example.com", ServiceTokenRef: "keychain"})
	if err := c.SaveAs(filepath.Join(dir, "config.toml")); err != nil {
		t.Fatal(err)
	}
	store := config.NewTenantsStore(c)
	if err := store.Upsert("prod", config.TenantRef{Alias: "acme", ID: "tid-acme", RUC: "1", Env: "prod"}); err != nil {
		t.Fatal(err)
	}
	return CertDeps{
		API:         a,
		Store:       store,
		Config:      c,
		ContextName: "prod",
	}
}

// TestCertStatus_Valid pins the real backend contract (CertificateDtos.cs:3-8):
// the cert DTO carries {id, nombrePropietario, fechaExpiracion, activo,
// fechaCreacion}. Status is DERIVED in the handler from
// fechaExpiracion+activo+warn-days; a future expiry far past --warn-days
// MUST report valid (the previous v1 bug had every cert showing expired
// because the wrong DTO produced a zero-time ExpiresAt).
func TestCertStatus_Valid(t *testing.T) {
	now := time.Now().UTC()
	a := &fakeCertAPI{respond: []api.Certificate{
		{
			ID:                "c1",
			NombrePropietario: "ACME S.A.",
			FechaExpiracion:   now.Add(90 * 24 * time.Hour),
			Activo:            true,
			FechaCreacion:     now.Add(-30 * 24 * time.Hour),
		},
	}}
	d := certDepsWithTenant(t, a)
	out, err := CertStatusHandler(d)(context.Background(), CertStatusRequest{TenantAlias: "acme", WarnDays: 30})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if len(out.Data.Certs) != 1 || out.Data.Certs[0].Status != "valid" {
		t.Errorf("unexpected: %+v", out.Data.Certs)
	}
	// Subject must be populated from NombrePropietario (the only
	// descriptive field the backend exposes).
	if out.Data.Certs[0].Subject != "ACME S.A." {
		t.Errorf("Subject: got %q want ACME S.A.", out.Data.Certs[0].Subject)
	}
	// ExpiresAt must NOT be zero (the original bug).
	if out.Data.Certs[0].ExpiresAt.IsZero() {
		t.Error("ExpiresAt must be populated from fechaExpiracion")
	}
}

// TestCertStatus_ExpiringIsCIError asserts that a cert within --warn-days
// returns a renderable sentinel with code=cert_expiring. The middleware
// uses MarkRenderable to preserve the payload alongside the signal.
func TestCertStatus_ExpiringIsCIError(t *testing.T) {
	now := time.Now().UTC()
	a := &fakeCertAPI{respond: []api.Certificate{
		{
			ID:                "c1",
			NombrePropietario: "ACME S.A.",
			FechaExpiracion:   now.Add(10 * 24 * time.Hour),
			Activo:            true,
		},
	}}
	d := certDepsWithTenant(t, a)
	_, err := CertStatusHandler(d)(context.Background(), CertStatusRequest{TenantAlias: "acme", WarnDays: 30})
	if err == nil {
		t.Fatal("expected error for expiring cert")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeCertExpiring {
		t.Errorf("expected cert_expiring, got %s", ce.Code)
	}
	// Renderable: the cli middleware must render the payload (table
	// or JSON) to stdout even though err != nil. This is the bug fix
	// for RunHandler's previous "discard out when err != nil" path
	// (finding #4).
	if !ce.Renderable() {
		t.Error("expected the cert_expiring sentinel to be Renderable")
	}
	// Exit code is now 8 (distinct from the network/retryable 6 per
	// design §#9 / ai-contract REQ-ERR-002).
	if got := errs.ExitCode(err); got != 8 {
		t.Errorf("expected exit 8 for cert_expiring, got %d", got)
	}
}

// TestCertStatus_ExpiredIsCIError asserts the same for an already-expired
// cert. Exit code is now 9 (distinct from cert_expiring=8).
func TestCertStatus_ExpiredIsCIError(t *testing.T) {
	now := time.Now().UTC()
	a := &fakeCertAPI{respond: []api.Certificate{
		{
			ID:                "c1",
			NombrePropietario: "ACME S.A.",
			FechaExpiracion:   now.Add(-1 * time.Hour),
			Activo:            true,
		},
	}}
	d := certDepsWithTenant(t, a)
	_, err := CertStatusHandler(d)(context.Background(), CertStatusRequest{TenantAlias: "acme", WarnDays: 30})
	if err == nil {
		t.Fatal("expected error for expired cert")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeCertExpired {
		t.Errorf("expected cert_expired, got %v", err)
	}
	if !ce.Renderable() {
		t.Error("expected the cert_expired sentinel to be Renderable")
	}
	if got := errs.ExitCode(err); got != 9 {
		t.Errorf("expected exit 9 for cert_expired, got %d", got)
	}
}

// TestCertStatus_RevokedIsExpired asserts that a cert with Activo=false
// is treated as expired regardless of the expiry date. The backend
// doesn't expose a "revoked" state explicitly; Activo is the closest
// signal and the handler maps it to expired (the user-facing "this
// cert is not usable" sentinel).
func TestCertStatus_RevokedIsExpired(t *testing.T) {
	now := time.Now().UTC()
	a := &fakeCertAPI{respond: []api.Certificate{
		{
			ID:                "c1",
			NombrePropietario: "ACME S.A.",
			FechaExpiracion:   now.Add(365 * 24 * time.Hour),
			Activo:            false, // revoked
		},
	}}
	d := certDepsWithTenant(t, a)
	_, err := CertStatusHandler(d)(context.Background(), CertStatusRequest{TenantAlias: "acme", WarnDays: 30})
	if err == nil {
		t.Fatal("expected error for revoked cert")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeCertExpired {
		t.Errorf("expected cert_expired for revoked, got %v", err)
	}
}

// TestCertStatus_EmptyListIsCertNotFound asserts the REAL contract: the
// backend returns 200 [] for a tenant without a cert, and the handler
// MUST surface that as CodeCertNotFound (exit 4), NOT as exit 0 with an
// empty list. The previous v1 implementation was wrong on both counts
// (special-cased a fabricated 404 and returned exit 0 on 200 []).
func TestCertStatus_EmptyListIsCertNotFound(t *testing.T) {
	a := &fakeCertAPI{respond: []api.Certificate{}}
	d := certDepsWithTenant(t, a)
	_, err := CertStatusHandler(d)(context.Background(), CertStatusRequest{TenantAlias: "acme", WarnDays: 30})
	if err == nil {
		t.Fatal("expected error for empty cert list")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeCertNotFound {
		t.Errorf("expected cert_not_found, got %v", err)
	}
	if ce.Hint == "" {
		t.Error("expected a hint telling the operator how to upload a cert")
	}
	if got := errs.ExitCode(err); got != 4 {
		t.Errorf("expected exit 4 for cert_not_found, got %d", got)
	}
}

// (The RunHandler renderable test lives in internal/cli/runhandler_test.go
// because RunHandler is in the cli package and the core package cannot
// import cli without creating an import cycle.)
