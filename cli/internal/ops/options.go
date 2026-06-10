// Package ops is the shared wiring layer between the cobra frontend
// (internal/cli) and the interactive TUI (internal/tui). It owns:
//
//   - Options: the effective cross-cutting flags (read-only, dry-run,
//     tenant, install dir, confirmation bypass).
//   - Deps: the bag of dependencies handlers need (api client, secrets,
//     config, compose) plus the resolved context name.
//   - ConfirmDecision: the pure decision table for destructive-op
//     confirmation (TTY × bypass) shared by the cobra prompt and the
//     TUI modal.
//   - Seeder / prompter / secret-store constructors used by the install
//     pipeline.
//
// Both frontends MUST invoke the same core handlers through this wiring
// so guards (core.GuardMutation) and confirmation semantics stay
// identical. ops contains NO presentation logic.
package ops

import (
	"context"
	"os"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/render"
)

// Options carries the values of the persistent flags set on the cobra
// root command (and, for the TUI, the equivalent session settings). We
// pass it explicitly to subcommand constructors instead of reaching into
// a global, which keeps every command testable in isolation.
type Options struct {
	Output   string
	Dir      string
	Tenant   string
	Yes      bool
	NoInput  bool
	DryRun   bool
	ReadOnly bool
	// Mutating is set by the cli subcommand wrapper for commands that
	// call GuardMutation. Read-only commands leave it false so the
	// guard has no effect.
	Mutating bool
	// RequiresConfirm (added in sriyactl-v1-fixes, design §#1) flags
	// destructive commands whose handler MUST not run until the user
	// has explicitly confirmed via --yes/--no-input or an interactive
	// y/N prompt. Only `infra restore` and the mutation path of
	// `infra upgrade` set this. Read-only and tenant commands do not.
	RequiresConfirm bool
	// ConfirmDescription is the human-readable description of the
	// action shown in the prompt, e.g. "restore dump.sql into the
	// postgres container". It is set by the destructive command's
	// wiring and ignored when RequiresConfirm is false.
	ConfirmDescription string
}

// BuildContext derives a context.Context decorated with the read-only and
// dry-run flags. Read-only is also forced on when SRIYACTL_READONLY=1 is set
// in the environment. The single source of truth for read-only is the
// context (see core.GuardMutation).
//
// When `Mutating` is true, the returned context is also marked as a
// mutator. The guard at core.GuardMutation will then block the call if
// the context is read-only. Read-only commands should leave Mutating=false
// so the guard has no effect (it never fires for read-only contexts).
func (f Options) BuildContext() context.Context {
	ctx := context.Background()
	if f.ReadOnly || os.Getenv("SRIYACTL_READONLY") == "1" {
		ctx = core.WithReadOnly(ctx)
	}
	if f.DryRun {
		ctx = core.WithDryRun(ctx)
	}
	if f.Mutating {
		ctx = core.MarkMutable(ctx)
	}
	return ctx
}

// ResolveFormat returns the effective output format. --output wins; else
// fall back to the auto-detected value from render.AutoFormat.
func (f Options) ResolveFormat() render.Format {
	if f.Output != "" {
		return render.ParseFormat(f.Output)
	}
	return render.AutoFormat()
}
