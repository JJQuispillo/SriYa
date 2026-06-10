package core

import (
	"context"
	"errors"
	"fmt"
	"io"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/compose"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/installer"
)

// InfraInstallRequest is the input to InfraInstallHandler. The non-secret
// values flow straight into the rendered .env via installer.EnvConfig;
// secrets are NEVER passed here (they are generated inside RenderEnv).
type InfraInstallRequest struct {
	// Version is the pinned image tag + compose ref ("" / "latest" → main).
	Version string
	// Port is the host API port (BILLING_API_PORT).
	Port string
	// CorsOrigin is the allowed frontend origin (CORS_ORIGIN_0).
	CorsOrigin string
	// DBUser is the owner/bootstrap DB role (BILLING_DB_USER).
	DBUser string
	// Dir is the install dir to provision. Required.
	Dir string

	// AutoInstall requests `--auto-install` (brew/colima on macOS). The
	// flag is parsed and threaded here, but the actual auto-install flow
	// is Fase 4 (T-INST-030) and is NOT implemented in this handler — it
	// is a clean seam: when docker is unavailable the handler fails with
	// docker_unavailable and a hint to install docker or pass
	// --auto-install once that lands.
	AutoInstall bool
	// NoBootstrap skips the first-tenant bootstrap (Fase 5). When false (the
	// default) and the stack becomes healthy, the handler seeds a local
	// context + service token and chains POST /api/v1/bootstrap.
	NoBootstrap bool
	// Boot is the bootstrap request POSTed to /api/v1/bootstrap. On a TTY any
	// gaps are filled interactively; headless it must be complete.
	Boot api.BootstrapRequest

	// ContextName is the local context alias to seed in config (default
	// "local").
	ContextName string
	// LocalURL is the base URL of the freshly provisioned stack
	// (e.g. http://localhost:8080); seeded into the local context.
	LocalURL string
}

// InfraInstallResult is the success payload of `infra install`.
//
// TenantID / APIKey are reserved for the Fase 5 bootstrap chaining and are
// json:"-" so a secret API key never lands in structured output by
// accident. NextStep tells the operator (or an agent) what to run next.
type InfraInstallResult struct {
	InstallDir     string `json:"installDir"     yaml:"installDir"`
	ImageTag       string `json:"imageTag"       yaml:"imageTag"`
	EnvCreated     bool   `json:"envCreated"     yaml:"envCreated"`
	ComposeCreated bool   `json:"composeCreated" yaml:"composeCreated"`
	Healthy        bool   `json:"healthy"        yaml:"healthy"`
	NextStep       string `json:"nextStep"       yaml:"nextStep"`

	// Reserved for Fase 5 bootstrap chaining; never serialized.
	TenantID string `json:"-" yaml:"-"`
	APIKey   string `json:"-" yaml:"-"`
}

// Columns implements render.Renderable so `infra install` gets a clean
// table instead of the reflection fallback. The secret-bearing TenantID /
// APIKey fields are intentionally excluded.
func (r InfraInstallResult) Columns() []string {
	return []string{"installDir", "imageTag", "envCreated", "composeCreated", "healthy", "nextStep"}
}

// Rows implements render.Renderable.
func (r InfraInstallResult) Rows() [][]string {
	return [][]string{{
		r.InstallDir,
		r.ImageTag,
		fmt.Sprintf("%t", r.EnvCreated),
		fmt.Sprintf("%t", r.ComposeCreated),
		fmt.Sprintf("%t", r.Healthy),
		r.NextStep,
	}}
}

// ComposeRunnerFactory builds a compose.Runner bound to the freshly
// provisioned install dir. It is a seam because `infra install` creates
// the dir at runtime — the runner cannot be resolved up front by
// buildCmdContext (which requires an already-valid install dir). The
// production factory is compose.NewExecRunner; tests inject a fake.
type ComposeRunnerFactory func(dir string) (compose.Runner, error)

// ReadyProbe polls the backend readiness endpoint. It is the seam the
// health-wait loop drives; production wiring passes a function backed by
// api.Client.Ready against the freshly installed stack, tests pass a fake.
type ReadyProbe func(ctx context.Context) (api.Health, error)

