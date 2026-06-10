// Package cli is the thin cobra layer. Every command in this package is
// expected to be a small function that:
//  1. Parses flags (cobra handles this).
//  2. Calls a core.Handler with a typed request.
//  3. Pipes the resulting core.Output through render.Render.
//  4. Maps any error to the correct exit code via errs.ExitCode.
//
// Commands MUST NOT contain business logic and MUST NOT print directly.
package cli

import (
	"errors"
	"fmt"
	"io"
	"os"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/ops"
	"github.com/JJQuispillo/billing/cli/internal/render"
)

// SharedFlags carries the values of the persistent flags set on the cobra
// root command. The struct itself (with BuildContext / ResolveFormat) now
// lives in internal/ops as ops.Options so the TUI shares the exact same
// wiring; the alias keeps the cobra layer churn-free.
type SharedFlags = ops.Options

// RunHandler is the common pipeline invoked by every cobra command. It
// guarantees: error → JSON envelope, success → envelope in the resolved
// format, exit code derived from the error. This is the SINGLE place
// where the render layer is invoked, so ai-contract requirements are met
// exactly once.
//
// Pipeline (sriyactl-v1-fixes, design §#1 + §#4):
//
//  1. BuildContext decorates ctx with readonly / dry-run / mutable.
//  2. If the command is destructive (Mutating && RequiresConfirm) AND
//     not a dry-run, run the Confirm gate against os.Stdin / stdout.
//     A refused gate short-circuits with the gate's exit code (no
//     side effect).
//  3. Invoke the handler.
//  4. On error: if the error is Renderable (sentinel that wants the
//     payload to be preserved alongside the signal), render the
//     handler's `out` to stdout FIRST, then render the error envelope
//     to stderr. Exit code is the error's mapped code.
//  5. On success: render the envelope to stdout. Exit 0.
//
// The render-then-error ordering for renderable errors is what makes
// `cert status` and `infra status` keep their table/JSON payload when
// they signal a non-fatal warning (expiring/expired/degraded).
func RunHandler[In any, Out any](
	flags SharedFlags,
	stdout io.Writer,
	stderr io.Writer,
	handler core.Handler[In, Out],
	in In,
) int {
	ctx := flags.BuildContext()
	format := flags.ResolveFormat()

	// 2a. Readonly gate. The readonly guard MUST be evaluated BEFORE the
	//     confirmation gate (design §#1 / tasks 1.1): a mutating command
	//     run under SRIYACTL_READONLY=1 must fail fast with
	//     readonly_blocked (exit 7) and never prompt — even on a non-TTY
	//     without --yes (which would otherwise yield confirmation_required,
	//     exit 2). We reuse the SINGLE guard (core.GuardMutation) rather
	//     than re-deriving the readonly logic here; BuildContext has
	//     already marked ctx mutable when flags.Mutating is true, so the
	//     guard fires exactly for mutating + readonly contexts.
	if flags.Mutating {
		if gerr := core.GuardMutation(ctx); gerr != nil {
			_ = render.RenderError(stderr, gerr, format)
			return errs.ExitCode(gerr)
		}
	}

	// 2b. Confirm gate for destructive ops. Skipped under --dry-run
	//    (no side effect → no confirm needed per design §#1).
	if flags.Mutating && flags.RequiresConfirm && !flags.DryRun {
		if cerr := Confirm(flags, os.Stdin, stdout, flags.ConfirmDescription); cerr != nil {
			_ = render.RenderError(stderr, cerr, format)
			return errs.ExitCode(cerr)
		}
	}

	out, err := handler(ctx, in)
	if err != nil {
		// Renderable sentinels want the payload to be preserved
		// alongside the error. We render `out` to stdout FIRST
		// (so the table/JSON the operator was waiting for is
		// emitted), then render the error envelope to stderr.
		var r errs.Renderable
		if errors.As(err, &r) && r.Renderable() {
			_ = render.Render(stdout, out, format)
		}
		_ = render.RenderError(stderr, err, format)
		return errs.ExitCode(err)
	}
	if rerr := render.Render(stdout, out, format); rerr != nil {
		fmt.Fprintf(stderr, "render error: %v\n", rerr)
		return errs.ExitCode(rerr)
	}
	return 0
}

// runInstallHandler is RunHandler specialized for `infra install`. It is
// identical to RunHandler EXCEPT it prints the one-time bootstrap apiKey to
// STDERR after a successful render. The apiKey is json:"-" on
// InfraInstallResult so it never lands in the structured stdout payload (no
// secret leak), but the operator must see it exactly once — stderr is the
// right channel (keeps stdout machine-parseable). On a Renderable error
// (e.g. install_health_timeout, bootstrap_input_required) the payload is
// still rendered to stdout, matching RunHandler's contract.
func runInstallHandler(
	flags SharedFlags,
	stdout io.Writer,
	stderr io.Writer,
	handler core.Handler[core.InfraInstallRequest, core.InfraInstallResult],
	in core.InfraInstallRequest,
) int {
	ctx := flags.BuildContext()
	format := flags.ResolveFormat()

	if flags.Mutating {
		if gerr := core.GuardMutation(ctx); gerr != nil {
			_ = render.RenderError(stderr, gerr, format)
			return errs.ExitCode(gerr)
		}
	}

	out, err := handler(ctx, in)
	if err != nil {
		var r errs.Renderable
		if errors.As(err, &r) && r.Renderable() {
			_ = render.Render(stdout, out, format)
		}
		_ = render.RenderError(stderr, err, format)
		return errs.ExitCode(err)
	}
	if rerr := render.Render(stdout, out, format); rerr != nil {
		fmt.Fprintf(stderr, "render error: %v\n", rerr)
		return errs.ExitCode(rerr)
	}
	// One-time apiKey banner to stderr (never stdout/JSON).
	if out.Data.APIKey != "" {
		fmt.Fprintf(stderr, "\n==> API key del primer tenant (se muestra UNA sola vez, guárdala ahora):\n    %s\n", out.Data.APIKey)
	}
	return 0
}
