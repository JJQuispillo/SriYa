package cli

import (
	"github.com/JJQuispillo/billing/cli/internal/config"
	"github.com/JJQuispillo/billing/cli/internal/core"
)

// toCfgTenantsStore is a thin identity function (kept as a named helper
// so call sites read clearly). The CLI now uses config.TenantsStore
// directly.
func toCfgTenantsStore(s config.TenantsStore) config.TenantsStore { return s }

// loadRawConfigFor returns the underlying *config.Config so handlers
// (which take *config.Config directly) can read it. We re-load to
// ensure the on-disk state is current.
func loadRawConfigFor(_ *CmdContext) *config.Config {
	c, err := config.Load()
	if err != nil {
		return &config.Config{Contexts: map[string]config.Context{}, Tenants: map[string]map[string]config.Tenant{}}
	}
	return c
}

// ensure core is referenced (handlers live in core).
var _ = core.SchemaVersion
