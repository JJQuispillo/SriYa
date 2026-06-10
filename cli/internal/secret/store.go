// Package secret wraps OS keychain access and env-var fallback. The CLI
// never writes secrets to disk: api keys and the service token live in
// the keychain (or in env vars for CI/headless contexts). See design.md
// "Config & Secret Model".
package secret

import (
	"fmt"
	"os"

	"github.com/JJQuispillo/billing/cli/internal/errs"
	gokeyring "github.com/zalando/go-keyring"
)

// EnvServiceToken is the env-var override for the service token.
const EnvServiceToken = "SRIYACTL_SERVICE_TOKEN"

// EnvAPIKey is the env-var override for a per-tenant api key.
const EnvAPIKey = "SRIYACTL_API_KEY"

// KeychainPrefix is the namespace under which all sriyactl entries live.
// go-keyring uses the format `service:user`; we keep `service` constant
// and put the routing info in `user`.
const KeychainPrefix = "sriyactl"

// Store is the interface handlers depend on. Tests use the InMemory impl
// from store_memory.go; production uses KeyringStore.
type Store interface {
	Get(key string) (string, error)
	Set(key, val string) error
	Delete(key string) error
}

// KeyringStore uses the OS keychain via zalando/go-keyring.
type KeyringStore struct{}

// NewKeyringStore returns a Store backed by the OS keychain.
func NewKeyringStore() *KeyringStore { return &KeyringStore{} }

// Get reads a secret. If the env var override is set, it wins.
func (k *KeyringStore) Get(key string) (string, error) {
	if v, ok := envOverride(key); ok {
		return v, nil
	}
	v, err := gokeyring.Get(KeychainPrefix, key)
	if err != nil {
		if err == gokeyring.ErrNotFound {
			return "", errs.New(errs.CodeNotFound, "secret not found in keychain: "+key, "set it via `sriyactl context use` or "+envNameFor(key))
		}
		return "", errs.Wrap(errs.CodeGeneric, err, "keychain get failed", "check that a keychain service is available (Linux: secret-service, macOS: login keychain)")
	}
	return v, nil
}

// Set writes a secret to the keychain. The env var override is read-only
// (CI never writes to the keychain).
func (k *KeyringStore) Set(key, val string) error {
	if err := gokeyring.Set(KeychainPrefix, key, val); err != nil {
		return errs.Wrap(errs.CodeGeneric, err, "keychain set failed", "check keychain permissions")
	}
	return nil
}

// Delete removes a secret. Missing entries are not an error.
func (k *KeyringStore) Delete(key string) error {
	if err := gokeyring.Delete(KeychainPrefix, key); err != nil && err != gokeyring.ErrNotFound {
		return errs.Wrap(errs.CodeGeneric, err, "keychain delete failed", "check keychain permissions")
	}
	return nil
}

// ContextKey returns the keychain key for a context's service token.
func ContextKey(contextName string) string {
	return fmt.Sprintf("%s/%s", KeychainPrefix, contextName)
}

// TenantAPIKey returns the keychain key for a tenant's api key.
func TenantAPIKey(contextName, tenantAlias string) string {
	return fmt.Sprintf("%s/%s/%s", KeychainPrefix, contextName, tenantAlias)
}

func envOverride(key string) (string, bool) {
	switch key {
	case "service_token":
		if v := os.Getenv(EnvServiceToken); v != "" {
			return v, true
		}
	case "api_key":
		if v := os.Getenv(EnvAPIKey); v != "" {
			return v, true
		}
	}
	return "", false
}

func envNameFor(key string) string {
	switch key {
	case "service_token":
		return EnvServiceToken
	case "api_key":
		return EnvAPIKey
	default:
		return ""
	}
}
