package secret

import "testing"

func TestInMemoryStore_Roundtrip(t *testing.T) {
	s := NewInMemoryStore()
	if err := s.Set("k", "v"); err != nil {
		t.Fatalf("set: %v", err)
	}
	got, err := s.Get("k")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	if got != "v" {
		t.Errorf("got %q, want v", got)
	}
}

func TestInMemoryStore_NotFound(t *testing.T) {
	s := NewInMemoryStore()
	_, err := s.Get("nope")
	if err == nil {
		t.Error("expected error for missing key")
	}
}

func TestInMemoryStore_DeleteMissingIsNoError(t *testing.T) {
	s := NewInMemoryStore()
	if err := s.Delete("never-existed"); err != nil {
		t.Errorf("expected no error, got %v", err)
	}
}

func TestKeyNames(t *testing.T) {
	if got := ContextKey("prod"); got != "sriyactl/prod" {
		t.Errorf("ContextKey: got %q", got)
	}
	if got := TenantAPIKey("prod", "acme"); got != "sriyactl/prod/acme" {
		t.Errorf("TenantAPIKey: got %q", got)
	}
}
