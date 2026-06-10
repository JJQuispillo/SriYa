package cli

import (
	"bytes"
	"errors"
	"strings"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// withFakeTUIRun swaps the TUI launcher for a recorder.
func withFakeTUIRun(t *testing.T) *struct {
	called  bool
	version string
	opts    *ops.Options
} {
	t.Helper()
	rec := &struct {
		called  bool
		version string
		opts    *ops.Options
	}{}
	orig := tuiRun
	tuiRun = func(version string, opts *ops.Options) error {
		rec.called = true
		rec.version = version
		rec.opts = opts
		return nil
	}
	t.Cleanup(func() { tuiRun = orig })
	return rec
}

// TestUICmd_KillSwitchRefuses: SRIYACTL_NO_TUI=1 must make `ui` fail with
// a clear usage error and NEVER launch the TUI.
func TestUICmd_KillSwitchRefuses(t *testing.T) {
	t.Setenv("SRIYACTL_NO_TUI", "1")
	withFakeTTY(t, true)
	rec := withFakeTUIRun(t)

	cmd := NewRootCmd("test")
	var out, errOut bytes.Buffer
	cmd.SetOut(&out)
	cmd.SetErr(&errOut)
	cmd.SetArgs([]string{"ui"})
	err := cmd.Execute()
	if err == nil {
		t.Fatal("expected error with SRIYACTL_NO_TUI=1")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeUsage {
		t.Errorf("expected usage error, got %v", err)
	}
	if !strings.Contains(err.Error(), "SRIYACTL_NO_TUI") {
		t.Errorf("expected a clear kill-switch message, got: %v", err)
	}
	if rec.called {
		t.Error("TUI MUST NOT launch when the kill-switch is set")
	}
	if errs.ExitCode(err) == 0 {
		t.Error("expected non-zero exit code")
	}
}

// TestUICmd_NonTTYRefuses: without a TTY, `ui` must fail (exit ≠ 0) with
// a clear message instead of silently printing help.
func TestUICmd_NonTTYRefuses(t *testing.T) {
	withFakeTTY(t, false)
	rec := withFakeTUIRun(t)

	cmd := NewRootCmd("test")
	cmd.SetOut(&bytes.Buffer{})
	cmd.SetErr(&bytes.Buffer{})
	cmd.SetArgs([]string{"ui"})
	err := cmd.Execute()
	if err == nil {
		t.Fatal("expected error without a TTY")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeUsage {
		t.Errorf("expected usage error, got %v", err)
	}
	if !strings.Contains(err.Error(), "terminal") {
		t.Errorf("expected a clear no-TTY message, got: %v", err)
	}
	if rec.called {
		t.Error("TUI MUST NOT launch without a TTY")
	}
	if errs.ExitCode(err) == 0 {
		t.Error("expected non-zero exit code")
	}
}

// TestUICmd_LaunchesAndPropagatesFlags: on a TTY without the kill-switch
// the TUI launches with the persistent flags (--dir/--tenant) propagated
// through the shared ops.Options.
func TestUICmd_LaunchesAndPropagatesFlags(t *testing.T) {
	withFakeTTY(t, true)
	rec := withFakeTUIRun(t)

	cmd := NewRootCmd("v1.2.3")
	cmd.SetOut(&bytes.Buffer{})
	cmd.SetErr(&bytes.Buffer{})
	cmd.SetArgs([]string{"--dir", "/tmp/sriya", "--tenant", "acme", "ui"})
	if err := cmd.Execute(); err != nil {
		t.Fatalf("ui should launch on a TTY: %v", err)
	}
	if !rec.called {
		t.Fatal("expected the TUI to launch")
	}
	if rec.version != "v1.2.3" {
		t.Errorf("expected version v1.2.3, got %q", rec.version)
	}
	if rec.opts == nil || rec.opts.Dir != "/tmp/sriya" || rec.opts.Tenant != "acme" {
		t.Errorf("expected --dir/--tenant propagated, got %+v", rec.opts)
	}
}
