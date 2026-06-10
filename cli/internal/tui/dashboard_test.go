package tui

import (
	"strings"
	"testing"
	"time"

	tea "github.com/charmbracelet/bubbletea"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// failingLoader simulates a dead backend: every section errors.
func failingLoader() dashboardStatus {
	err := errs.New(errs.CodeNetwork, "backend no responde", "verifica el stack")
	return dashboardStatus{infraErr: err, certErr: err, tenantErr: err}
}

// TestDashboard_BackendDownShowsUnavailable: with the infra down, the
// dashboard renders "no disponible" per section and never panics; the
// screen stays operable (esc pops back to the menu).
func TestDashboard_BackendDownShowsUnavailable(t *testing.T) {
	d := &dashboardScreen{load: failingLoader}

	// Drive the load command manually (what Init schedules).
	msg := d.loadCmd()()
	s, _ := d.Update(msg)
	d = s.(*dashboardScreen)

	view := d.View()
	if got := strings.Count(view, "no disponible"); got != 3 {
		t.Errorf("expected 3 'no disponible' sections, got %d:\n%s", got, view)
	}

	// Screen stays operable: esc pops.
	_, cmd := d.Update(keyMsg("esc"))
	if cmd == nil {
		t.Fatal("esc must emit a command")
	}
	if _, ok := cmd().(navPopMsg); !ok {
		t.Error("esc must pop back to the menu")
	}
}

// TestDashboard_HappyPathRendersSections: a healthy load shows infra,
// cert and tenant data.
func TestDashboard_HappyPathRendersSections(t *testing.T) {
	d := &dashboardScreen{load: func() dashboardStatus {
		return dashboardStatus{
			infra: &core.InfraStatusResult{
				ImageTag:   "1.0.0",
				InstallDir: "/srv/sriya",
				Services:   []core.InfraServiceRow{{Name: "api", State: "running", Health: "healthy"}},
			},
			cert: &core.CertStatusResult{
				Tenant: "acme",
				Certs:  []core.CertStatusEntry{{Subject: "ACME SA", Status: "valid", ExpiresAt: time.Date(2027, 1, 1, 0, 0, 0, 0, time.UTC), DaysLeft: 200}},
			},
			tenant: &core.TenantCurrentResult{Context: "local", Alias: "acme", ID: "tid-1"},
		}
	}}
	s, _ := d.Update(d.loadCmd()())
	d = s.(*dashboardScreen)
	view := d.View()
	for _, want := range []string{"1.0.0", "/srv/sriya", "api", "ACME SA", "acme", "valid"} {
		if !strings.Contains(view, want) {
			t.Errorf("dashboard view missing %q:\n%s", want, view)
		}
	}
	if strings.Contains(view, "no disponible") {
		t.Errorf("healthy dashboard must not show 'no disponible':\n%s", view)
	}
}

// TestDashboard_RefreshKeyReloads: pressing r schedules a reload.
func TestDashboard_RefreshKeyReloads(t *testing.T) {
	loads := 0
	d := &dashboardScreen{load: func() dashboardStatus {
		loads++
		return dashboardStatus{}
	}}
	_, cmd := d.Update(keyMsg("r"))
	if cmd == nil {
		t.Fatal("r must schedule a reload")
	}
	if _, ok := cmd().(dashboardLoadedMsg); !ok {
		t.Fatal("expected a dashboardLoadedMsg from the reload")
	}
	if loads != 1 {
		t.Errorf("expected exactly one load, got %d", loads)
	}
}

// TestDashboard_TickReloadsAndRearms: the auto-refresh tick reloads and
// schedules the next tick.
func TestDashboard_TickReloadsAndRearms(t *testing.T) {
	loads := 0
	d := &dashboardScreen{load: func() dashboardStatus {
		loads++
		return dashboardStatus{}
	}}
	_, cmd := d.Update(tickMsg(time.Now()))
	if cmd == nil {
		t.Fatal("tick must emit commands")
	}
	// The batch contains the reload + the next tick. The tick member
	// blocks ~10s, so run each member with a timeout and look for the
	// quick dashboardLoadedMsg.
	batch, ok := cmd().(tea.BatchMsg)
	if !ok {
		t.Fatalf("expected tea.BatchMsg, got %T", cmd())
	}
	if len(batch) != 2 {
		t.Fatalf("expected 2 batched commands (reload + re-arm), got %d", len(batch))
	}
	found := false
	for _, member := range batch {
		ch := make(chan tea.Msg, 1)
		go func(c tea.Cmd) { ch <- c() }(member)
		select {
		case m := <-ch:
			if _, ok := m.(dashboardLoadedMsg); ok {
				found = true
			}
		case <-time.After(100 * time.Millisecond):
			// the re-armed tick: blocks until the next interval — skip.
		}
	}
	if !found || loads == 0 {
		t.Errorf("tick must trigger a reload (found=%v loads=%d)", found, loads)
	}
}
