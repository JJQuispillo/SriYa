// Package config loads ~/.config/sriyactl/config.toml via viper. It holds
// ONLY non-secret values: context URLs, current_context/current_tenant,
// tenant aliases + ids. Service tokens and api keys NEVER live in this
// file — they are in the OS keychain (internal/secret) or in env vars.
package config

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/spf13/viper"
)

const (
	// AppName is the directory name under ~/.config used for sriyactl.
	AppName = "sriyactl"
	// ConfigFile is the file name inside the config dir.
	ConfigFile = "config.toml"
)

// EnvHomeOverride is the env var that overrides the install dir resolution.
const EnvHomeOverride = "SRIYACTL_HOME"

// DefaultConfigDir returns ~/.config/sriyactl, honoring XDG_CONFIG_HOME.
func DefaultConfigDir() (string, error) {
	if dir := os.Getenv("XDG_CONFIG_HOME"); dir != "" {
		return filepath.Join(dir, AppName), nil
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return "", fmt.Errorf("resolve home dir: %w", err)
	}
	return filepath.Join(home, ".config", AppName), nil
}

// Config is the in-memory view of config.toml.
type Config struct {
	// CurrentContext is the active kubectl-style context name.
	CurrentContext string `toml:"current_context" mapstructure:"current_context"`
	// CurrentTenant is the alias of the active tenant within CurrentContext.
	CurrentTenant string `toml:"current_tenant" mapstructure:"current_tenant"`
	// Contexts maps context name to its config.
	Contexts map[string]Context `toml:"contexts" mapstructure:"contexts"`
	// Tenants maps context name to its known tenants.
	Tenants map[string]map[string]Tenant `toml:"tenants" mapstructure:"tenants"`
}

// Context is a single context (host) definition.
type Context struct {
	// URL is the base URL of the billing API (e.g. https://sri.example.com).
	URL string `toml:"url" mapstructure:"url"`
	// ServiceTokenRef is always "keychain" for v1. The actual token is
	// fetched at runtime from the OS keychain.
	ServiceTokenRef string `toml:"service_token_ref" mapstructure:"service_token_ref"`
	// ReadOnly, when true, marks this context as read-only by default.
	// SRIYACTL_READONLY=1 still wins.
	ReadOnly bool `toml:"read_only" mapstructure:"read_only"`
}

// Tenant is a per-context tenant record (alias → id + metadata).
type Tenant struct {
	ID  string `toml:"id" mapstructure:"id"`
	RUC string `toml:"ruc" mapstructure:"ruc"`
	Env string `toml:"env" mapstructure:"env"`
}

// Load reads the config from disk. If the file does not exist, an empty
// Config is returned (first run after install).
func Load() (*Config, error) {
	dir, err := DefaultConfigDir()
	if err != nil {
		return nil, err
	}
	return LoadFrom(filepath.Join(dir, ConfigFile))
}

// LoadFrom reads config from an explicit path. The file is allowed to be
// missing; in that case an empty Config is returned. The parent dir is
// created lazily on the first Save.
func LoadFrom(path string) (*Config, error) {
	v := viper.New()
	v.SetConfigFile(path)
	v.SetConfigType("toml")
	// Sensible defaults so a fresh install does not crash.
	v.SetDefault("current_context", "")
	v.SetDefault("current_tenant", "")
	v.SetDefault("contexts", map[string]Context{})
	v.SetDefault("tenants", map[string]map[string]Tenant{})

	if err := v.ReadInConfig(); err != nil {
		var nf viper.ConfigFileNotFoundError
		if errors.As(err, &nf) {
			return emptyConfig(), nil
		}
		// Also accept plain fs.PathError so callers passing a custom
		// path (e.g. tests in a temp dir) get the same fresh-install
		// behavior as a real first run.
		if os.IsNotExist(err) {
			return emptyConfig(), nil
		}
		return nil, fmt.Errorf("read config: %w", err)
	}

	var c Config
	if err := v.Unmarshal(&c); err != nil {
		return nil, fmt.Errorf("parse config: %w", err)
	}
	if c.Contexts == nil {
		c.Contexts = map[string]Context{}
	}
	if c.Tenants == nil {
		c.Tenants = map[string]map[string]Tenant{}
	}
	return &c, nil
}

