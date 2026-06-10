package ops

import (
	"bufio"
	"context"
	"fmt"
	"io"
	"os"
	"strings"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/config"
	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/secret"
)

// serviceTokenKey is the keychain/env lookup key for the service token. It
// MUST match the key resolved by api.KeyringDispatch
// (resolveCredential("service_token", …)) so a token seeded here is found by
// the chained bootstrap and later `tenant create`.
const serviceTokenKey = "service_token"

// NewSecretStore returns the production keychain-backed store. Tests
// inject their own.
func NewSecretStore() secret.Store { return secret.NewKeyringStore() }

// InstallSeeder is the production core.ContextSeeder used by `infra install`.
// It registers a local context in config.toml (host URL of the freshly
// provisioned stack) and stores the service token in the OS keychain, with a
// graceful fallback to the SRIYACTL_SERVICE_TOKEN env var when no secret
// service is available (headless / Linux without secret-service).
type InstallSeeder struct {
	store secret.Store
	// configPath overrides the config.toml path (tests). Empty → default.
	configPath string
}

// NewInstallSeeder builds the production seeder over the given secret store.
func NewInstallSeeder(store secret.Store) *InstallSeeder {
	return &InstallSeeder{store: store}
}

// Seed implements core.ContextSeeder.
func (s *InstallSeeder) Seed(_ context.Context, in core.SeedInput) (core.SeedResult, error) {
	name := in.ContextName
	if name == "" {
		name = "local"
	}

	// 1. Register the context in config.toml (idempotent upsert).
	cfg, err := s.loadConfig()
	if err != nil {
		return core.SeedResult{}, errs.Wrap(errs.CodeConfigInvalid, err, "load config to seed local context", "check ~/.config/sriyactl/config.toml")
	}
	cfg.UpsertContext(name, config.Context{
		URL:             in.URL,
		ServiceTokenRef: "keychain",
	})
	// Make the seeded context current so a follow-up `tenant create` resolves
	// it without an extra `context use`. Only set it if there is no current
	// context yet (do not stomp an operator's existing selection on a re-run).
	if cfg.CurrentContext == "" {
		cfg.SetCurrent(name, cfg.CurrentTenant)
	}
	if err := s.saveConfig(cfg); err != nil {
		return core.SeedResult{}, errs.Wrap(errs.CodeConfigInvalid, err, "save config with seeded local context", "check write permissions on ~/.config/sriyactl")
	}

	// 2. Store the service token. Prefer the keychain; on failure fall back to
	//    the env var WITHOUT aborting (spec: report the fallback mode).
	res := core.SeedResult{ContextName: name}
	if in.ServiceToken == "" {
		// Nothing to store (a re-run on a pre-existing .env may not surface
		// the token). The context was still seeded.
		return res, nil
	}
	// If the env override is already set, that wins at resolution time; treat
	// it as the fallback mode and do not fight it.
	if os.Getenv(secret.EnvServiceToken) != "" {
		res.TokenFallbackEnv = true
		return res, nil
	}
	if err := s.store.Set(serviceTokenKey, in.ServiceToken); err != nil {
		// Keychain unavailable (headless / no secret-service). Set the env var
		// for THIS process so the immediately-following bootstrap can
		// authenticate, and report the fallback so the operator persists it.
		_ = os.Setenv(secret.EnvServiceToken, in.ServiceToken)
		res.TokenFallbackEnv = true
		return res, nil
	}
	return res, nil
}

func (s *InstallSeeder) loadConfig() (*config.Config, error) {
	if s.configPath != "" {
		return config.LoadFrom(s.configPath)
	}
	return config.Load()
}

func (s *InstallSeeder) saveConfig(c *config.Config) error {
	if s.configPath != "" {
		return c.SaveAs(s.configPath)
	}
	return c.Save()
}

// BootstrapPrompter is the production core.BootstrapInteractor for the
// cobra path. On a TTY it asks for any bootstrap field the operator did not
// pass as a flag. Secrets (the owner password) are read with echo on for v1
// simplicity; the certificate is a path. Empty answers keep whatever flag
// value was already set. The TUI does NOT use this prompter: its huh form
// collects every field up-front (design D5).
type BootstrapPrompter struct {
	in  io.Reader
	out io.Writer
}

// NewBootstrapPrompter wires the prompter to stdin/stderr (prompts go to
// stderr so stdout stays structured).
func NewBootstrapPrompter(out io.Writer) *BootstrapPrompter {
	return &BootstrapPrompter{in: os.Stdin, out: out}
}

// Prompt implements core.BootstrapInteractor.
func (p *BootstrapPrompter) Prompt(req api.BootstrapRequest) (api.BootstrapRequest, error) {
	r := bufio.NewReader(p.in)
	fmt.Fprintln(p.out, "==> Bootstrap del primer tenant (deja vacío para conservar el valor de un flag):")
	req.RUC = p.ask(r, "RUC", req.RUC)
	req.RazonSocial = p.ask(r, "Razón social", req.RazonSocial)
	req.OwnerName = p.ask(r, "Nombre del propietario", req.OwnerName)
	req.Password = p.ask(r, "Contraseña del propietario", req.Password)
	req.CertificatePath = p.ask(r, "Ruta del certificado (.p12/.pfx)", req.CertificatePath)
	// Optional fields.
	req.NombreComercial = p.ask(r, "Nombre comercial (opcional)", req.NombreComercial)
	req.CorreoContacto = p.ask(r, "Correo de contacto (opcional)", req.CorreoContacto)
	return req, nil
}

func (p *BootstrapPrompter) ask(r *bufio.Reader, label, def string) string {
	if def != "" {
		fmt.Fprintf(p.out, "  %s [%s]: ", label, def)
	} else {
		fmt.Fprintf(p.out, "  %s: ", label)
	}
	line, _ := r.ReadString('\n')
	ans := strings.TrimSpace(line)
	if ans == "" {
		return def
	}
	return ans
}
