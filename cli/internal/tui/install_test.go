package tui

import (
	"strings"
	"testing"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/huh"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// submitInstallForm fills the bound data and forces completion through
// the screen's own submit path (huh keystroke simulation is huh's
// concern, not ours).
func submitInstallForm(t *testing.T, s *installScreen) (Screen, tea.Cmd) {
	t.Helper()
	*s.data = installFormData{
		dir:         "/tmp/sriya-test",
		port:        "9999",
		contextName: "local",
		doBootstrap: true,
		ruc:         "1790012345001",
		razonSocial: "ACME SA",
		ownerName:   "Ana",
		password:    "s3cr3t",
		certPath:    "/tmp/cert.p12",
		apiKeyName:  "bootstrap",
	}
	s.form.State = huh.StateCompleted
	return s.Update(struct{}{}) // any msg routes through updateForm
}

// TestInstall_SubmitBuildsRequestAndRuns: completing the form executes
// the install with the collected fields; the wizard switches to the
// running phase.
func TestInstall_SubmitBuildsRequestAndRuns(t *testing.T) {
	var got core.InfraInstallRequest
	exec := func(req core.InfraInstallRequest) tea.Cmd {
		got = req
		return func() tea.Msg {
			return resultMsg{kind: "InfraInstall", data: core.InfraInstallResult{Healthy: true}}
		}
	}
	s := newInstallScreenWith(ops.Options{}, exec)
	ns, cmd := submitInstallForm(t, s)
	s = ns.(*installScreen)
	if s.phase != installRunning {
		t.Fatalf("expected running phase, got %d", s.phase)
	}
	if cmd == nil {
		t.Fatal("submit must schedule the install")
	}
	_ = cmd()
	if got.Dir != "/tmp/sriya-test" || got.Port != "9999" {
		t.Errorf("request mismatch: %+v", got)
	}
	if got.NoBootstrap {
		t.Error("doBootstrap=true must map to NoBootstrap=false")
	}
	if got.Boot.RUC != "1790012345001" || got.Boot.Password != "s3cr3t" || got.Boot.CertificatePath != "/tmp/cert.p12" {
		t.Errorf("bootstrap fields mismatch: %+v", got.Boot)
	}
	if got.LocalURL != "http://localhost:9999" {
		t.Errorf("LocalURL mismatch: %q", got.LocalURL)
	}
}

// TestInstall_FailureIsRecoverable: a failed step shows the error and
// returns to the menu WITHOUT closing the TUI (spec: install fallido).
func TestInstall_FailureIsRecoverable(t *testing.T) {
	exec := func(req core.InfraInstallRequest) tea.Cmd {
		return func() tea.Msg {
			return resultMsg{err: errs.New(errs.CodeNetwork, "health-wait agotado", "revisa docker")}
		}
	}
	s := newInstallScreenWith(ops.Options{}, exec)
	ns, cmd := submitInstallForm(t, s)
	s = ns.(*installScreen)

	// Deliver the failure.
	ns, _ = s.Update(cmd())
	s = ns.(*installScreen)
	if s.phase != installFailed {
		t.Fatalf("expected failed phase, got %d", s.phase)
	}
	view := s.View()
	if !strings.Contains(view, "health-wait agotado") {
		t.Errorf("expected the error in the view:\n%s", view)
	}

	// enter goes back to the menu (navPop) — the program keeps running.
	_, cmd = s.Update(keyMsg("enter"))
	if cmd == nil {
		t.Fatal("enter must navigate back")
	}
	if _, ok := cmd().(navPopMsg); !ok {
		t.Error("expected navPopMsg back to the menu")
	}
}

// TestInstall_SuccessWithAPIKeyPushesOneTimeScreen: the apiKey triggers
// the blocking one-time screen and the wizard keeps NO copy of it.
func TestInstall_SuccessWithAPIKeyPushesOneTimeScreen(t *testing.T) {
	exec := func(req core.InfraInstallRequest) tea.Cmd {
		return func() tea.Msg {
			return resultMsg{data: core.InfraInstallResult{Healthy: true, APIKey: "sk-one-time-123", TenantID: "tid-1"}}
		}
	}
	s := newInstallScreenWith(ops.Options{}, exec)
	ns, cmd := submitInstallForm(t, s)
	s = ns.(*installScreen)

	ns, cmd = s.Update(cmd())
	s = ns.(*installScreen)
	if cmd == nil {
		t.Fatal("success with apiKey must push the one-time screen")
	}
	push, ok := cmd().(navPushMsg)
	if !ok {
		t.Fatalf("expected navPushMsg, got %T", cmd())
	}
	ak, ok := push.screen.(*apiKeyScreen)
	if !ok {
		t.Fatalf("expected apiKeyScreen, got %T", push.screen)
	}
	if string(ak.key) != "sk-one-time-123" {
		t.Errorf("apiKey screen must receive the key")
	}
	// The wizard itself retains nothing.
	if s.result.APIKey != "" || s.result.TenantID != "" {
		t.Error("the wizard must not keep a copy of the secret")
	}
	if strings.Contains(s.View(), "sk-one-time-123") {
		t.Error("the wizard view must never show the apiKey")
	}
}

// TestInstall_FormAbortReturnsToMenu: aborting the form (esc) executes
// nothing and pops back.
func TestInstall_FormAbortReturnsToMenu(t *testing.T) {
	called := false
	exec := func(req core.InfraInstallRequest) tea.Cmd {
		called = true
		return nil
	}
	s := newInstallScreenWith(ops.Options{}, exec)
	s.form.State = huh.StateAborted
	_, cmd := s.Update(struct{}{})
	if called {
		t.Error("aborting the form must not execute the install")
	}
	if cmd == nil {
		t.Fatal("abort must navigate back")
	}
	if _, ok := cmd().(navPopMsg); !ok {
		t.Error("expected navPopMsg on abort")
	}
}

// TestInstall_ProgressLinesAccumulate: streamLineMsg lines render during
// the running phase.
func TestInstall_ProgressLinesAccumulate(t *testing.T) {
	exec := func(req core.InfraInstallRequest) tea.Cmd { return nil }
	s := newInstallScreenWith(ops.Options{}, exec)
	ns, _ := submitInstallForm(t, s)
	s = ns.(*installScreen)
	ns, _ = s.Update(StreamLineMsg("==> rendering .env"))
	s = ns.(*installScreen)
	ns, _ = s.Update(StreamLineMsg("==> compose up -d"))
	s = ns.(*installScreen)
	view := s.View()
	if !strings.Contains(view, "rendering .env") || !strings.Contains(view, "compose up -d") {
		t.Errorf("expected progress lines in view:\n%s", view)
	}
}
