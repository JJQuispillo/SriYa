package api

import (
	"net/http"
	"os"
	"strings"

	"github.com/JJQuispillo/billing/cli/internal/secret"
)

// AuthKind selects which credential the RoundTripper resolves and which
// header set it emits. Per design.md "Auth Dispatch".
type AuthKind int

const (
	// AuthAnonymous is used for health checks (no headers).
	AuthAnonymous AuthKind = iota
	// AuthServiceToken injects X-Service-Token (+ optional X-Tenant-Id).
	AuthServiceToken
	// AuthTenantAPIKey injects X-Api-Key (per-tenant; v2 for documents).
	AuthTenantAPIKey
)

// AuthCallOptions is the per-call configuration for the RoundTripper.
// `TenantID` is per-call, NOT per-context: bootstrap and health omit it
// (per correction B applied to design.md).
type AuthCallOptions struct {
	Auth     AuthKind
	TenantID string
	// ContextName selects which context's service-token to use. The
	// dispatcher resolves it from the secret.Store using ContextKey.
	ContextName string
	// TenantAlias, when set, lets the dispatcher look up the per-tenant
	// api key. Only used with AuthTenantAPIKey.
	TenantAlias string
}

// AuthDispatch is the single injection point for auth headers. The
// production implementation is KeyringDispatch; tests use FakeDispatch.
type AuthDispatch interface {
	SetRequestAuth(req *http.Request, opts AuthCallOptions)
}

// KeyringDispatch is the production AuthDispatch. It consults the
// secret.Store (keychain) for credentials and applies precedence
// flag > env > keychain > config (the env fallback lives inside
// secret.Store.Get, so the dispatcher simply asks the store).
type KeyringDispatch struct {
	Store secret.Store
}

// NewKeyringDispatch returns a KeyringDispatch backed by the given store.
func NewKeyringDispatch(s secret.Store) *KeyringDispatch {
	return &KeyringDispatch{Store: s}
}

// SetRequestAuth implements AuthDispatch. It applies the rules in
// design.md "Auth Dispatch": service-token for service/admin/bootstrap,
// api-key for per-tenant ops, and X-Tenant-Id only when explicitly
// provided in AuthCallOptions (per-call, not per-context). Precedence
// flag > env > keychain > config: env is consulted before keychain.
func (k *KeyringDispatch) SetRequestAuth(req *http.Request, opts AuthCallOptions) {
	switch opts.Auth {
	case AuthAnonymous:
		// No headers. Used by Health.
		return
	case AuthServiceToken:
		tok := resolveCredential("service_token", k.Store)
		if tok == "" {
			// We do NOT fail the request here — the request will return
			// 401 from the backend and the caller will surface
			// `auth_invalid`. Failing here would conflate transport
			// errors with auth errors.
			return
		}
		req.Header.Set("X-Service-Token", tok)
		// Per-call tenant scoping. Empty string = omit (bootstrap, health).
		if id := strings.TrimSpace(opts.TenantID); id != "" {
			req.Header.Set("X-Tenant-Id", id)
		}
	case AuthTenantAPIKey:
		// Per-tenant api key. Look up in env (SRIYACTL_API_KEY) or
		// keychain under sriyactl/<ctx>/<alias>.
		lookupKey := "api_key"
		if opts.ContextName != "" && opts.TenantAlias != "" {
			lookupKey = secret.TenantAPIKey(opts.ContextName, opts.TenantAlias)
		}
		key := resolveCredential(lookupKey, k.Store)
		if key != "" {
			req.Header.Set("X-Api-Key", key)
		}
	}
}

// resolveCredential returns the value for the given lookup key, applying
// precedence env > keychain. env is the env-var name bound to the key by
// the secret package conventions.
//
// We centralize the env check here (not in secret.Store) so any Store
// impl can be plugged in without losing precedence semantics, and so
// the Store contract stays focused on durable storage.
func resolveCredential(lookupKey string, store secret.Store) string {
	envName := envNameForKey(lookupKey)
	if envName != "" {
		if v := os.Getenv(envName); v != "" {
			return v
		}
	}
	v, err := store.Get(lookupKey)
	if err != nil {
		return ""
	}
	return v
}

func envNameForKey(key string) string {
	switch key {
	case "service_token":
		return secret.EnvServiceToken
	default:
		// "api_key" or per-tenant lookup; both map to SRIYACTL_API_KEY
		// (the per-tenant lookup is overridden by env only in CI/headless).
		return secret.EnvAPIKey
	}
}

// FakeDispatch lets tests inject deterministic credentials. Set values
// directly in the fields and they will be applied to every request.
type FakeDispatch struct {
	ServiceToken string
	APIKey       string
	// LastOptions records the most recent call so tests can assert
	// per-call semantics (X-Tenant-Id, lookup key).
	LastOptions AuthCallOptions
}

// SetRequestAuth implements AuthDispatch. Always sets headers if values
// are non-empty; never reads the keychain.
func (f *FakeDispatch) SetRequestAuth(req *http.Request, opts AuthCallOptions) {
	f.LastOptions = opts
	switch opts.Auth {
	case AuthServiceToken:
		if f.ServiceToken != "" {
			req.Header.Set("X-Service-Token", f.ServiceToken)
		}
		if opts.TenantID != "" {
			req.Header.Set("X-Tenant-Id", opts.TenantID)
		}
	case AuthTenantAPIKey:
		if f.APIKey != "" {
			req.Header.Set("X-Api-Key", f.APIKey)
		}
	}
}

// EnvServiceToken returns the current value of SRIYACTL_SERVICE_TOKEN.
// Exposed for diagnostics in the `infra doctor` command (v1).
func EnvServiceToken() string { return os.Getenv(secret.EnvServiceToken) }
