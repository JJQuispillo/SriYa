package compose

import (
	"path/filepath"
	"testing"
)

func TestIsInstallDir(t *testing.T) {
	dir := t.TempDir()
	if isInstallDir(dir) {
		t.Error("empty temp dir should not be a valid install dir")
	}
	// Drop the .env + docker-compose.yml; should now be valid.
	if err := writeFile(filepath.Join(dir, ".env"), "TAG=foo"); err != nil {
		t.Fatal(err)
	}
	if err := writeFile(filepath.Join(dir, "docker-compose.yml"), "services: {}"); err != nil {
		t.Fatal(err)
	}
	if !isInstallDir(dir) {
		t.Error("expected valid install dir after creating both files")
	}
}

func TestResolveInstallDir_PrefersOverride(t *testing.T) {
	dir := t.TempDir()
	writeBoth(t, dir)
	other := t.TempDir()
	writeBoth(t, other)
	got, err := resolveInstallDir(dir)
	if err != nil {
		t.Fatalf("expected to find %s, got %v", dir, err)
	}
	abs, _ := filepath.Abs(dir)
	if got != abs {
		t.Errorf("expected abs of %s, got %s", dir, got)
	}
}

func TestValidateInstallDir_Missing(t *testing.T) {
	dir := t.TempDir()
	r := &ExecRunner{Dir: dir, EnvFile: ".env", ComposeFile: "docker-compose.yml"}
	err := r.ValidateInstallDir()
	if err == nil {
		t.Error("expected error for empty install dir")
	}
}

func writeFile(path, content string) error {
	return writeFileImpl(path, content)
}

func writeBoth(t *testing.T, dir string) {
	t.Helper()
	if err := writeFile(filepath.Join(dir, ".env"), "TAG=x"); err != nil {
		t.Fatal(err)
	}
	if err := writeFile(filepath.Join(dir, "docker-compose.yml"), "x: 1"); err != nil {
		t.Fatal(err)
	}
}