// InfraInstallDeps bundles the install handler's injectable dependencies.
// Every external effect (network download, docker probe, compose
// lifecycle, health polling) is behind one of these seams so the handler
// is fully unit-testable with fakes and never touches the real network /
// daemon / filesystem outside the temp install dir under test.
type InfraInstallDeps struct {
	// Fetcher downloads the pinned compose file. nil → production HTTP
	// fetcher.
	Fetcher installer.Fetcher
	// Probe is the docker binary/daemon probe for the pre-install doctor.
	// nil → production exec probe.
	Probe installer.DockerProbe
	// DepCmd runs brew/colima for the `--auto-install` flow (Fase 4). nil →
	// production exec runner. Only consulted when the request sets
	// AutoInstall and docker is missing.
	DepCmd installer.DepCommand
	// Progress receives human-readable auto-install step lines (wired to
	// Stderr by the cli layer). nil → discarded.
	Progress io.Writer
	// NewCompose builds the compose runner for the provisioned dir.
	// Required.
	NewCompose ComposeRunnerFactory
	// Ready polls /health/ready after `up -d`. Required.
	Ready ReadyProbe

	// BootstrapAPI POSTs the first-tenant bootstrap (Fase 5). Required only
	// when NoBootstrap is false; the cli wires an api.Client pointed at the
	// local stack with the seeded service token.
	BootstrapAPI BootstrapClient
	// Seeder registers the local context + service token (Fase 5, F5).
	// Required only when NoBootstrap is false.
	Seeder ContextSeeder
	// Interactive reports whether stdin is a TTY (default false → headless).
	// When true and Interactor is set, the bootstrap inputs are prompted.
	Interactive func() bool
	// Interactor collects bootstrap inputs interactively (TTY only).
	Interactor BootstrapInteractor

	// HealthTimeout caps the health-wait. Default 90s.
	HealthTimeout time.Duration
	// HealthPollInterval is the gap between readiness probes. Default 3s.
	HealthPollInterval time.Duration
}

// installHealthPollIntervalDefault / installHealthTimeoutDefault are the
// defaults applied when InfraInstallDeps leaves them zero. 90s matches the
// spec health-wait budget.
const (
	installHealthTimeoutDefault      = 90 * time.Second
	installHealthPollIntervalDefault = 3 * time.Second
)

