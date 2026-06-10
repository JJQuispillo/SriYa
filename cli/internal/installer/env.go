package installer

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// EnvConfig carries the NON-secret, operator-controlled values that go into
// the rendered .env. The secret values (passwords, tokens, encryption key)
// are NEVER passed in — they are generated internally so a secret never
// transits a flag, a config file, or a log line.
type EnvConfig struct {
	// Version is written to BILLING_IMAGE_TAG (the pinned image tag).
	Version string
	// Port is written to BILLING_API_PORT (host port for the API).
	Port string
	// CorsOrigin is written to CORS_ORIGIN_0 (allowed frontend origin).
	CorsOrigin string
	// DBUser is written to BILLING_DB_USER (owner/bootstrap DB role).
	DBUser string
}

// env defaults mirror install.sh so a zero-value field renders the same
// value the bash installer would have used.
const (
	defaultVersion    = "1.0.0"
	defaultPort       = "8080"
	defaultCorsOrigin = "http://localhost:3000"
	defaultDBUser     = "billing_user"
)

// withDefaults returns a copy of c with empty fields filled from the
// install.sh defaults.
func (c EnvConfig) withDefaults() EnvConfig {
	if c.Version == "" {
		c.Version = defaultVersion
	}
	if c.Port == "" {
		c.Port = defaultPort
	}
	if c.CorsOrigin == "" {
		c.CorsOrigin = defaultCorsOrigin
	}
	if c.DBUser == "" {
		c.DBUser = defaultDBUser
	}
	return c
}

// EnvKeys is the exact set of keys RenderEnv writes, in render order. It is
// exported so tests (and a future `infra doctor` post-install check) can
// assert the contract without re-listing the keys. These 9 keys are the
// ones install.sh emits — verified against billing/install.sh:98-123 and
// docker-compose.prod.yml.
var EnvKeys = []string{
	"BILLING_IMAGE_TAG",
	"BILLING_API_PORT",
	"BILLING_DB_USER",
	"BILLING_DB_PASSWORD",
	"BILLING_APP_DB_PASSWORD",
	"BILLING_PRIVILEGED_DB_PASSWORD",
	"SERVICE_AUTH_TOKEN",
	"ENCRYPTION_KEY",
	"CORS_ORIGIN_0",
}

