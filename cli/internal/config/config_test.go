package config

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadFrom_MissingFileReturnsEmpty(t *testing.T) {
	dir := t.TempDir()
	c, err := LoadFrom(filepath.Join(dir, "nope.toml"))
	if err != nil {
		t.Fatalf("expected no error for missing file, got %v", err)
	}
	if c.Contexts == nil || c.Tenants == nil {
		t.Error("expected non-nil maps")
	}
}

func TestSaveAndLoad_Roundtrip(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	c, err := LoadFrom(path)
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	c.CurrentContext = "prod"
	c.CurrentTenant = "acme"
	c.UpsertContext("prod", Context{URL: "https://sri.example.com", ServiceTokenRef: "keychain"})
	c.UpsertTenant("prod", "acme", Tenant{ID: "00000000-0000-0000-0000-000000000001", RUC: "1790000000001", Env: "prod"})

	if err := c.SaveAs(path); err != nil {
		t.Fatalf("save: %v", err)
	}

	// Force the viper cache to forget by reading from a fresh handle.
	_ = os.Setenv("XDG_CONFIG_HOME", dir)
	defer os.Unsetenv("XDG_CONFIG_HOME")
	c2, err := LoadFrom(path)
	if err != nil {
		t.Fatalf("reload: %v", err)
	}
	if c2.CurrentContext != "prod" {
		t.Errorf("current_context: want prod, got %q", c2.CurrentContext)
	}
	if c2.CurrentTenant != "acme" {
		t.Errorf("current_tenant: want acme, got %q", c2.CurrentTenant)
	}
	if got := c2.Contexts["prod"].URL; got != "https://sri.example.com" {
		t.Errorf("context url: got %q", got)
	}
	if got := c2.Tenants["prod"]["acme"].RUC; got != "1790000000001" {
		t.Errorf("tenant ruc: got %q", got)
	}
}

func TestActiveTenant_OverrideDoesNotPersist(t *testing.T) {
	c := &Config{
		CurrentTenant: "acme",
		Tenants: map[string]map[string]Tenant{
			"prod": {
				"acme": {ID: "1"},
				"beta": {ID: "2"},
			},
		},
	}
	alias, tnt, ok, hint := c.ActiveTenant("prod", "beta")
	if !ok {
		t.Fatalf("expected hit, got hint=%q", hint)
	}
	if alias != "beta" || tnt.ID != "2" {
		t.Errorf("override not applied: alias=%s id=%s", alias, tnt.ID)
	}
	if c.CurrentTenant != "acme" {
		t.Errorf("override should not mutate CurrentTenant, got %q", c.CurrentTenant)
	}
}

func TestActiveTenant_Missing(t *testing.T) {
	c := &Config{Tenants: map[string]map[string]Tenant{"prod": {}}}
	_, _, ok, hint := c.ActiveTenant("prod", "")
	if ok {
		t.Error("expected miss when no current_tenant set")
	}
	if hint == "" {
		t.Error("expected non-empty hint for no-active-tenant case")
	}
}

func TestActiveContext_NotFound(t *testing.T) {
	c := &Config{Contexts: map[string]Context{}}
	_, _, err := c.ActiveContext("ghost")
	if err == nil {
		t.Error("expected error for unknown context")
	}
}
