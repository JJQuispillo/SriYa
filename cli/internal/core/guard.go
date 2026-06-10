package core

import (
	"context"
	"fmt"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// MutationContextKey is the context key used to flag that a handler is a
// mutator. The guard reads this value via MutableFromContext.
type mutationContextKey struct{}

// MarkMutable tags the context so the guard allows the handler to run even
// when SRIYACTL_READONLY=1 is set. Mutator handlers call this in their
// constructor; read-only handlers MUST NOT.
func MarkMutable(ctx context.Context) context.Context {
	return context.WithValue(ctx, mutationContextKey{}, true)
}

// IsMutable reports whether the context has been tagged mutable. Read-only
// handlers should leave the value unset; the guard then rejects the call
// when the CLI is in read-only mode.
func IsMutable(ctx context.Context) bool {
	v, _ := ctx.Value(mutationContextKey{}).(bool)
	return v
}

// GuardMutation is the SINGLE point where SRIYACTL_READONLY=1 is enforced.
// Every mutator handler MUST call this before any side effect. The CLI
// marks mutating-command contexts with MarkMutable (or via SharedFlags
// .Mutating = true at the cli layer); when SRIYACTL_READONLY=1 is set,
// the guard short-circuits with a CLIError that maps to exit 7.
//
// A handler that is read-only (tenant list, cert status) MUST NOT call
// this function.
func GuardMutation(ctx context.Context) error {
	if !IsMutable(ctx) {
		// Handler did not opt in as a mutator. For v1 we treat this as
		// "caller forgot to mark"; to keep the rule strict we block the
		// action rather than silently allowing it. v2 may add a list of
		// known mutating commands to the registry and remove this check.
		return errs.New(
			errs.CodeGeneric,
			"handler is not marked as a mutator",
			"this is a CLI bug: a mutating handler must call MarkMutable(ctx) before any side effect",
		)
	}
	if readOnlyFromContext(ctx) {
		return errs.New(
			errs.CodeReadOnlyBlocked,
			"mutating command blocked: CLI is in read-only mode",
			"unset SRIYACTL_READONLY or use a read-only context",
		)
	}
	return nil
}

// ReadOnlyContextKey is the context key set by the CLI middleware when
// SRIYACTL_READONLY=1 (or the active context is read-only).
type readOnlyKey struct{}

func readOnlyFromContext(ctx context.Context) bool {
	v, _ := ctx.Value(readOnlyKey{}).(bool)
	return v
}

// WithReadOnly returns a derived context marked as read-only. Used by the
// CLI middleware (task 1.6). Exposed for tests and for callers that build
// custom context trees.
func WithReadOnly(ctx context.Context) context.Context {
	return context.WithValue(ctx, readOnlyKey{}, true)
}

// DryRunContextKey flags a context so handlers can short-circuit and return
// a Plan object instead of performing the action. See task 1.6.
type dryRunKey struct{}

// WithDryRun returns a derived context tagged with --dry-run. Handlers read
// this via IsDryRun and report a Plan instead of mutating.
func WithDryRun(ctx context.Context) context.Context {
	return context.WithValue(ctx, dryRunKey{}, true)
}

// IsDryRun reports whether the caller passed --dry-run.
func IsDryRun(ctx context.Context) bool {
	v, _ := ctx.Value(dryRunKey{}).(bool)
	return v
}

// Plan represents a planned-but-not-executed action. Mutator handlers
// return a Plan wrapped in their Output when --dry-run is active. The
// render layer projects the Plan as a table (human) or JSON (machine).
type Plan struct {
	Action  string         `json:"action"  yaml:"action"`
	Target  string         `json:"target"  yaml:"target"`
	Details map[string]any `json:"details,omitempty" yaml:"details,omitempty"`
}

// String renders a one-line description for table mode.
func (p Plan) String() string {
	return fmt.Sprintf("%s -> %s", p.Action, p.Target)
}
