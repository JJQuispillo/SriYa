package installer

import (
	"bufio"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// parseEnvFile parses a rendered .env into a key→value map, skipping
// comments and blank lines.
func parseEnvFile(t *testing.T, path string) map[string]string {
	t.Helper()
	f, err := os.Open(path)
	if err != nil {
		t.Fatalf("open .env: %v", err)
	}
	defer f.Close()
	out := map[string]string{}
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		eq := strings.IndexByte(line, '=')
		if eq <= 0 {
			continue
		}
		out[strings.TrimSpace(line[:eq])] = strings.TrimSpace(line[eq+1:])
	}
	return out
}

func TestRenderEnv_WritesAllNineKeys(t *testing.T) {
	dir := t.TempDir()
	created, err := RenderEnv(dir, EnvConfig{Version: "1.4.0", Port: "9090", CorsOrigin: "https://app.example.com", DBUser: "owner"})
	if err != nil {
		t.Fatalf("RenderEnv: %v", err)
	}
	if !created {
		t.Fatal("expected created=true on a fresh dir")
	}
	kv := parseEnvFile(t, filepath.Join(dir, ".env"))

	if len(EnvKeys) != 9 {
		t.Fatalf("EnvKeys should be the 9-key contract, got %d", len(EnvKeys))
	}
	for _, k := range EnvKeys {
		if _, ok := kv[k]; !ok {
			t.Errorf("missing key %s in rendered .env", k)
		}
	}
	if len(kv) != 9 {
		t.Errorf("rendered .env has %d keys, want exactly 9: %v", len(kv), kv)
	}

	// Operator (non-secret) values must be passed through verbatim.
	if kv["BILLING_IMAGE_TAG"] != "1.4.0" {
		t.Errorf("BILLING_IMAGE_TAG = %q, want 1.4.0", kv["BILLING_IMAGE_TAG"])
	}
	if kv["BILLING_API_PORT"] != "9090" {
		t.Errorf("BILLING_API_PORT = %q, want 9090", kv["BILLING_API_PORT"])
	}
	if kv["CORS_ORIGIN_0"] != "https://app.example.com" {
		t.Errorf("CORS_ORIGIN_0 = %q", kv["CORS_ORIGIN_0"])
	}
	if kv["BILLING_DB_USER"] != "owner" {
		t.Errorf("BILLING_DB_USER = %q, want owner", kv["BILLING_DB_USER"])
	}
}

func TestRenderEnv_SecretsAreCharsetSafe(t *testing.T) {
	dir := t.TempDir()
	if _, err := RenderEnv(dir, EnvConfig{}); err != nil {
		t.Fatalf("RenderEnv: %v", err)
	}
	kv := parseEnvFile(t, filepath.Join(dir, ".env"))

	secretKeys := []string{
		"BILLING_DB_PASSWORD",
		"BILLING_APP_DB_PASSWORD",
		"BILLING_PRIVILEGED_DB_PASSWORD",
		"SERVICE_AUTH_TOKEN",
		"ENCRYPTION_KEY",
	}
	for _, k := range secretKeys {
		v := kv[k]
		if len(v) != DefaultSecretLen {
			t.Errorf("%s length = %d, want %d", k, len(v), DefaultSecretLen)
		}
		if !alnum.MatchString(v) {
			t.Errorf("%s = %q is not [A-Za-z0-9]", k, v)
		}
	}
	// The five secrets must all differ from each other.
	values := map[string]bool{}
	for _, k := range secretKeys {
		if values[kv[k]] {
			t.Errorf("duplicate secret value for %s", k)
		}
		values[kv[k]] = true
	}
}

func TestRenderEnv_NoClobber(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, ".env")
	const sentinel = "PRE_EXISTING=keepme\n"
	if err := os.WriteFile(path, []byte(sentinel), 0o600); err != nil {
		t.Fatal(err)
	}

	created, err := RenderEnv(dir, EnvConfig{})
	if err != nil {
		t.Fatalf("RenderEnv: %v", err)
	}
	if created {
		t.Error("expected created=false when .env already exists")
	}
	got, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if string(got) != sentinel {
		t.Errorf("existing .env was clobbered: got %q, want %q", got, sentinel)
	}
}

func TestRenderEnv_AppliesDefaults(t *testing.T) {
	dir := t.TempDir()
	if _, err := RenderEnv(dir, EnvConfig{}); err != nil {
		t.Fatalf("RenderEnv: %v", err)
	}
	kv := parseEnvFile(t, filepath.Join(dir, ".env"))
	if kv["BILLING_IMAGE_TAG"] != defaultVersion {
		t.Errorf("default version = %q, want %q", kv["BILLING_IMAGE_TAG"], defaultVersion)
	}
	if kv["BILLING_API_PORT"] != defaultPort {
		t.Errorf("default port = %q, want %q", kv["BILLING_API_PORT"], defaultPort)
	}
	if kv["BILLING_DB_USER"] != defaultDBUser {
		t.Errorf("default db user = %q, want %q", kv["BILLING_DB_USER"], defaultDBUser)
	}
	if kv["CORS_ORIGIN_0"] != defaultCorsOrigin {
		t.Errorf("default cors = %q, want %q", kv["CORS_ORIGIN_0"], defaultCorsOrigin)
	}
}

func TestRenderEnv_Chmod600(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("unix file modes not meaningful on windows")
	}
	dir := t.TempDir()
	if _, err := RenderEnv(dir, EnvConfig{}); err != nil {
		t.Fatalf("RenderEnv: %v", err)
	}
	info, err := os.Stat(filepath.Join(dir, ".env"))
	if err != nil {
		t.Fatal(err)
	}
	if perm := info.Mode().Perm(); perm != 0o600 {
		t.Errorf(".env perm = %o, want 600", perm)
	}
}
