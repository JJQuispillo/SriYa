package ops

import (
	"context"
	"io"
	"os"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/compose"
	"github.com/JJQuispillo/billing/cli/internal/config"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/secret"
)

// Deps is the bag of dependencies a frontend command receives. It
// includes everything the handlers need (api client, secrets, config,
// compose) plus the resolved effective context name. The cobra layer
// aliases it as CmdContext; the TUI consumes it directly.
type Deps struct {
	Ctx         context.Context
	Stdout      io.Writer
	Stderr      io.Writer
	Flags       *Options
	API         api.Client
	Compose     compose.Runner
	TenantStore config.TenantsStore
	Secret      secret.Store
	Loader      ConfigLoader
	// Resolved context for the command (after --context / SRIYACTL_CONTEXT / config).
	ContextName string
}

// ConfigLoader abstracts the config file load.
type ConfigLoader interface {
	Load() (*LoadedConfig, error)
}

// LoadedConfig carries the parsed config plus a TenantsStore factory.
type LoadedConfig struct {
	CurrentContext string
	CurrentTenant  string
	Tenants        config.TenantsStore
}

// BuildDeps is the canonical constructor for Deps. It loads the config,
// resolves the active context, and instantiates the api client and the
// compose runner. It is the single wiring path shared by cobra commands
// (via cli.buildCmdContext) and the TUI.
func BuildDeps(flags *Options) (*Deps, error) {
	loader := &fsConfigLoader{}
	cfg, err := loader.Load()
	if err != nil {
		return nil, err
	}
	ctxName, err := resolveActiveContext(flags, cfg)
	if err != nil {
		return nil, err
	}
	apiClient, err := newAPIClient(cfg, ctxName)
	if err != nil {
		return nil, err
	}
	var comp compose.Runner
	exec, err := compose.NewExecRunner(flags.Dir)
	if err != nil {
		// Compose is best-effort for non-infra commands; defer the
		// failure to the command that needs it. We return a runner
		// that fails on use with a stable code.
		comp = &lazyComposeRunner{err: err}
	} else {
		comp = exec
	}
	return &Deps{
		Ctx:         context.Background(),
		Stdout:      os.Stdout,
		Stderr:      os.Stderr,
		Flags:       flags,
		API:         apiClient,
		Compose:     comp,
		TenantStore: cfg.Tenants,
		Secret:      NewSecretStore(),
		Loader:      loader,
		ContextName: ctxName,
	}, nil
}

// LoadRawConfig returns the underlying *config.Config so handlers that
// take *config.Config directly (e.g. core.TenantDeps.Config) can read
// it. We re-load to ensure the on-disk state is current; on failure an
// empty (but non-nil) config is returned so read paths degrade
// gracefully instead of panicking.
func LoadRawConfig() *config.Config {
	c, err := config.Load()
	if err != nil {
		return &config.Config{Contexts: map[string]config.Context{}, Tenants: map[string]map[string]config.Tenant{}}
	}
	return c
}

// resolveActiveContext returns the context name to use for the
// command, respecting --context / SRIYACTL_CONTEXT / config.
func resolveActiveContext(flags *Options, cfg *LoadedConfig) (string, error) {
	// Tenant flag does not select context; ignore here. v2 may add
	// a separate --context flag.
	_ = flags
	if cfg == nil || cfg.CurrentContext == "" {
		return "", errs.New(
			errs.CodeConfigInvalid,
			"no current_context in config",
			"run `sriyactl context use <name>` after editing the config",
		)
	}
	return cfg.CurrentContext, nil
}

// fsConfigLoader loads the real on-disk config. It is the production
// ConfigLoader; tests can substitute a fake.
type fsConfigLoader struct{}

func (f *fsConfigLoader) Load() (*LoadedConfig, error) {
	c, err := config.Load()
	if err != nil {
		return nil, errs.Wrap(errs.CodeConfigInvalid, err, "load config", "check ~/.config/sriyactl/config.toml")
	}
	return &LoadedConfig{
		CurrentContext: c.CurrentContext,
		CurrentTenant:  c.CurrentTenant,
		Tenants:        config.NewTenantsStore(c),
	}, nil
}

// newAPIClient builds an HTTPClient for the resolved context, using the
// keychain-backed auth dispatcher.
func newAPIClient(cfg *LoadedConfig, ctxName string) (api.Client, error) {
	c, err := config.Load()
	if err != nil {
		return nil, err
	}
	cc, ok := c.Contexts[ctxName]
	if !ok {
		return nil, errs.New(
			errs.CodeConfigInvalid,
			"context not found: "+ctxName,
			"add it under [contexts."+ctxName+"] in config.toml",
		)
	}
	if cc.URL == "" {
		return nil, errs.New(
			errs.CodeConfigInvalid,
			"context "+ctxName+" has no url",
			"set [contexts."+ctxName+"].url in config.toml",
		)
	}
	auth := api.NewKeyringDispatch(NewSecretStore())
	return api.NewHTTPClient(cc.URL, auth), nil
}

// lazyComposeRunner returns the resolved error on every call. Used
// when the install dir cannot be detected but the command does not
// need compose (e.g. tenant create).
type lazyComposeRunner struct{ err error }

func (l *lazyComposeRunner) Run(ctx context.Context, args ...string) (compose.Result, error) {
	return compose.Result{}, l.err
}
func (l *lazyComposeRunner) Stream(ctx context.Context, w io.Writer, args ...string) error {
	return l.err
}
func (l *lazyComposeRunner) RunTo(ctx context.Context, w io.Writer, args ...string) error {
	return l.err
}
func (l *lazyComposeRunner) InstallDir() string        { return "" }
func (l *lazyComposeRunner) ValidateInstallDir() error { return l.err }
