package config

import (
	"errors"
	"path/filepath"
	"testing"
)

func TestTenantsStore_ListGetUpsert(t *testing.T) {
	dir := t.TempDir()
	c, _ := LoadFrom(filepath.Join(dir, "config.toml"))
	c.UpsertContext("prod", Context{URL: "https://sri.example.com", ServiceTokenRef: "keychain"})
	if err := c.SaveAs(filepath.Join(dir, "config.toml")); err != nil {
		t.Fatal(err)
	}

	store := NewTenantsStore(c)
	if err := store.Upsert("prod", TenantRef{Alias: "acme", ID: "id-1", RUC: "1790000000001", Env: "prod"}); err != nil {
		t.Fatalf("upsert: %v", err)
	}
	got, err := store.Get("prod", "acme")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	if got.ID != "id-1" {
		t.Errorf("id: %q", got.ID)
	}

	list, err := store.ListKnown("prod")
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(list) != 1 || list[0].Alias != "acme" {
		t.Errorf("list: %+v", list)
	}
}

func TestTenantsStore_GetNotFound(t *testing.T) {
	dir := t.TempDir()
	c, _ := LoadFrom(filepath.Join(dir, "config.toml"))
	store := NewTenantsStore(c)
	_, err := store.Get("prod", "ghost")
	if !errors.Is(err, ErrTenantNotFound) {
		t.Errorf("expected ErrTenantNotFound, got %v", err)
	}
}

func TestTenantsStore_ActiveRequiresSet(t *testing.T) {
	dir := t.TempDir()
	c, _ := LoadFrom(filepath.Join(dir, "config.toml"))
	store := NewTenantsStore(c)
	_, err := store.Active("prod")
	if !errors.Is(err, ErrNoActiveTenant) {
		t.Errorf("expected ErrNoActiveTenant, got %v", err)
	}
}

func TestTenantsStore_SetCurrentValidatesAlias(t *testing.T) {
	dir := t.TempDir()
	c, _ := LoadFrom(filepath.Join(dir, "config.toml"))
	c.CurrentContext = "prod"
	store := NewTenantsStore(c)
	if err := store.SetCurrent("prod", "ghost"); !errors.Is(err, ErrTenantNotFound) {
		t.Errorf("expected ErrTenantNotFound, got %v", err)
	}
}