// Save writes the config atomically (temp file + rename) and creates the
// parent directory if missing.
func (c *Config) Save() error {
	return c.SaveAs(mustDefaultPath())
}

// SaveAs is Save but with an explicit path (used by tests).
func (c *Config) SaveAs(path string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return fmt.Errorf("create config dir: %w", err)
	}
	v := viper.New()
	v.SetConfigFile(path)
	v.SetConfigType("toml")
	v.Set("current_context", c.CurrentContext)
	v.Set("current_tenant", c.CurrentTenant)
	v.Set("contexts", c.Contexts)
	v.Set("tenants", c.Tenants)

	tmp, err := os.CreateTemp(filepath.Dir(path), ".config-*.toml")
	if err != nil {
		return fmt.Errorf("create temp: %w", err)
	}
	tmpName := tmp.Name()
	defer os.Remove(tmpName)
	if err := v.WriteConfigAs(tmpName); err != nil {
		tmp.Close()
		return fmt.Errorf("write temp config: %w", err)
	}
	if err := tmp.Close(); err != nil {
		return fmt.Errorf("close temp: %w", err)
	}
	if err := os.Rename(tmpName, path); err != nil {
		return fmt.Errorf("rename config: %w", err)
	}
	return nil
}

func mustDefaultPath() string {
	dir, err := DefaultConfigDir()
	if err != nil {
		panic(err) // unreachable in normal use
	}
	return filepath.Join(dir, ConfigFile)
}

func emptyConfig() *Config {
	return &Config{
		Contexts: map[string]Context{},
		Tenants:  map[string]map[string]Tenant{},
	}
}

// ActiveContext returns the named context, falling back to CurrentContext.
func (c *Config) ActiveContext(name string) (string, Context, error) {
	if name != "" {
		ctx, ok := c.Contexts[name]
		if !ok {
			return "", Context{}, fmt.Errorf("context %q not found", name)
		}
		return name, ctx, nil
	}
	if c.CurrentContext == "" {
		return "", Context{}, errors.New("no current_context set (use `sriyactl context use <name>` first)")
	}
	return c.CurrentContext, c.Contexts[c.CurrentContext], nil
}

// ActiveTenant returns the alias → Tenant for the given context, applying
// the override or current_tenant. Returns (alias, tenant, true, "") on hit.
// The third return is false when no tenant is active; the fourth is an
// error message suitable for a CLIError hint.
func (c *Config) ActiveTenant(ctxName string, override string) (string, Tenant, bool, string) {
	tenants := c.Tenants[ctxName]
	alias := override
	if alias == "" {
		alias = c.CurrentTenant
	}
	if alias == "" {
		return "", Tenant{}, false, "no active tenant (use `sriyactl tenant use <alias>` or pass --tenant <alias>`)"
	}
	t, ok := tenants[alias]
	if !ok {
		return "", Tenant{}, false, fmt.Sprintf("tenant %q not found in context %q", alias, ctxName)
	}
	return alias, t, true, ""
}

// SetCurrent persists a new current_context/current_tenant pair.
func (c *Config) SetCurrent(ctx, tenant string) {
	c.CurrentContext = ctx
	c.CurrentTenant = tenant
}

// UpsertContext adds or replaces a context.
func (c *Config) UpsertContext(name string, ctx Context) {
	c.Contexts[name] = ctx
}

// UpsertTenant adds or replaces a tenant under a context.
func (c *Config) UpsertTenant(ctxName, alias string, t Tenant) {
	if c.Tenants[ctxName] == nil {
		c.Tenants[ctxName] = map[string]Tenant{}
	}
	c.Tenants[ctxName][alias] = t
}

// NormalizeKey strips whitespace and lowercases, used to build stable
// keychain keys.
func NormalizeKey(s string) string {
	return strings.ToLower(strings.TrimSpace(s))
}
