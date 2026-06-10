// Package cli — destructive-op confirmation helper (design §#1).
//
// Why a dedicated helper?
//
//   - "AI-contract once": every destructive command MUST pass through the
//     same confirmation gate. The table of decision (TTY × bypass) lives
//     in ops.ConfirmDecision so the cobra prompt and the TUI modal share
//     the exact same semantics; this file only adds the bufio y/N prompt
//     on top of the pure decision.
//   - The gate MUST run BEFORE the handler's side effect but AFTER the
//     readonly / dry-run guards (those short-circuit in the handler).
//     `Confirm` therefore takes the *effective* flags + I/O as
//     arguments so the middleware can decide the right point in the
//     pipeline to invoke it.
//
// Decision table (see ops.ConfirmDecision):
//
//	| env       | --yes/--no-input | action                                |
//	|-----------|------------------|---------------------------------------|
//	| TTY       | no               | prompt [y/N]; n/empty ⇒ abort         |
//	| TTY       | yes              | proceed                               |
//	| non-TTY   | no               | refuse (confirmation_required, exit 2)|
//	| non-TTY   | yes              | proceed                               |
package cli

import (
	"bufio"
	"fmt"
	"io"
	"os"
	"strings"

	"golang.org/x/term"

	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// isTerminalFn is the package-level TTY check. It is a var so tests
// can swap it for a deterministic stub without depending on the
// host's actual TTY state. Production wiring uses the real
// term.IsTerminal.
var isTerminalFn = func(fd int) bool { return term.IsTerminal(fd) }

// Confirm asks the operator to confirm a destructive operation. It is
// invoked by the cli middleware (NOT by the handler) for any command
// whose wiring sets SharedFlags.Mutating = true AND whose
// RequiresConfirm annotation is set.
//
// Arguments:
//
//   - flags: the effective SharedFlags. --yes or --no-input bypass the
//     prompt.
//   - stdin: source for the operator's reply (e.g. the test's
//     strings.Reader or os.Stdin in production).
//   - stdout: where the prompt is written (and any prompt newline).
//   - resourceDesc: human-friendly description of what is about to
//     happen, e.g. "restore dump.sql into the postgres container".
//
// The function does NOT call out to the terminal on its own: TTY
// detection is performed against os.Stdin.Fd() (the standard input
// the operator types into). This matches the agent/CI convention where
// stdin is piped and stdout is captured — i.e. we refuse when the
// operator is NOT sitting at a keyboard.
func Confirm(flags SharedFlags, stdin io.Reader, stdout io.Writer, resourceDesc string) error {
	switch ops.ConfirmDecision(flags, isTerminalFn(int(os.Stdin.Fd()))) {
	case ops.DecisionProceed:
		// Bypass path: explicit non-interactive intent.
		return nil

	case ops.DecisionRefuse:
		// Non-TTY path: refuse. The operator must re-run with an explicit
		// --yes or --no-input to confirm destructive intent. We surface
		// confirmation_required (exit 2, usage) per design §#9.
		return errs.New(
			errs.CodeConfirmRequired,
			"destructive operation requires confirmation",
			"re-run with --yes / --no-input (non-interactive)",
		)
	}

	// Interactive TTY: prompt [y/N]. Accept only y / yes (case
	// insensitive). Anything else (including empty) aborts.
	if stdout != nil {
		fmt.Fprintf(stdout, "About to %s. Continue? [y/N]: ", resourceDesc)
	}
	reader := bufio.NewReader(stdin)
	line, err := reader.ReadString('\n')
	if err != nil && line == "" {
		// EOF on TTY (Ctrl-D). Treat as abort.
		return errs.New(
			errs.CodeConfirmAborted,
			"confirmation aborted (no input received)",
			"re-run and answer y to confirm",
		)
	}
	answer := strings.ToLower(strings.TrimSpace(line))
	if answer == "y" || answer == "yes" {
		return nil
	}
	return errs.New(
		errs.CodeConfirmAborted,
		"confirmation aborted",
		"re-run and answer y to confirm the destructive operation",
	)
}
