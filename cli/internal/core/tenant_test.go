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

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/config"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/secret"
)

type fakeAPI struct {
	bootstrapFn func(context.Context, api.BootstrapRequest) (api.BootstrapResponse, error)
}

func (f *fakeAPI) Health(_ context.Context) (api.Health, error) {
	return api.Health{Status: "Healthy"}, nil
}
func (f *fakeAPI) Ready(_ context.Context) (api.Health, error) {
	return api.Health{Status: "Ready"}, nil
}
func (f *fakeAPI) BootstrapTenant(ctx context.Context, in api.BootstrapRequest) (api.BootstrapResponse, error) {
	return f.bootstrapFn(ctx, in)
}
func (f *fakeAPI) CertStatus(_ context.Context, _ string) ([]api.Certificate, error) {
	return nil, nil
}

func writeCert(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	p := filepath.Join(dir, "cert.p12")
	if err := os.WriteFile(p, []byte("fake"), 0o600); err != nil {
		t.Fatal(err)
	}
	return p
}

func newTenantDeps(t *testing.T, a api.Client) (TenantDeps, *secret.InMemoryStore, *config.Config) {
	t.Helper()
	store := secret.NewInMemoryStore()
	dir := t.TempDir()
	c, _ := config.LoadFrom(filepath.Join(dir, "config.toml"))
	c.UpsertContext("prod", config.Context{URL: "https://example.com", ServiceTokenRef: "keychain"})
	c.CurrentContext = "prod"
	if err := c.SaveAs(filepath.Join(dir, "config.toml")); err != nil {
		t.Fatal(err)
	}
	return TenantDeps{
		API:         a,
		Store:       config.NewTenantsStore(c),
		Secret:      store,
		Config:      c,
		ContextName: "prod",
	}, store, c
}

func TestTenantCreate_Success_AutoCapturesKey(t *testing.T) {
	a := &fakeAPI{bootstrapFn: func(_ context.Context, in api.BootstrapRequest) (api.BootstrapResponse, error) {
		return api.BootstrapResponse{
			TenantID: "tid-1",
			RUC:      in.RUC,
			APIKey:   "secret-key-once",
		}, nil
	}}
	deps, store, c := newTenantDeps(t, a)
	h := TenantCreateHandler(deps)
	out, err := h(MarkMutable(context.Background()), TenantCreateRequest{
		Alias: "acme", RUC: "1", RazonSocial: "X", OwnerName: "Y",
		Password: "p", CertificatePath: writeCert(t),
	})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	// APIKey NOT in result (ShowAPIKey false).
	if out.Data.APIKey != "" {
		t.Errorf("expected empty APIKey, got %q", out.Data.APIKey)
	}
	if !out.Data.APIKeyStored {
		t.Error("expected APIKeyStored=true")
	}
	// Confirm keychain captured the value.
	got, err := store.Get(secret.TenantAPIKey("prod", "acme"))
	if err != nil {
		t.Fatalf("keychain get: %v", err)
	}
	if got != "secret-key-once" {
		t.Errorf("keychain value: got %q", got)
	}
	// Confirm tenant registered.
	if _, err := config.NewTenantsStore(c).Get("prod", "acme"); err != nil {
		t.Errorf("tenant not registered: %v", err)
	}
}

func TestTenantCreate_ShowAPIKey(t *testing.T) {
	a := &fakeAPI{bootstrapFn: func(_ context.Context, in api.BootstrapRequest) (api.BootstrapResponse, error) {
		return api.BootstrapResponse{TenantID: "tid", RUC: in.RUC, APIKey: "k-once"}, nil
	}}
	deps, _, _ := newTenantDeps(t, a)
	h := TenantCreateHandler(deps)
	out, err := h(MarkMutable(context.Background()), TenantCreateRequest{
		Alias: "acme", RUC: "1", RazonSocial: "X", OwnerName: "Y",
		Password: "p", CertificatePath: writeCert(t), ShowAPIKey: true,
	})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Data.APIKey != "k-once" {
		t.Errorf("expected APIKey in result, got %q", out.Data.APIKey)
	}
}