// InfraInstallHandler is the day-1 entry point. It orchestrates, in order:
//
//  1. PRE-install doctor (docker binary + daemon) — runs BEFORE the
//     install dir exists, so it must NOT touch compose/.env (design §#5,
//     F6). On failure it returns docker_unavailable.
//  2. RenderEnv — generates charset-safe secrets and writes .env
//     (no-clobber, chmod 600).
//  3. DownloadCompose — fetches the pinned compose for v<version>
//     (no-clobber).
//  4. compose pull && up -d via the freshly built runner.
//  5. health-wait: poll /health/ready up to ~90s.
//  6. return a typed result with a NextStep hint. Bootstrap chaining and
//     --auto-install are clean seams left for Fase 5 / Fase 4.
//
// Idempotency (T-INST-024): RenderEnv and DownloadCompose are both
// no-clobber, so a re-run never rotates secrets or clobbers an edited
// compose; `compose up -d` is itself idempotent. A re-run of a healthy
// stack reports EnvCreated=false, ComposeCreated=false, Healthy=true.
func InfraInstallHandler(d InfraInstallDeps) Handler[InfraInstallRequest, InfraInstallResult] {
	return func(ctx context.Context, in InfraInstallRequest) (Output[InfraInstallResult], error) {
		if in.Dir == "" {
			return Output[InfraInstallResult]{}, errs.New(errs.CodeUsage, "missing install dir", "pass --dir <path> or set SRIYACTL_HOME")
		}
		if d.NewCompose == nil || d.Ready == nil {
			return Output[InfraInstallResult]{}, errs.New(errs.CodeGeneric, "install handler misconfigured (missing compose factory or ready probe)", "this is a wiring bug; report it")
		}

		// Step 1: PRE-install doctor (docker binary + daemon only). This
		// runs before the dir exists, so it goes through the installer
		// probe seam, not compose. When docker is unavailable and
		// AutoInstall was requested (Fase 4, T-INST-030), EnsureDocker
		// attempts `brew install colima docker docker-compose` + `colima
		// start` on macOS (guide-only on Linux). The default (no flag) is
		// detect + guide and returns docker_unavailable on a miss.
		if derr := installer.EnsureDocker(ctx, in.AutoInstall, installer.AutoInstallDeps{
			Cmd:      d.DepCmd,
			Probe:    d.Probe,
			Progress: d.Progress,
		}); derr != nil {
			return Output[InfraInstallResult]{}, derr
		}

		out := InfraInstallResult{InstallDir: in.Dir}

		// Step 2: render .env (no-clobber, charset-safe secrets).
		envCfg := installer.EnvConfig{
			Version:    in.Version,
			Port:       in.Port,
			CorsOrigin: in.CorsOrigin,
			DBUser:     in.DBUser,
		}
		envCreated, err := installer.RenderEnv(in.Dir, envCfg)
		if err != nil {
			return Output[InfraInstallResult]{}, err
		}
		out.EnvCreated = envCreated

		// Resolve the effective image tag for the result (read back from
		// .env so a re-run reports the EXISTING tag, not the requested one).
		if tag, _ := readEnvVar(in.Dir, "BILLING_IMAGE_TAG"); tag != "" {
			out.ImageTag = tag
		} else {
			out.ImageTag = in.Version
		}

		// Step 3: download the pinned compose (no-clobber).
		fetcher := d.Fetcher
		if fetcher == nil {
			fetcher = installer.NewHTTPFetcher()
		}
		composeCreated, err := installer.DownloadCompose(in.Dir, in.Version, fetcher)
		if err != nil {
			return Output[InfraInstallResult]{}, err
		}
		out.ComposeCreated = composeCreated

		// Step 4: compose pull && up -d via a runner bound to the new dir.
		runner, err := d.NewCompose(in.Dir)
		if err != nil {
			return Output[InfraInstallResult]{}, err
		}
		if _, perr := runner.Run(ctx, "pull"); perr != nil {
			return Output[InfraInstallResult]{}, errs.Wrap(errs.CodeGeneric, perr, "compose pull failed", "inspect the compose output and retry")
		}
		if _, uerr := runner.Run(ctx, "up", "-d"); uerr != nil {
			return Output[InfraInstallResult]{}, errs.Wrap(errs.CodeGeneric, uerr, "compose up -d failed", "inspect the compose output and retry")
		}

		// Step 5: health-wait. Poll /health/ready up to the timeout.
		timeout := d.HealthTimeout
		if timeout == 0 {
			timeout = installHealthTimeoutDefault
		}
		interval := d.HealthPollInterval
		if interval == 0 {
			interval = installHealthPollIntervalDefault
		}
		healthy, herr := waitReady(ctx, d.Ready, timeout, interval)
		out.Healthy = healthy
		if herr != nil {
			out.NextStep = "stack started but never became ready; check `sriyactl infra logs` and `sriyactl infra doctor`"
			return NewOutput("InfraInstall", out), errs.New(
				errs.CodeInstallHealthTimeout,
				fmt.Sprintf("the stack never became ready within %s", timeout),
				"check `sriyactl infra logs`; once ready, run `sriyactl tenant bootstrap` to onboard the first tenant",
			).MarkRenderable()
		}

		// Step 6: success. Chain the first-tenant bootstrap (Fase 5) unless
		// --no-bootstrap was passed. On --no-bootstrap, leave the stack
		// provisioned and point the operator at the next command.
		if in.NoBootstrap {
			out.NextStep = "stack is healthy; bootstrap skipped (--no-bootstrap). Run `sriyactl tenant create` when ready."
			return NewOutput("InfraInstall", out), nil
		}

		// Seed local context + service token, then POST /api/v1/bootstrap.
		// On failure the stack stays up; we surface the backend code with the
		// payload rendered (Renderable) so the operator sees the install
		// result alongside the error.
		if berr := chainBootstrap(ctx, d, in, &out); berr != nil {
			if out.NextStep == "" {
				out.NextStep = "stack is healthy but bootstrap failed; fix the issue and run `sriyactl tenant create`"
			}
			var ce *errs.CLIError
			if errors.As(berr, &ce) {
				return NewOutput("InfraInstall", out), ce.MarkRenderable()
			}
			return NewOutput("InfraInstall", out), berr
		}
		return NewOutput("InfraInstall", out), nil
	}
}

// waitReady polls ready until it returns Status=="Ready" or the timeout
// elapses. It returns (true, nil) on success and (false, err) on timeout.
// The first probe fires immediately so a stack that is already healthy
// (idempotent re-run) returns without waiting a full interval.
func waitReady(ctx context.Context, ready ReadyProbe, timeout, interval time.Duration) (bool, error) {
	waitCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	for {
		r, err := ready(waitCtx)
		if err == nil && r.Status == "Ready" {
			return true, nil
		}
		select {
		case <-waitCtx.Done():
			return false, waitCtx.Err()
		case <-time.After(interval):
		}
	}
}
