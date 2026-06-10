package tui

import (
	"context"
	"errors"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// TestRunHandler_ReadOnlyBlocksMutation: a mutating action launched from
// the TUI pipeline under readonly MUST be blocked by core.GuardMutation
// with readonly_blocked (exit 7) and the handler MUST NOT run — exactly
// like the equivalent cobra subcommand (spec: paridad de guards).
func TestRunHandler_ReadOnlyBlocksMutation(t *testing.T) {
	called := false
	handler := core.Handler[struct{}, string](func(ctx context.Context, _ struct{}) (core.Output[string], error) {
		called = true
		return core.NewOutput("X", "boom"), nil
	})
	opts := ops.Options{ReadOnly: true, Mutating: true}

	msg := runHandler(opts, handler, struct{}{})()
	rm, ok := msg.(resultMsg)
	if !ok {
		t.Fatalf("expected resultMsg, got %T", msg)
	}
	if rm.err == nil {
		t.Fatal("expected readonly_blocked error")
	}
	var ce *errs.CLIError
	if !errors.As(rm.err, &ce) || ce.Code != errs.CodeReadOnlyBlocked {
		t.Errorf("expected readonly_blocked, got %v", rm.err)
	}
	if errs.ExitCode(rm.err) != 7 {
		t.Errorf("expected the same exit semantics as cobra (7), got %d", errs.ExitCode(rm.err))
	}
	if called {
		t.Error("handler MUST NOT run when the readonly guard blocks")
	}
}

// TestRunHandler_EnvReadOnlyBlocksMutation: SRIYACTL_READONLY=1 has the
// same effect as --readonly (single source of truth: BuildContext).
func TestRunHandler_EnvReadOnlyBlocksMutation(t *testing.T) {
	t.Setenv("SRIYACTL_READONLY", "1")
	called := false
	handler := core.Handler[struct{}, string](func(ctx context.Context, _ struct{}) (core.Output[string], error) {
		called = true
		return core.NewOutput("X", "boom"), nil
	})
	msg := runHandler(ops.Options{Mutating: true}, handler, struct{}{})()
	rm := msg.(resultMsg)
	var ce *errs.CLIError
	if rm.err == nil || !errors.As(rm.err, &ce) || ce.Code != errs.CodeReadOnlyBlocked {
		t.Errorf("expected readonly_blocked via env, got %v", rm.err)
	}
	if called {
		t.Error("handler MUST NOT run under SRIYACTL_READONLY=1")
	}
}

// TestRunHandler_MutationProceedsWhenWritable: without readonly the
// mutation runs and the typed payload comes back in the resultMsg.
func TestRunHandler_MutationProceedsWhenWritable(t *testing.T) {
	handler := core.Handler[string, string](func(ctx context.Context, in string) (core.Output[string], error) {
		if !core.IsMutable(ctx) {
			t.Error("pipeline must mark the context mutable for Mutating actions")
		}
		return core.NewOutput("Echo", "hola "+in), nil
	})
	msg := runHandler(ops.Options{Mutating: true}, handler, "tui")()
	rm := msg.(resultMsg)
	if rm.err != nil {
		t.Fatalf("unexpected error: %v", rm.err)
	}
	if rm.kind != "Echo" || rm.data.(string) != "hola tui" {
		t.Errorf("unexpected result: %+v", rm)
	}
}

// TestRunHandler_ReadOnlyAllowsReadOnlyActions: read-only handlers
// (Mutating=false) are NOT affected by readonly mode — the guard never
// fires for them (same as cobra).
func TestRunHandler_ReadOnlyAllowsReadOnlyActions(t *testing.T) {
	handler := core.Handler[struct{}, int](func(ctx context.Context, _ struct{}) (core.Output[int], error) {
		return core.NewOutput("N", 42), nil
	})
	msg := runHandler(ops.Options{ReadOnly: true}, handler, struct{}{})()
	rm := msg.(resultMsg)
	if rm.err != nil || rm.data.(int) != 42 {
		t.Errorf("read-only action must run under readonly mode, got %+v", rm)
	}
}

// TestRunHandler_DryRunFlowsToContext: --dry-run reaches the handler via
// the context so handlers can return a Plan instead of mutating.
func TestRunHandler_DryRunFlowsToContext(t *testing.T) {
	handler := core.Handler[struct{}, bool](func(ctx context.Context, _ struct{}) (core.Output[bool], error) {
		return core.NewOutput("DryRun", core.IsDryRun(ctx)), nil
	})
	msg := runHandler(ops.Options{DryRun: true, Mutating: true}, handler, struct{}{})()
	rm := msg.(resultMsg)
	if rm.err != nil || rm.data.(bool) != true {
		t.Errorf("expected IsDryRun=true in handler ctx, got %+v", rm)
	}
}
