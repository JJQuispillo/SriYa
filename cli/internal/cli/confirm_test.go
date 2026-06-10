package cli

import (
	"bytes"
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/render"
)

// withFakeTTY swaps the package-level TTY check for the duration of
// the test. Returns a restore function (also wired to t.Cleanup).
func withFakeTTY(t *testing.T, tty bool) {
	t.Helper()
	orig := isTerminalFn
	isTerminalFn = func(int) bool { return tty }
	t.Cleanup(func() { isTerminalFn = orig })
}

// -----------------------------------------------------------------------------
// 1. Confirm gate: TTY + answer "n" → abort, NO side effect, non-zero exit
// -----------------------------------------------------------------------------

func TestConfirm_TTY_RejectsEmpty(t *testing.T) {
	withFakeTTY(t, true)
	flags := SharedFlags{Mutating: true, RequiresConfirm: true, ConfirmDescription: "restore dump"}
	var stdout, stderr bytes.Buffer
	stdin := strings.NewReader("\n") // empty line → abort
	err := Confirm(flags, stdin, &stdout, "restore dump")
	if err == nil {
		t.Fatal("expected confirmation_aborted, got nil")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeConfirmAborted {
		t.Errorf("expected confirmation_aborted, got %s", ce.Code)
	}
	if errs.ExitCode(err) == 0 {
		t.Error("expected non-zero exit code")
	}
	// Prompt was emitted to stdout (human-visible, NOT to stderr).
	if !strings.Contains(stdout.String(), "Continue? [y/N]:") {
		t.Errorf("expected prompt in stdout, got %q", stdout.String())
	}
	_ = stderr
}

// -----------------------------------------------------------------------------
// 2. Confirm gate: TTY + answer "n" (explicit) → abort
// -----------------------------------------------------------------------------

func TestConfirm_TTY_RejectsN(t *testing.T) {
	withFakeTTY(t, true)
	flags := SharedFlags{RequiresConfirm: true, ConfirmDescription: "restore dump"}
	stdin := strings.NewReader("n\n")
	err := Confirm(flags, stdin, &bytes.Buffer{}, "restore dump")
	if err == nil {
		t.Fatal("expected abort on 'n'")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeConfirmAborted {
		t.Errorf("expected confirmation_aborted, got %v", err)
	}
}

// -----------------------------------------------------------------------------
// 3. Confirm gate: TTY + answer "y" → proceed, no error
// -----------------------------------------------------------------------------

func TestConfirm_TTY_AcceptsYes(t *testing.T) {
	withFakeTTY(t, true)
	flags := SharedFlags{RequiresConfirm: true, ConfirmDescription: "restore dump"}
	stdin := strings.NewReader("y\n")
	var stdout bytes.Buffer
	if err := Confirm(flags, stdin, &stdout, "restore dump"); err != nil {
		t.Errorf("expected no error on 'y', got %v", err)
	}
	if !strings.Contains(stdout.String(), "restore dump") {
		t.Errorf("expected prompt to mention 'restore dump', got %q", stdout.String())
	}
}

// -----------------------------------------------------------------------------
// 4. Confirm gate: --yes bypasses the prompt entirely
// -----------------------------------------------------------------------------

func TestConfirm_YesBypasses(t *testing.T) {
	withFakeTTY(t, true) // TTY or non-TTY: --yes always wins.
	flags := SharedFlags{Yes: true, RequiresConfirm: true}
	if err := Confirm(flags, strings.NewReader(""), &bytes.Buffer{}, "x"); err != nil {
		t.Errorf("expected --yes to bypass, got %v", err)
	}
}

// -----------------------------------------------------------------------------
// 5. Confirm gate: --no-input bypasses the prompt entirely
// -----------------------------------------------------------------------------

func TestConfirm_NoInputBypasses(t *testing.T) {
	withFakeTTY(t, true)
	flags := SharedFlags{NoInput: true, RequiresConfirm: true}
	if err := Confirm(flags, strings.NewReader(""), &bytes.Buffer{}, "x"); err != nil {
		t.Errorf("expected --no-input to bypass, got %v", err)
	}
}

// -----------------------------------------------------------------------------
// 6. Confirm gate: non-TTY + no bypass → confirmation_required, exit 2
// -----------------------------------------------------------------------------

func TestConfirm_NonTTYRefusesWithoutBypass(t *testing.T) {
	withFakeTTY(t, false)
	flags := SharedFlags{RequiresConfirm: true, ConfirmDescription: "restore dump"}
	err := Confirm(flags, strings.NewReader(""), &bytes.Buffer{}, "restore dump")
	if err == nil {
		t.Fatal("expected confirmation_required on non-TTY without bypass")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeConfirmRequired {
		t.Errorf("expected confirmation_required, got %v", err)
	}
	if got := errs.ExitCode(err); got != 2 {
		t.Errorf("expected exit 2 (usage) for confirmation_required, got %d", got)
	}
}

// -----------------------------------------------------------------------------
// 7. Confirm gate: non-TTY + --yes → proceed
// -----------------------------------------------------------------------------

func TestConfirm_NonTTYWithYesProceeds(t *testing.T) {
	withFakeTTY(t, false)
	flags := SharedFlags{Yes: true, RequiresConfirm: true}
	if err := Confirm(flags, strings.NewReader(""), &bytes.Buffer{}, "x"); err != nil {
		t.Errorf("expected --yes to proceed even on non-TTY, got %v", err)
	}
}

// -----------------------------------------------------------------------------
// 8. RunHandler: renderable sentinel emits payload to stdout AND error to
//    stderr, non-zero exit. This is the design §#4 fix.
// -----------------------------------------------------------------------------

// minimalHandler is a fake handler that returns a renderable error
// alongside a typed payload. We use it to drive RunHandler in
// isolation (the real handlers are in core and would create an
// import cycle here).
type minimalPayload struct {
	Items []string `json:"items"`
}

func minimalRenderableHandler(_ context.Context, _ struct{}) (core.Output[minimalPayload], error) {
	return core.Output[minimalPayload]{
		SchemaVersion: "1.0",
		Kind:          "Minimal",
		Data:          minimalPayload{Items: []string{"alpha", "beta"}},
	}, errs.New(errs.CodeCertExpiring, "demo expiring", "rotate it").MarkRenderable()
}

func TestRunHandler_RenderableEmitsPayloadThenError(t *testing.T) {
	var stdout, stderr bytes.Buffer
	exit := RunHandler(
		SharedFlags{Output: "json"}, // force JSON for stable assertions
		&stdout, &stderr,
		minimalRenderableHandler,
		struct{}{},
	)
	if exit == 0 {
		t.Fatal("expected non-zero exit for renderable sentinel")
	}
	// stdout MUST contain the payload (JSON envelope with the data).
	outStr := stdout.String()
	if !strings.Contains(outStr, `"items"`) {
		t.Errorf("expected payload on stdout, got: %s", outStr)
	}
	if !strings.Contains(outStr, `"alpha"`) || !strings.Contains(outStr, `"beta"`) {
		t.Errorf("expected payload data on stdout, got: %s", outStr)
	}
	// stderr MUST contain the error envelope (JSON: "code":"cert_expiring").
	if !strings.Contains(stderr.String(), `cert_expiring`) {
		t.Errorf("expected cert_expiring on stderr, got: %s", stderr.String())
	}
}

// -----------------------------------------------------------------------------
// 9. RunHandler: non-renderable error does NOT emit payload to stdout
//    (only the error envelope on stderr). This is the ai-contract
//    behavior for fatal errors like auth or network-hard.
// -----------------------------------------------------------------------------

type minimalFatalHandler struct{}

func (minimalFatalHandler) fatalErr() error {
	return errs.New(errs.CodeAuth, "no token", "set SRIYACTL_SERVICE_TOKEN")
}

func minimalFatalHandlerFn(_ context.Context, _ struct{}) (core.Output[minimalPayload], error) {
	return core.Output[minimalPayload]{}, minimalFatalHandler{}.fatalErr()
}

func TestRunHandler_FatalErrorOmitsPayload(t *testing.T) {
	var stdout, stderr bytes.Buffer
	exit := RunHandler(
		SharedFlags{Output: "json"},
		&stdout, &stderr,
		minimalFatalHandlerFn,
		struct{}{},
	)
	if exit == 0 {
		t.Fatal("expected non-zero exit for fatal error")
	}
	// stdout should be empty (no payload) — only the error envelope on
	// stderr.
	if strings.Contains(stdout.String(), `"items"`) {
		t.Errorf("expected no payload on stdout for fatal error, got: %s", stdout.String())
	}
	if !strings.Contains(stderr.String(), `auth_invalid`) {
		t.Errorf("expected auth_invalid on stderr, got: %s", stderr.String())
	}
}

// -----------------------------------------------------------------------------
// 10. RunHandler: Confirm gate runs before the handler and short-circuits
//     when RequiresConfirm is set and bypass is absent. This is the
//     design §#1 integration check.
// -----------------------------------------------------------------------------

func TestRunHandler_ConfirmGateShortCircuits(t *testing.T) {
	// Force non-TTY so the gate refuses.
	withFakeTTY(t, false)
	called := false
	handler := func(_ context.Context, _ struct{}) (core.Output[minimalPayload], error) {
		called = true
		return core.Output[minimalPayload]{Kind: "X", Data: minimalPayload{}}, nil
	}
	flags := SharedFlags{
		Output:             "json",
		Mutating:           true,
		RequiresConfirm:    true,
		ConfirmDescription: "destroy the universe",
	}
	var stdout, stderr bytes.Buffer
	exit := RunHandler(flags, &stdout, &stderr, handler, struct{}{})
	if exit == 0 {
		t.Fatal("expected non-zero exit from confirm gate")
	}
	if called {
		t.Error("handler MUST NOT be called when confirm gate refuses")
	}
	if !strings.Contains(stderr.String(), "confirmation_required") {
		t.Errorf("expected confirmation_required on stderr, got: %s", stderr.String())
	}
}

// -----------------------------------------------------------------------------
// 11. RunHandler: --dry-run skips the confirm gate (no side effect).
// -----------------------------------------------------------------------------

func TestRunHandler_DryRunSkipsConfirm(t *testing.T) {
	// Non-TTY + no bypass: would normally refuse. But --dry-run means
	// no side effect → confirm gate is skipped (design §#1).
	withFakeTTY(t, false)
	called := false
	handler := func(_ context.Context, _ struct{}) (core.Output[minimalPayload], error) {
		called = true
		return core.Output[minimalPayload]{SchemaVersion: "1.0", Kind: "X", Data: minimalPayload{Items: []string{"plan"}}}, nil
	}
	flags := SharedFlags{
		Output:             "json",
		Mutating:           true,
		RequiresConfirm:    true,
		ConfirmDescription: "would mutate",
		DryRun:             true,
	}
	var stdout, stderr bytes.Buffer
	exit := RunHandler(flags, &stdout, &stderr, handler, struct{}{})
	if exit != 0 {
		t.Errorf("expected exit 0 for dry-run, got %d (stderr=%s)", exit, stderr.String())
	}
	if !called {
		t.Error("handler MUST be called under --dry-run")
	}
}

// -----------------------------------------------------------------------------
// 12. RunHandler: readonly gate wins over the confirm gate. A mutating
//     command run in read-only mode on a non-TTY without --yes must fail
//     with readonly_blocked (exit 7), NOT confirmation_required (exit 2).
//     This is the design §#1 / tasks 1.1 precedence fix.
// -----------------------------------------------------------------------------

func TestRunHandler_ReadOnlyWinsOverConfirm(t *testing.T) {
	// Non-TTY + no bypass: the confirm gate alone would refuse with
	// confirmation_required (exit 2). Readonly must short-circuit FIRST
	// with readonly_blocked (exit 7).
	withFakeTTY(t, false)
	called := false
	handler := func(_ context.Context, _ struct{}) (core.Output[minimalPayload], error) {
		called = true
		return core.Output[minimalPayload]{Kind: "X", Data: minimalPayload{}}, nil
	}
	flags := SharedFlags{
		Output:             "json",
		Mutating:           true,
		RequiresConfirm:    true,
		ReadOnly:           true,
		ConfirmDescription: "destroy the universe",
	}
	var stdout, stderr bytes.Buffer
	exit := RunHandler(flags, &stdout, &stderr, handler, struct{}{})
	if exit != 7 {
		t.Fatalf("expected exit 7 (readonly_blocked), got %d (stderr=%s)", exit, stderr.String())
	}
	if called {
		t.Error("handler MUST NOT be called when readonly gate blocks")
	}
	if !strings.Contains(stderr.String(), "readonly_blocked") {
		t.Errorf("expected readonly_blocked on stderr, got: %s", stderr.String())
	}
	if strings.Contains(stderr.String(), "confirmation_required") {
		t.Errorf("readonly must win; confirmation_required must NOT appear, got: %s", stderr.String())
	}
}

//  13. RunHandler: --yes + readonly still ends at readonly_blocked (exit 7).
//     The bypass MUST NOT let a readonly mutation through.
func TestRunHandler_ReadOnlyWinsOverYes(t *testing.T) {
	withFakeTTY(t, true) // TTY irrelevant; --yes set.
	called := false
	handler := func(_ context.Context, _ struct{}) (core.Output[minimalPayload], error) {
		called = true
		return core.Output[minimalPayload]{Kind: "X", Data: minimalPayload{}}, nil
	}
	flags := SharedFlags{
		Output:          "json",
		Mutating:        true,
		RequiresConfirm: true,
		ReadOnly:        true,
		Yes:             true,
	}
	var stdout, stderr bytes.Buffer
	exit := RunHandler(flags, &stdout, &stderr, handler, struct{}{})
	if exit != 7 {
		t.Fatalf("expected exit 7 (readonly_blocked) with --yes+readonly, got %d (stderr=%s)", exit, stderr.String())
	}
	if called {
		t.Error("handler MUST NOT be called when readonly gate blocks, even with --yes")
	}
	if !strings.Contains(stderr.String(), "readonly_blocked") {
		t.Errorf("expected readonly_blocked on stderr, got: %s", stderr.String())
	}
}

//  14. NewRootCmd: --version is wired (cobra auto-flag) and prints the
//     injected version, exiting 0. Guards the goreleaser brew test:
//     `sriyactl --version`.
func TestRootCmd_VersionFlag(t *testing.T) {
	cmd := NewRootCmd("v9.9.9")
	if cmd.Version != "v9.9.9" {
		t.Fatalf("expected cmd.Version to be set to v9.9.9, got %q", cmd.Version)
	}
	var out, errOut bytes.Buffer
	cmd.SetOut(&out)
	cmd.SetErr(&errOut)
	cmd.SetArgs([]string{"--version"})
	if err := cmd.Execute(); err != nil {
		t.Fatalf("--version should exit cleanly, got: %v (stderr=%s)", err, errOut.String())
	}
	if !strings.Contains(out.String(), "v9.9.9") {
		t.Errorf("expected version output to contain v9.9.9, got: %q", out.String())
	}
}

// silence the imports we use indirectly.
var (
	_ = render.FormatJSON
	_ = api.Health{}
)
