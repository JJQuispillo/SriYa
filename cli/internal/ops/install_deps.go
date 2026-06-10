package ops

import (
	"context"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/compose"
	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// ResolveInstallDir resolves where `infra install` provisions the
// stack. Unlike the other infra commands, install runs BEFORE a valid
// install dir exists, so it does NOT go through compose.NewExecRunner
// (which requires .env + docker-compose.yml). Precedence mirrors the
// compose resolver's writable target: --dir > SRIYACTL_HOME > $HOME/sriya.
func ResolveInstallDir(opts *Options) (string, error) {
	if opts.Dir != "" {
		return opts.Dir, nil
	}
	if h := os.Getenv(compose.EnvHomeOverride); h != "" {
		return h, nil
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return "", errs.New(errs.CodeUsage, "cannot resolve a default install dir", "pass --dir <path> or set SRIYACTL_HOME=<path>")
	}
	return filepath.Join(home, "sriya"), nil
}

// LocalAPIURL returns the URL of the freshly provisioned local stack for
// the given host port ("" → 8080). The readiness probe and the bootstrap
// client both target this URL.
func LocalAPIURL(port string) string {
	if port == "" {
		port = "8080"
	}
	return fmt.Sprintf("http://localhost:%s", port)
}

// BuildInstallDeps builds the production core.InfraInstallDeps for
// `infra install`. It is shared by the cobra command and the TUI wizard:
//
//   - cobra passes progress=stderr, interactive=installer.IsInteractive and
//     interactor=NewBootstrapPrompter(stderr) so missing bootstrap fields
//     are prompted on a TTY.
//   - the TUI passes its streaming pipe as progress, interactive=func()
//     bool { return false } and interactor=nil because its huh form
//     collects every field BEFORE execution (design D5) — the handler must
//     never re-prompt under bubbletea.
//
// The caller sets HealthTimeout on the returned deps (flag/tui setting).
func BuildInstallDeps(
	opts *Options,
	dir string,
	port string,
	progress io.Writer,
	interactive func() bool,
	interactor core.BootstrapInteractor,
) core.InfraInstallDeps {
	_ = opts // reserved: future per-context wiring (e.g. custom fetcher mirrors)
	_ = dir  // the handler receives the dir via InfraInstallRequest.Dir

	// The readiness probe targets the local stack on the chosen port. We
	// build a throwaway api client pointed at localhost; it is only used
	// to poll /health/ready and to POST the first-tenant bootstrap.
	localClient := api.NewHTTPClient(LocalAPIURL(port), api.NewKeyringDispatch(NewSecretStore()))

	return core.InfraInstallDeps{
		Fetcher:  nil,      // production HTTP fetcher
		Probe:    nil,      // production exec docker probe
		DepCmd:   nil,      // production exec brew/colima runner (--auto-install)
		Progress: progress, // step lines (stderr for cobra, pipe for TUI)
		NewCompose: func(d string) (compose.Runner, error) {
			// The dir now has .env + docker-compose.yml, so the
			// exec runner resolves cleanly against it.
			return compose.NewExecRunner(d)
		},
		Ready: func(ctx context.Context) (api.Health, error) {
			return localClient.Ready(ctx)
		},
		// Bootstrap chaining + context/token seeding.
		BootstrapAPI: localClient,
		Seeder:       NewInstallSeeder(NewSecretStore()),
		Interactive:  interactive,
		Interactor:   interactor,
	}
}