func TestTenantCreate_AliasAlreadyExists(t *testing.T) {
	a := &fakeAPI{bootstrapFn: func(_ context.Context, _ api.BootstrapRequest) (api.BootstrapResponse, error) {
		t.Fatal("backend should not be called for a known collision")
		return api.BootstrapResponse{}, nil
	}}
	deps, _, _ := newTenantDeps(t, a)
	// Pre-register the alias.
	if err := deps.Store.Upsert("prod", config.TenantRef{Alias: "acme", ID: "x"}); err != nil {
		t.Fatal(err)
	}
	_, err := TenantCreateHandler(deps)(MarkMutable(context.Background()), TenantCreateRequest{
		Alias: "acme", RUC: "1", RazonSocial: "X", OwnerName: "Y",
		Password: "p", CertificatePath: writeCert(t),
	})
	if err == nil {
		t.Fatal("expected error")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeTenantDuplicate {
		t.Errorf("expected tenant_duplicate, got %s", ce.Code)
	}
}

func TestTenantCreate_DryRunNoSideEffects(t *testing.T) {
	a := &fakeAPI{bootstrapFn: func(_ context.Context, _ api.BootstrapRequest) (api.BootstrapResponse, error) {
		t.Fatal("dry-run must not call backend")
		return api.BootstrapResponse{}, nil
	}}
	deps, store, _ := newTenantDeps(t, a)
	h := TenantCreateHandler(deps)
	ctx := MarkMutable(WithDryRun(context.Background()))
	out, err := h(ctx, TenantCreateRequest{
		Alias: "acme", RUC: "1", RazonSocial: "X", OwnerName: "Y",
		Password: "p", CertificatePath: writeCert(t),
	})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Kind != "TenantCreatePlan" {
		t.Errorf("expected plan kind, got %s", out.Kind)
	}
	if _, err := store.Get(secret.TenantAPIKey("prod", "acme")); err == nil {
		t.Error("dry-run must not write to keychain")
	}
}

func TestTenantCreate_ReadOnlyBlocked(t *testing.T) {
	a := &fakeAPI{bootstrapFn: func(_ context.Context, _ api.BootstrapRequest) (api.BootstrapResponse, error) {
		t.Fatal("read-only must not call backend")
		return api.BootstrapResponse{}, nil
	}}
	deps, _, _ := newTenantDeps(t, a)
	ctx := MarkMutable(WithReadOnly(context.Background()))
	_, err := TenantCreateHandler(deps)(ctx, TenantCreateRequest{
		Alias: "acme", RUC: "1", RazonSocial: "X", OwnerName: "Y",
		Password: "p", CertificatePath: writeCert(t),
	})
	if err == nil {
		t.Fatal("expected readonly_blocked")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeReadOnlyBlocked {
		t.Errorf("expected readonly_blocked, got %v", err)
	}
}

func TestTenantList_RendersActive(t *testing.T) {
	a := &fakeAPI{}
	deps, _, c := newTenantDeps(t, a)
	if err := deps.Store.Upsert("prod", config.TenantRef{Alias: "acme", ID: "1"}); err != nil {
		t.Fatal(err)
	}
	if err := deps.Store.Upsert("prod", config.TenantRef{Alias: "beta", ID: "2"}); err != nil {
		t.Fatal(err)
	}
	c.CurrentTenant = "beta"
	h := TenantListHandler(deps)
	out, err := h(context.Background(), struct{}{})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Data.Active != "beta" {
		t.Errorf("active: got %q", out.Data.Active)
	}
	// Verify one tenant is marked active.
	activeCount := 0
	for _, t0 := range out.Data.Tenants {
		if t0.IsActive {
			activeCount++
		}
	}
	if activeCount != 1 {
		t.Errorf("expected 1 active, got %d", activeCount)
	}
}

func TestTenantUse_PersistsAndResolves(t *testing.T) {
	a := &fakeAPI{}
	deps, _, _ := newTenantDeps(t, a)
	if err := deps.Store.Upsert("prod", config.TenantRef{Alias: "acme", ID: "1"}); err != nil {
		t.Fatal(err)
	}
	h := TenantUseHandler(deps)
	out, err := h(context.Background(), TenantUseRequest{Alias: "acme"})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Data.Alias != "acme" {
		t.Errorf("alias: %q", out.Data.Alias)
	}
	// Confirm current_tenant was set in the underlying config.
	c2, _ := config.Load()
	if c2.CurrentTenant != "acme" {
		t.Errorf("current_tenant not persisted: %q", c2.CurrentTenant)
	}
}

func TestTenantUse_AliasNotFound(t *testing.T) {
	a := &fakeAPI{}
	deps, _, _ := newTenantDeps(t, a)
	h := TenantUseHandler(deps)
	_, err := h(context.Background(), TenantUseRequest{Alias: "ghost"})
	if err == nil {
		t.Fatal("expected error")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeTenantNotFound {
		t.Errorf("expected tenant_not_found, got %v", err)
	}
}

func TestTenantCurrent_Empty(t *testing.T) {
	a := &fakeAPI{}
	deps, _, _ := newTenantDeps(t, a)
	h := TenantCurrentHandler(deps)
	_, err := h(context.Background(), struct{}{})
	if err == nil {
		t.Fatal("expected error for no active tenant")
	}
}

// TestTenantCreate_DuplicateIs400WithProblemDetails asserts the REAL
// contract: the backend returns HTTP 400 BadRequest with a
// ProblemDetails body whose Detail contains the Spanish sentinel
// "Ya existe un tenant con el RUC '...'" for a duplicate RUC. The
// handler must surface this as CodeTenantDuplicate (exit 5) and MUST
// NOT register the alias in config or write the apiKey to the
// keychain. The previous v1 test asserted a fabricated 409; that
// test is GONE — this one replaces it.
//
// We use an httptest stub so the test exercises the real HTTP code
// path (multipart, ProblemDetails parsing) and not a hand-rolled
// fake that could drift from production.
func TestTenantCreate_DuplicateIs400WithProblemDetails(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
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
	defer srv.Close()

	// Build a real HTTPClient against the stub. The dispatcher is the
	// production keychain dispatcher seeded with a memory store.
	dispatch := api.NewKeyringDispatch(secret.NewInMemoryStore())
	hc := api.NewHTTPClient(srv.URL, dispatch)

	deps, store, c := newTenantDeps(t, hc)
	ctx := MarkMutable(context.Background())
	_, err := TenantCreateHandler(deps)(ctx, TenantCreateRequest{
		Alias: "acme", RUC: "1790000000001", RazonSocial: "X", OwnerName: "Y",
		Password: "p", CertificatePath: writeCert(t),
	})
	if err == nil {
		t.Fatal("expected tenant_duplicate error")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T: %v", err, err)
	}
	if ce.Code != errs.CodeTenantDuplicate {
		t.Errorf("expected tenant_duplicate, got %s (full: %v)", ce.Code, err)
	}
	if got := errs.ExitCode(err); got != 5 {
		t.Errorf("expected exit 5 for tenant_duplicate, got %d", got)
	}
	// CRITICAL: per spec, the handler MUST NOT register the alias or
	// write the apiKey to the keychain on a duplicate. Confirm both.
	if _, gerr := config.NewTenantsStore(c).Get("prod", "acme"); gerr == nil {
		t.Error("duplicate must not register the alias in config")
	}
	if _, serr := store.Get(secret.TenantAPIKey("prod", "acme")); serr == nil {
		t.Error("duplicate must not write the apiKey to the keychain")
	}
}

// TestTenantCreate_Other400IsBadRequest asserts the negative case: a
// 400 with a ProblemDetails that does NOT match the duplicate sentinel
// (e.g. RUC inválido) surfaces as CodeBootstrapBadReq (not
// tenant_duplicate), with the Detail verbatim. This guards against a
// too-loose duplicate heuristic.
func TestTenantCreate_Other400IsBadRequest(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(400)
		_ = json.NewEncoder(w).Encode(map[string]any{
			"type":   "https://sri.example.com/errors/invalid-ruc",
			"title":  "Error de facturación",
			"status": 400,
			"detail": "El RUC ingresado no es válido (debe tener 13 dígitos)",
		})
	}))
	defer srv.Close()

	dispatch := api.NewKeyringDispatch(secret.NewInMemoryStore())
	hc := api.NewHTTPClient(srv.URL, dispatch)

	deps, _, _ := newTenantDeps(t, hc)
	ctx := MarkMutable(context.Background())
	_, err := TenantCreateHandler(deps)(ctx, TenantCreateRequest{
		Alias: "acme", RUC: "1", RazonSocial: "X", OwnerName: "Y",
		Password: "p", CertificatePath: writeCert(t),
	})
	if err == nil {
		t.Fatal("expected error for invalid ruc")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T: %v", err, err)
	}
	if ce.Code == errs.CodeTenantDuplicate {
		t.Errorf("invalid-ruc must NOT map to tenant_duplicate, got: %v", err)
	}
	if ce.Code != errs.CodeBootstrapBadReq {
		t.Errorf("expected bootstrap_bad_request, got %s (full: %v)", ce.Code, err)
	}
	// Detail must be present verbatim so the operator can act.
	if !strings.Contains(ce.Message, "13 dígitos") {
		t.Errorf("expected the backend Detail verbatim, got: %q", ce.Message)
	}
}

// _ ensures integration with api package is not silently dropped.
var _ = json.Marshal
var _ = httptest.NewServer
var _ = http.StatusCreated
var _ = strings.TrimSpace
