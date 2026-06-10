package cli

import (
	"os"

	"github.com/spf13/cobra"

	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/tui"
)

// tuiRun is the seam tests use to fake the TUI launch (the real one
// would grab the terminal).
var tuiRun = tui.Run

// newUICmd wires `sriyactl ui`: an explicit launcher for the interactive
// TUI. Unlike the bare-invocation gate in main.go (which silently falls
// back to help), `ui` states intent — so a missing TTY or the
// SRIYACTL_NO_TUI kill-switch are ERRORS (exit ≠ 0), never a silent
// fallback. Persistent flags (--dir, --tenant, --readonly, --dry-run)
// propagate into the TUI session via the shared ops.Options.
func newUICmd(flags *SharedFlags) *cobra.Command {
	return &cobra.Command{
		Use:   "ui",
		Short: "Launch the interactive TUI (requires a TTY)",
		Long: "Opens the full-screen interactive interface (dashboard, install wizard, tenants, logs). " +
			"Requires stdin and stdout to be a terminal; honors SRIYACTL_NO_TUI=1 as a kill-switch.",
		RunE: func(cmd *cobra.Command, _ []string) error {
			if os.Getenv("SRIYACTL_NO_TUI") == "1" {
				return errs.New(
					errs.CodeUsage,
					"TUI disabled by SRIYACTL_NO_TUI=1",
					"unset SRIYACTL_NO_TUI to launch `sriyactl ui`",
				)
			}
			if !isTerminalFn(int(os.Stdin.Fd())) || !isTerminalFn(int(os.Stdout.Fd())) {
				return errs.New(
					errs.CodeUsage,
					"the TUI requires an interactive terminal (stdin and stdout must be a TTY)",
					"run sriyactl from a terminal, or use the headless subcommands (e.g. `sriyactl infra status`)",
				)
			}
			return tuiRun(cmd.Root().Version, flags)
		},
	}
}
