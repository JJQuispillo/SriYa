package tui

import (
	"flag"
	"os"
	"path/filepath"
	"testing"
	"time"

	tea "github.com/charmbracelet/bubbletea"

	"github.com/JJQuispillo/billing/cli/internal/core"
)

var update = flag.Bool("update", false, "update golden files")

func TestGolden_Menu(t *testing.T) {
	m := newMenuScreen()
	m.Update(tea.WindowSizeMsg{Width: 80, Height: 24})

	golden := filepath.Join("testdata", t.Name()+".golden")
	got := m.View()
	if *update {
		os.WriteFile(golden, []byte(got), 0o644)
	}
	want, err := os.ReadFile(golden)
	if err != nil {
		t.Fatal(err)
	}
	if got != string(want) {
		t.Errorf("golden mismatch. got:\n%s\nwant:\n%s", got, want)
	}
}

func TestGolden_DashboardErrors(t *testing.T) {
	d := &dashboardScreen{load: failingLoader}
	msg := d.loadCmd()()
	s, _ := d.Update(msg)
	d = s.(*dashboardScreen)

	golden := filepath.Join("testdata", t.Name()+".golden")
	got := d.View()
	if *update {
		os.WriteFile(golden, []byte(got), 0o644)
	}
	want, err := os.ReadFile(golden)
	if err != nil {
		t.Fatal(err)
	}
	if got != string(want) {
		t.Errorf("golden mismatch. got:\n%s\nwant:\n%s", got, want)
	}
}

func TestGolden_DashboardHealthy(t *testing.T) {
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

	golden := filepath.Join("testdata", t.Name()+".golden")
	got := d.View()
	if *update {
		os.WriteFile(golden, []byte(got), 0o644)
	}
	want, err := os.ReadFile(golden)
	if err != nil {
		t.Fatal(err)
	}
	if got != string(want) {
		t.Errorf("golden mismatch. got:\n%s\nwant:\n%s", got, want)
	}
}
