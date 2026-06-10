package core

import (
	"context"
	"strings"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// -----------------------------------------------------------------------------
// Fase 5 — install → bootstrap chaining + context/token seeding (F5).
//
// After the stack is healthy, `infra install` (unless --no-bootstrap) seeds a
// local context + the service token derived from the freshly rendered .env,
// then chains the first-tenant bootstrap. All external effects are behind the
// seams below so the handler is fully unit-testable with fakes (no real
// keychain, no real config file, no real HTTP).
// -----------------------------------------------------------------------------

// BootstrapClient is the narrow slice of api.Client the install→bootstrap
// chain needs. api.Client satisfies it; tests pass a fake.
type BootstrapClient interface {
	BootstrapTenant(ctx context.Context, req api.BootstrapRequest) (api.BootstrapResponse, error)
}

// ContextSeeder resolves the second chicken-and-egg (F5): it writes a local
// context (the host URL of the just-provisioned stack) into the CLI config and
// stores the service token so the chained bootstrap — and any later
// `tenant create` — authenticate without the operator re-supplying creds.
//
// Seed MUST be idempotent (a re-run of `infra install` re-seeds the same
// context/token without error). It returns the resolved context name and the
// fallback mode used (see SeedResult).
type ContextSeeder interface {
	Seed(ctx context.Context, in SeedInput) (SeedResult, error)
}

// SeedInput is the data the seeder needs.
type SeedInput struct {
	// ContextName is the alias to register (default "local").
	ContextName string
	// URL is the base URL of the freshly provisioned stack
	// (e.g. http://localhost:8080).
	URL string
	// ServiceToken is the SERVICE_AUTH_TOKEN read back from the rendered .env.
	ServiceToken string
}

// SeedResult reports how the token was persisted.
type SeedResult struct {
	// ContextName is the context that was registered.
	ContextName string
	// TokenFallbackEnv is true when the keychain was unavailable and the
	// operator must export SRIYACTL_SERVICE_TOKEN instead. The flow does NOT
	// abort in that case (per spec) — it reports the fallback mode.
	TokenFallbackEnv bool
}

// BootstrapInteractor collects the first-tenant inputs interactively. On a TTY
// the install flow uses it to fill in whatever the operator did not pass as a
// flag. It receives the partially-populated request (from flags) and returns
// the completed one. Implementations MUST NOT prompt when stdin is not a TTY —
// the handler only calls Prompt when Interactive() is true.
type BootstrapInteractor interface {
	Prompt(in api.BootstrapRequest) (api.BootstrapRequest, error)
}

// chainBootstrap runs the seed → (prompt) → POST /api/v1/bootstrap sequence
// after the stack is healthy. It is only called when in.NoBootstrap is false.
// It mutates `out` in place (TenantID/APIKey/NextStep) and returns an error on
// failure. The stack is left running on any bootstrap failure (the caller has
// already provisioned it) — we never tear it down here.
func chainBootstrap(ctx context.Context, d InfraInstallDeps, in InfraInstallRequest, out *InfraInstallResult) error {
	// The bootstrap chain requires the API client + seeder. If the cli layer
	// did not wire them (e.g. a unit test of the provision-only path), treat
	// it as a wiring bug rather than silently skipping.
	if d.BootstrapAPI == nil || d.Seeder == nil {
		return errs.New(
			errs.CodeGeneric,
			"bootstrap requested but the install handler is missing its bootstrap wiring",
			"this is a wiring bug; report it (or pass --no-bootstrap)",
		)
	}

	// Read the service token back from the freshly rendered .env so it can be
	// seeded into the keychain (the secret never transits a flag).
	token, _ := readEnvVar(in.Dir, "SERVICE_AUTH_TOKEN")

	ctxName := in.ContextName
	if ctxName == "" {
		ctxName = "local"
	}
	seed, serr := d.Seeder.Seed(ctx, SeedInput{
		ContextName:  ctxName,
		URL:          in.LocalURL,
		ServiceToken: token,
	})
	if serr != nil {
		return serr
	}

	// Build the bootstrap request from flags, then fill gaps interactively on
	// a TTY. Headless (non-TTY) with a missing required field is a usage
	// error (bootstrap_input_required) — we never hang waiting for input.
	req := in.Boot
	interactive := d.Interactive != nil && d.Interactive()
	if interactive && d.Interactor != nil {
		filled, perr := d.Interactor.Prompt(req)
		if perr != nil {
			return perr
		}
		req = filled
	}
	if err := validateBootstrapInput(req, interactive); err != nil {
		return err
	}

	resp, berr := d.BootstrapAPI.BootstrapTenant(ctx, req)
	if berr != nil {
		// Surface the backend code verbatim (tenant_duplicate,
		// bootstrap_bad_request, auth_invalid, …) — the stack stays up.
		return berr
	}

	out.TenantID = resp.TenantID
	out.APIKey = resp.APIKey
	fallbackNote := ""
	if seed.TokenFallbackEnv {
		fallbackNote = " (service token stored via SRIYACTL_SERVICE_TOKEN fallback; no OS keychain available)"
	}
	out.NextStep = "tenant bootstrapped (id " + resp.TenantID + ") in context " + seed.ContextName +
		". The apiKey is shown ONCE — store it now." + fallbackNote
	return nil
}

// validateBootstrapInput enforces the required fields. When headless
// (non-interactive), a missing required field is bootstrap_input_required so
// the operator gets a deterministic, non-hanging failure. When interactive the
// prompt should have filled the values; a still-missing field is a normal
// usage error from the api layer's Validate (reused here for parity).
func validateBootstrapInput(req api.BootstrapRequest, interactive bool) error {
	missing := []string{}
	if strings.TrimSpace(req.RUC) == "" {
		missing = append(missing, "--ruc")
	}
	if strings.TrimSpace(req.RazonSocial) == "" {
		missing = append(missing, "--razon-social")
	}
	if strings.TrimSpace(req.OwnerName) == "" {
		missing = append(missing, "--owner-name")
	}
	if strings.TrimSpace(req.Password) == "" {
		missing = append(missing, "--password")
	}
	if strings.TrimSpace(req.CertificatePath) == "" {
		missing = append(missing, "--cert")
	}
	if len(missing) == 0 {
		return nil
	}
	if !interactive {
		return errs.New(
			errs.CodeBootstrapInputRequired,
			"missing required bootstrap input(s): "+strings.Join(missing, ", "),
			"run on a TTY to be prompted, pass the flags, or use --no-bootstrap to skip",
		)
	}
	// Interactive but still missing (the operator left a required prompt
	// blank). Same code so the contract is consistent.
	return errs.New(
		errs.CodeBootstrapInputRequired,
		"missing required bootstrap input(s): "+strings.Join(missing, ", "),
		"re-run and provide all required values, or use --no-bootstrap to skip",
	)
}
