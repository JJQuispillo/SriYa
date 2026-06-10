package cli

import (
	"github.com/spf13/cobra"
)

// NewRootCmd builds the cobra root command. Subcommands are registered by
// AddCommands (called from main.go) so that this file stays free of
// command-specific wiring.
//
// version is the build-time version string injected by goreleaser via
// `-X main.version=...` and threaded through from main.main(). Setting
// cmd.Version makes cobra auto-add the `--version` flag and a version
// template, so `sriyactl --version` prints it and exits 0 (relied on by
// the goreleaser brew `test:` block).
func NewRootCmd(version string) *cobra.Command {
	flags := &SharedFlags{}
	cmd := &cobra.Command{
		Use:           "sriyactl",
		Short:         "Day-2 ops CLI for the SriYa/Qora self-hosted stack",
		Long:          "sriyactl wraps docker compose and the SriYa HTTP API to provide auditable, AI-friendly day-2 operations: infra status/upgrade/backup, tenant onboarding, certificate watch, and more.",
		Version:       version,
		SilenceUsage:  true,
		SilenceErrors: true,
		// PersistentPreRunE is a great place to validate cross-cutting
		// flag combinations (e.g. --dry-run + --yes on a destructive
		// command) BEFORE the command runs. For v1 we keep it simple.
		PersistentPreRunE: func(cmd *cobra.Command, _ []string) error {
			return nil
		},
	}
	pflags := cmd.PersistentFlags()
	pflags.StringVar(&flags.Output, "output", "", "output format: table|json|yaml (auto: tty=table, no-tty=json)")
	pflags.StringVar(&flags.Dir, "dir", "", "install dir (default: SRIYACTL_HOME or auto-detect)")
	pflags.StringVar(&flags.Tenant, "tenant", "", "tenant alias override (does not persist)")
	pflags.BoolVar(&flags.Yes, "yes", false, "auto-confirm destructive actions")
	pflags.BoolVar(&flags.NoInput, "no-input", false, "alias of --yes (CI/agent-friendly)")
	pflags.BoolVar(&flags.DryRun, "dry-run", false, "plan and print the action without executing it")
	pflags.BoolVar(&flags.ReadOnly, "readonly", false, "force read-only mode for this invocation (env: SRIYACTL_READONLY=1)")

	// Stash the flags struct on the command so subcommand constructors can
	// retrieve it via FlagsFromCmd. Avoids the need for a package-global.
	cmd.SetContext(cmd.Context())
	setFlags(cmd, flags)
	AddCommands(cmd, flags)
	return cmd
}

// flagsKey is the unexported context key under which we stash the
// SharedFlags. Subcommands retrieve the value via FlagsFromCmd.
type flagsKey struct{}

// setFlags stores the SharedFlags pointer on the command. We use a
// dedicated field rather than context because the flags are needed by
// every subcommand constructor.
func setFlags(cmd *cobra.Command, f *SharedFlags) {
	cmd.Annotations = map[string]string{}
	// Store as a cobra annotation-like side table via context.
	type ann struct{ f *SharedFlags }
	_ = ann{f}
	// Real storage: we use a known package-level map keyed by *cobra.Command.
	flagRegistry[cmd] = f
}

func FlagsFromCmd(cmd *cobra.Command) *SharedFlags {
	if f, ok := flagRegistry[cmd]; ok {
		return f
	}
	// Fallback: walk parents to find a registered root.
	cur := cmd
	for cur != nil {
		if f, ok := flagRegistry[cur]; ok {
			return f
		}
		cur = cur.Parent()
	}
	return &SharedFlags{}
}

// flagRegistry is the unexported map that lets subcommands find the
// SharedFlags without a global variable. In a v2 refactor we can swap this
// for cobra's Args/Context pattern.
var flagRegistry = map[*cobra.Command]*SharedFlags{}

// AddCommands is implemented in commands.go; we keep root.go minimal.
func AddCommands(cmd *cobra.Command, flags *SharedFlags) {
	cmd.AddCommand(
		newInfraCmd(flags),
		newTenantCmd(flags),
		newCertCmd(flags),
		newUICmd(flags),
	)
}