// RenderEnv writes a fresh .env into dir with strong random secrets for the
// five secret keys (BILLING_DB_PASSWORD, BILLING_APP_DB_PASSWORD,
// BILLING_PRIVILEGED_DB_PASSWORD, SERVICE_AUTH_TOKEN, ENCRYPTION_KEY) and
// the operator values for the rest.
//
// Behavior matches install.sh's secret/.env step:
//   - NO-CLOBBER: if dir/.env already exists, RenderEnv returns
//     (created=false, nil) and leaves the existing file untouched. This is
//     what makes `infra install` idempotent — re-running never rotates
//     secrets or loses data.
//   - chmod 600: the file is created with 0600 (owner-only) so the secrets
//     are not world-readable.
//   - Atomic-ish: the file is written via O_CREATE|O_EXCL, so a race where
//     the file appears between the stat and the write still no-clobbers.
//
// Returns created=true only when a new .env was written.
func RenderEnv(dir string, c EnvConfig) (created bool, err error) {
	c = c.withDefaults()
	path := filepath.Join(dir, ".env")

	// No-clobber pre-check (fast path). The O_EXCL below is the real
	// guarantee against a TOCTOU race.
	if _, statErr := os.Stat(path); statErr == nil {
		return false, nil
	}

	if mkErr := os.MkdirAll(dir, 0o755); mkErr != nil {
		return false, errs.Wrap(errs.CodeGeneric, mkErr, "create install dir", "check permissions on the parent directory")
	}

	// Generate the five secrets up front so a CSPRNG failure aborts before
	// any file is created (never leave a half-written .env).
	dbPass, err := GenSecret(DefaultSecretLen)
	if err != nil {
		return false, err
	}
	appPass, err := GenSecret(DefaultSecretLen)
	if err != nil {
		return false, err
	}
	privPass, err := GenSecret(DefaultSecretLen)
	if err != nil {
		return false, err
	}
	serviceToken, err := GenSecret(DefaultSecretLen)
	if err != nil {
		return false, err
	}
	encKey, err := GenSecret(DefaultSecretLen)
	if err != nil {
		return false, err
	}

	values := map[string]string{
		"BILLING_IMAGE_TAG":              c.Version,
		"BILLING_API_PORT":               c.Port,
		"BILLING_DB_USER":                c.DBUser,
		"BILLING_DB_PASSWORD":            dbPass,
		"BILLING_APP_DB_PASSWORD":        appPass,
		"BILLING_PRIVILEGED_DB_PASSWORD": privPass,
		"SERVICE_AUTH_TOKEN":             serviceToken,
		"ENCRYPTION_KEY":                 encKey,
		"CORS_ORIGIN_0":                  c.CorsOrigin,
	}

	body := renderEnvBody(values)

	// O_EXCL: fail (and no-clobber) if the file appeared since the stat.
	f, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if err != nil {
		if os.IsExist(err) {
			return false, nil
		}
		return false, errs.Wrap(errs.CodeGeneric, err, "create .env", "check permissions on the install dir")
	}
	if _, werr := f.WriteString(body); werr != nil {
		_ = f.Close()
		_ = os.Remove(path) // never leave a partial secrets file
		return false, errs.Wrap(errs.CodeGeneric, werr, "write .env", "check disk space and permissions")
	}
	if cerr := f.Close(); cerr != nil {
		_ = os.Remove(path)
		return false, errs.Wrap(errs.CodeGeneric, cerr, "close .env", "check disk space")
	}
	// Belt-and-suspenders: enforce 0600 even if umask widened the create
	// mode on some platforms.
	if chErr := os.Chmod(path, 0o600); chErr != nil {
		return false, errs.Wrap(errs.CodeGeneric, chErr, "chmod .env to 0600", "fix permissions manually")
	}
	return true, nil
}

// renderEnvBody builds the .env contents, grouping keys with the same
// commented section headers install.sh uses so a human reading the file
// recognizes it.
func renderEnvBody(v map[string]string) string {
	var b strings.Builder
	fmt.Fprintf(&b, "# Generated by sriyactl on %s\n", time.Now().UTC().Format(time.RFC3339))
	b.WriteString("# Review CORS_ORIGIN_0 before exposing this service publicly.\n\n")

	b.WriteString("# Pinned image version. Bump this and 'docker compose pull && up -d' to upgrade.\n")
	fmt.Fprintf(&b, "BILLING_IMAGE_TAG=%s\n\n", v["BILLING_IMAGE_TAG"])

	fmt.Fprintf(&b, "BILLING_API_PORT=%s\n\n", v["BILLING_API_PORT"])

	b.WriteString("# --- Database (rol propietario/bootstrap) ---\n")
	fmt.Fprintf(&b, "BILLING_DB_USER=%s\n", v["BILLING_DB_USER"])
	fmt.Fprintf(&b, "BILLING_DB_PASSWORD=%s\n\n", v["BILLING_DB_PASSWORD"])

	b.WriteString("# --- Runtime roles (RLS multi-emisor) ---\n")
	fmt.Fprintf(&b, "BILLING_APP_DB_PASSWORD=%s\n", v["BILLING_APP_DB_PASSWORD"])
	fmt.Fprintf(&b, "BILLING_PRIVILEGED_DB_PASSWORD=%s\n\n", v["BILLING_PRIVILEGED_DB_PASSWORD"])

	b.WriteString("# --- Service-to-service auth ---\n")
	fmt.Fprintf(&b, "SERVICE_AUTH_TOKEN=%s\n\n", v["SERVICE_AUTH_TOKEN"])

	b.WriteString("# --- Certificate-key encryption (at rest) ---\n")
	fmt.Fprintf(&b, "ENCRYPTION_KEY=%s\n\n", v["ENCRYPTION_KEY"])

	b.WriteString("# --- CORS ---\n")
	fmt.Fprintf(&b, "CORS_ORIGIN_0=%s\n", v["CORS_ORIGIN_0"])

	return b.String()
}
