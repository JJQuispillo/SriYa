package cli

import (
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// CmdContext is the bag of dependencies a cobra command receives. The
// concrete type now lives in internal/ops (ops.Deps) so the TUI consumes
// the exact same wiring; the aliases keep this package churn-free.
type CmdContext = ops.Deps

// ConfigLoader abstracts the config file load.
type ConfigLoader = ops.ConfigLoader

// LoadedConfig carries the parsed config plus a TenantsStore factory.
type LoadedConfig = ops.LoadedConfig

// buildCmdContext is the canonical constructor for CmdContext. It defers
// to ops.BuildDeps — the single wiring path shared with the TUI.
func buildCmdContext(flags *SharedFlags) (*CmdContext, error) {
	return ops.BuildDeps(flags)
}
