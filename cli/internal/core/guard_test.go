package core

import (
	"context"
	"errors"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

func TestGuardMutation_AllowsInNormalMode(t *testing.T) {
	ctx := MarkMutable(context.Background())
	if err := GuardMutation(ctx); err != nil {
		t.Errorf("expected no error, got %v", err)
	}
}

func TestGuardMutation_BlocksInReadOnly(t *testing.T) {
	ctx := MarkMutable(WithReadOnly(context.Background()))
	err := GuardMutation(ctx)
	if err == nil {
		t.Fatal("expected error in read-only mode")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeReadOnlyBlocked {
		t.Errorf("expected code=readonly_blocked, got %s", ce.Code)
	}
}

// TestGuardMutation_RejectsUnmarkedHandler documents the strict policy:
// a handler that does not call MarkMutable is treated as a CLI bug. This
// is intentional: it prevents accidental mutations from slipping past
// the gate because a future handler forgot to opt in.
func TestGuardMutation_RejectsUnmarkedHandler(t *testing.T) {
	ctx := context.Background()
	if err := GuardMutation(ctx); err == nil {
		t.Error("expected error for unmarked handler")
	}
}

func TestIsDryRun(t *testing.T) {
	if IsDryRun(context.Background()) {
		t.Error("default context should not be dry-run")
	}
	ctx := WithDryRun(context.Background())
	if !IsDryRun(ctx) {
		t.Error("WithDryRun context should report dry-run")
	}
}
