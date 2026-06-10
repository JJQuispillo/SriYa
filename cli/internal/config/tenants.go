package config

import (
	"errors"
	"fmt"
	"strings"
)

// TenantsStore is the interface used by handlers to read/write the
// tenant registry for a context. The implementation is TenantsOnConfig
// (TOML-backed). Handlers MUST NOT import this package's Config struct
// directly — they go through the interface so future stores (e.g. a
// SQLite-backed one for v3 multi-host admin) can be plugged in.
type TenantsStore interface {
	// ListKnown returns all tenants registered in the given context.
	// Returns an empty slice (not an error) when the context has none.
	ListKnown(ctxName string) ([]TenantRef, error)
	// Upsert registers or updates a tenant under the context.
	Upsert(ctxName string, t TenantRef) error
	// Get returns the tenant with the given alias. Returns
	// ErrTenantNotFound when not present.
	Get(ctxName, alias string) (TenantRef, error)
	// SetCurrent persists the active tenant alias for the context.
	// An empty alias is allowed (clears the active tenant).
	SetCurrent(ctxName, alias string) error
	// Active returns the currently-active tenant for the context, or
	// ErrNoActiveTenant if none is set.
	Active(ctxName string) (TenantRef, error)
}

// TenantRef is the wire shape handlers see. It deliberately does NOT
// expose the underlying storage; the on-disk Tenant struct is private to
// the package and is mapped at the boundary.
type TenantRef struct {
	Alias string `json:"alias" yaml:"alias"`
	ID    string `json:"id"    yaml:"id"`
	RUC   string `json:"ruc"   yaml:"ruc"`
	Env   string `json:"env"   yaml:"env"`
}

// ErrTenantNotFound is returned by TenantsStore.Get when the alias is
// not registered in the context. The CLIError translation lives in
// handler code so the store stays free of CLI concerns.
var ErrTenantNotFound = errors.New("tenant not found")

// ErrNoActiveTenant is returned by TenantsStore.Active when no
// current_tenant is set for the context.
var ErrNoActiveTenant = errors.New("no active tenant")

// TenantsOnConfig implements TenantsStore by delegating to a *Config
// instance. The instance is typically obtained from config.Load().
type TenantsOnConfig struct {
	C *Config
}

// NewTenantsStore returns a TenantsOnConfig backed by the given Config.
func NewTenantsStore(c *Config) *TenantsOnConfig { return &TenantsOnConfig{C: c} }

// ListKnown implements TenantsStore.
func (s *TenantsOnConfig) ListKnown(ctxName string) ([]TenantRef, error) {
	tenants := s.C.Tenants[ctxName]
	out := make([]TenantRef, 0, len(tenants))
	for alias, t := range tenants {
		out = append(out, TenantRef{
			Alias: alias,
			ID:    t.ID,
			RUC:   t.RUC,
			Env:   t.Env,
		})
	}
	return out, nil
}

// Upsert implements TenantsStore.
func (s *TenantsOnConfig) Upsert(ctxName string, ref TenantRef) error {
	if ref.Alias == "" {
		return errors.New("tenant alias is required")
	}
	s.C.UpsertTenant(ctxName, ref.Alias, Tenant{ID: ref.ID, RUC: ref.RUC, Env: ref.Env})
	return s.C.Save()
}

// Get implements TenantsStore.
func (s *TenantsOnConfig) Get(ctxName, alias string) (TenantRef, error) {
	t, ok := s.C.Tenants[ctxName][alias]
	if !ok {
		return TenantRef{}, fmt.Errorf("%w: %s/%s", ErrTenantNotFound, ctxName, alias)
	}
	return TenantRef{Alias: alias, ID: t.ID, RUC: t.RUC, Env: t.Env}, nil
}

// SetCurrent implements TenantsStore.
func (s *TenantsOnConfig) SetCurrent(ctxName, alias string) error {
	if ctxName == "" {
		return errors.New("ctxName is required")
	}
	// Allow clearing (empty alias) without an existence check.
	if alias != "" {
		if _, ok := s.C.Tenants[ctxName][alias]; !ok {
			return fmt.Errorf("%w: %s/%s", ErrTenantNotFound, ctxName, alias)
		}
	}
	// Only update current_tenant for THIS context; we keep current_context
	// intact. Multi-context setCurrent is a v2 concern.
	if s.C.CurrentContext == ctxName {
		s.C.CurrentTenant = alias
	}
	return s.C.Save()
}

// Active implements TenantsStore.
func (s *TenantsOnConfig) Active(ctxName string) (TenantRef, error) {
	alias := s.C.CurrentTenant
	if ctxName != s.C.CurrentContext || alias == "" {
		return TenantRef{}, ErrNoActiveTenant
	}
	return s.Get(ctxName, alias)
}

// normalize is shared with the toml file format, kept here so package
// callers can use a single alias canonicalization rule.
func normalizeAlias(s string) string {
	return strings.ToLower(strings.TrimSpace(s))
}
