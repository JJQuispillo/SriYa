package cli

import (
	"github.com/spf13/cobra"

	"github.com/JJQuispillo/billing/cli/internal/core"
)

// newCertCmd wires the cert subcommand. v1 ships only `cert status`;
// `cert upload` is deferred to v2.
func newCertCmd(flags *SharedFlags) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "cert",
		Short: "Manage SRI certificate lifecycle (v1: status only)",
		Long:  "Currently only `cert status` is implemented (expiration watch). cert upload is deferred to v2.",
	}
	cmd.AddCommand(newCertStatusCmd(flags))
	return cmd
}

func newCertStatusCmd(flags *SharedFlags) *cobra.Command {
	var (
		tenantAlias string
		warnDays    int
	)
	cmd := &cobra.Command{
		Use:   "status",
		Short: "Report certificate expiration state (valid | expiring | expired)",
		Long:  "Reads the certificates for the active tenant (or --tenant override) and reports status. Exits non-zero when any cert is expiring within --warn-days or already expired (CI signal).",
		RunE: func(cmd *cobra.Command, _ []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			deps := core.CertDeps{
				API:                 cc.API,
				Store:               toCfgTenantsStore(cc.TenantStore),
				Config:              loadRawConfigFor(cc),
				ContextName:         cc.ContextName,
				TenantAliasOverride: cc.Flags.Tenant,
			}
			handler := core.CertStatusHandler(deps)
			exit := RunHandler(*flags, cc.Stdout, cc.Stderr, handler, core.CertStatusRequest{
				TenantAlias: tenantAlias,
				WarnDays:    warnDays,
			})
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
	f := cmd.Flags()
	f.StringVar(&tenantAlias, "tenant", "", "tenant alias override (default: active tenant)")
	f.IntVar(&warnDays, "warn-days", 30, "days before expiration to mark as 'expiring'")
	return cmd
}
