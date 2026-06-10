package core

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"github.com/JJQuispillo/billing/cli/internal/compose"
)

// jsonUnmarshal is a tiny indirection so core stays decoupled from
// encoding/json in the public symbols. The error is also normalized to
// avoid leaking stdlib types in non-test signatures.
func jsonUnmarshal(data string, v any) error {
	d := json.NewDecoder(strings.NewReader(data))
	d.DisallowUnknownFields()
	return d.Decode(v)
}

// readEnvVar reads a single key from the install dir's .env file. It
// returns "" (and no error) when the key is missing — callers can treat
// that as a soft check.
func readEnvVar(dir, key string) (string, error) {
	f, err := os.Open(filepath.Join(dir, ".env"))
	if err != nil {
		return "", err
	}
	defer f.Close()
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
		k := strings.TrimSpace(line[:eq])
		if k != key {
			continue
		}
		v := strings.TrimSpace(line[eq+1:])
		v = strings.Trim(v, `"'`)
		return v, nil
	}
	return "", nil
}

// writeEnvVar replaces a key=value line in the install dir's .env file,
// preserving comments and other lines. If the key is missing, it is
// appended.
func writeEnvVar(dir, key, value string) error {
	path := filepath.Join(dir, ".env")
	in, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	lines := strings.Split(string(in), "\n")
	prefix := key + "="
	found := false
	for i, ln := range lines {
		trimmed := strings.TrimSpace(ln)
		if strings.HasPrefix(trimmed, prefix) {
			lines[i] = fmt.Sprintf("%s=%q", key, value)
			found = true
			break
		}
	}
	if !found {
		lines = append(lines, fmt.Sprintf("%s=%q", key, value))
	}
	out := strings.Join(lines, "\n")
	return os.WriteFile(path, []byte(out), 0o600)
}

// hasRunningServiceNamed does a lightweight scan of `compose ps` JSON
// for a service whose name or service key matches.
func hasRunningServiceNamed(stdout, want string) bool {
	for _, ln := range strings.Split(stdout, "\n") {
		ln = strings.TrimSpace(ln)
		if !strings.HasPrefix(ln, "{") {
			continue
		}
		var s struct {
			Name    string `json:"Name"`
			Service string `json:"Service"`
			State   string `json:"State"`
		}
		if err := json.Unmarshal([]byte(ln), &s); err != nil {
			continue
		}
		if (strings.Contains(s.Name, want) || strings.Contains(s.Service, want)) &&
			(s.State == "running" || s.State == "Running") {
			return true
		}
	}
	return false
}

// lookPath is a thin wrapper around exec.LookPath; centralizes future
// overrides (e.g. a bundled docker binary). Exposed as a var so unit
// tests in this package can swap it for a deterministic stub without
// requiring a real `docker` binary in PATH.
var lookPath = exec.LookPath

// writeFile is a stdlib wrapper that exists so infra.go can call it
// without importing os at the top of every file.
func writeFile(path, content string) error {
	return os.WriteFile(path, []byte(content), 0o600)
}

// fileInfo wraps os.Stat.
func fileInfo(path string) (os.FileInfo, error) { return os.Stat(path) }

// fileExists wraps os.Stat for the "exists" check.
func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

// restoreViaStdin streams a file into `compose exec -T postgres psql`.
// We shell out directly here because compose.Run captures stdout but we
// need to feed stdin. Exposed as a var so unit tests can swap it for a
// deterministic stub that records the call and returns nil — the real
// implementation requires a live docker daemon.
var restoreViaStdin = func(d InfraDeps, path string) error {
	ctx := context.Background()
	// We can't reuse d.Compose.Stream because it doesn't accept stdin;
	// the simpler approach is to cat the file and pipe it via exec.Cmd
	// with the install dir as working dir.
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	// docker compose exec -T billing-db psql -U <billing_user> qora_billing < path
	// F1 fix: the real stack uses service billing-db / role billing_user /
	// db qora_billing (read from .env so a custom --db-user install works).
	dbUser, _ := readEnvVar(d.Compose.InstallDir(), "BILLING_DB_USER")
	if dbUser == "" {
		dbUser = defaultDBUser
	}
	cmd := exec.CommandContext(ctx, "docker", "compose", "exec", "-T", dbService, "psql", "-U", dbUser, dbName)
	cmd.Dir = d.Compose.InstallDir()
	cmd.Stdin = f
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("psql exec failed: %w", err)
	}
	return nil
}

// ensure compose is imported (used via InfraDeps).
var _ = compose.EnvHomeOverride
