package main

import (
	"fmt"
	"os"

	"golang.org/x/term"

	"github.com/JJQuispillo/billing/cli/internal/cli"
	"github.com/JJQuispillo/billing/cli/internal/ops"
	"github.com/JJQuispillo/billing/cli/internal/tui"
)

// Build-time variables. These are set by goreleaser via -ldflags.
// They default to dev/local values so `go run` and `go test` work
// without ldflags.
var (
	version = "dev"
	commit  = "none"
	date    = "unknown"
)

// Version returns the build-time version triple for --version / telemetry.
func Version() (string, string, string) { return version, commit, date }

// shouldLaunchTUI is the activation gate for the interactive TUI. It is
// pure (all inputs injected) so the decision table is unit-testable:
//
//   - bare `sriyactl` (no args) AND
//   - stdin is a TTY AND stdout is a TTY AND
//   - SRIYACTL_NO_TUI != "1" (kill-switch)
//
// Any other combination falls through to cobra unchanged — piped/CI
// invocations keep printing the help byte-identically (ai-contract).
func shouldLaunchTUI(args []string, stdinTTY, stdoutTTY bool, getenv func(string) string) bool {
	return len(args) == 1 && stdinTTY && stdoutTTY && getenv("SRIYACTL_NO_TUI") != "1"
}

func main() {
	if shouldLaunchTUI(
		os.Args,
		term.IsTerminal(int(os.Stdin.Fd())),
		term.IsTerminal(int(os.Stdout.Fd())),
		os.Getenv,
	) {
		if err := tui.Run(version, &ops.Options{}); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}
	if err := cli.NewRootCmd(version).Execute(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
