package tui

import (
	"errors"
	"fmt"
	"strings"
	"time"

	tea "github.com/charmbracelet/bubbletea"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// dashboardRefreshInterval is the auto-refresh tick (design open
// question resolved: 10s + manual `r`).
const dashboardRefreshInterval = 10 * time.Second

// tickMsg drives the dashboard auto-refresh.
type tickMsg time.Time

// dashboardStatus aggregates the three status sections. Each section
// carries its own error so a dead backend degrades to "no disponible"
// per-section instead of crashing the screen (spec scenario).
type dashboardStatus struct {
	infra     *core.InfraStatusResult
	infraErr  error
	cert      *core.CertStatusResult
	certErr   error
	tenant    *core.TenantCurrentResult
	tenantErr error
}

// dashboardLoadedMsg delivers a completed load to the screen.
type dashboardLoadedMsg struct{ status dashboardStatus }

// dashboardLoader produces a dashboardStatus. It runs inside a tea.Cmd
// (off the UI thread). Tests inject a fake; production uses
// productionDashboardLoader.
type dashboardLoader func() dashboardStatus

// dashboardScreen shows infra / certificate / tenant state at a glance.
type dashboardScreen struct {
	load    dashboardLoader
	status  dashboardStatus
	loaded  bool
	loading bool
}

func newDashboardScreen(a *App) *dashboardScreen {
	return &dashboardScreen{load: productionDashboardLoader(a)}
}

// productionDashboardLoader builds the three sections through the shared
// ops wiring + core handlers (read-only: Mutating stays false). A failed
// deps build is retried once (ResetDeps) so the dashboard recovers after
// the install wizard seeds a brand-new context.
func productionDashboardLoader(a *App) dashboardLoader {
	return func() dashboardStatus {
		var st dashboardStatus
		deps, err := a.Deps()
		if err != nil {
			a.ResetDeps()
			deps, err = a.Deps()
		}
		if err != nil {
			st.infraErr, st.certErr, st.tenantErr = err, err, err
			return st
		}
		opts := *a.opts
		opts.Mutating = false
		ctx := opts.BuildContext()

		if out, ierr := core.InfraStatusHandler(core.InfraDeps{API: deps.API, Compose: deps.Compose})(ctx, struct{}{}); ierr != nil && !isRenderable(ierr) {
			st.infraErr = ierr
		} else {
			st.infra = &out.Data
		}

		certDeps := core.CertDeps{
			API:                 deps.API,
			Store:               deps.TenantStore,
			Config:              ops.LoadRawConfig(),
			ContextName:         deps.ContextName,
			TenantAliasOverride: opts.Tenant,
		}
		if out, cerr := core.CertStatusHandler(certDeps)(ctx, core.CertStatusRequest{TenantAlias: opts.Tenant}); cerr != nil && !isRenderable(cerr) {
			st.certErr = cerr
		} else {
			st.cert = &out.Data
		}

		tenantDeps := core.TenantDeps{
			API:         deps.API,
			Store:       deps.TenantStore,
			Secret:      deps.Secret,
			Config:      ops.LoadRawConfig(),
			ContextName: deps.ContextName,
		}
		if out, terr := core.TenantCurrentHandler(tenantDeps)(ctx, struct{}{}); terr != nil {
			st.tenantErr = terr
		} else {
			st.tenant = &out.Data
		}
		return st
	}
}

// isRenderable reports whether the error is a sentinel that still wants
// its payload shown (e.g. cert expiring, infra degraded). The dashboard
// keeps the data in that case — the section itself displays the state.
func isRenderable(err error) bool {
	var r errs.Renderable
	return errors.As(err, &r) && r.Renderable()
}

// loadCmd wraps the loader into a tea.Cmd.
func (d *dashboardScreen) loadCmd() tea.Cmd {
	load := d.load
	return func() tea.Msg { return dashboardLoadedMsg{status: load()} }
}

// tickCmd schedules the next auto-refresh.
func tickCmd() tea.Cmd {
	return tea.Tick(dashboardRefreshInterval, func(t time.Time) tea.Msg { return tickMsg(t) })
}

// Init implements Screen.
func (d *dashboardScreen) Init() tea.Cmd {
	d.loading = true
	return tea.Batch(d.loadCmd(), tickCmd())
}

// Update implements Screen.
func (d *dashboardScreen) Update(msg tea.Msg) (Screen, tea.Cmd) {
	switch msg := msg.(type) {
	case dashboardLoadedMsg:
		d.status = msg.status
		d.loaded = true
		d.loading = false
		return d, nil
	case tickMsg:
		// Auto-refresh: reload and re-arm the tick. The chain stops by
		// itself when the screen is no longer on top (the App routes
		// messages to the top screen only).
		return d, tea.Batch(d.loadCmd(), tickCmd())
	case tea.KeyMsg:
		switch msg.String() {
		case "r":
			d.loading = true
			return d, d.loadCmd()
		case "q", "esc":
			return d, navPop
		}
	}
	return d, nil
}

// View implements Screen.
func (d *dashboardScreen) View() string {
	var b strings.Builder
	b.WriteString(styleTitle.Render("Dashboard"))
	b.WriteString("\n\n")
	if !d.loaded {
		b.WriteString(styleDim.Render("cargando estado…"))
		b.WriteString("\n")
	} else {
		b.WriteString(d.viewInfra())
		b.WriteString("\n")
		b.WriteString(d.viewCert())
		b.WriteString("\n")
		b.WriteString(d.viewTenant())
	}
	b.WriteString("\n")
	hint := "r refrescar · esc menú"
	if d.loading && d.loaded {
		hint = "refrescando… · " + hint
	}
	b.WriteString(styleHelp.Render(hint))
	return b.String()
}

func (d *dashboardScreen) viewInfra() string {
	var b strings.Builder
	b.WriteString("Infra\n")
	if d.status.infraErr != nil || d.status.infra == nil {
		b.WriteString("  " + styleError.Render("no disponible") + styleDim.Render(" — "+shortErr(d.status.infraErr)) + "\n")
		return b.String()
	}
	in := d.status.infra
	state := "ok"
	if in.Degraded {
		state = "degradado"
	}
	b.WriteString(fmt.Sprintf("  estado: %s · imagen: %s · dir: %s\n", state, orDash(in.ImageTag), orDash(in.InstallDir)))
	for _, svc := range in.Services {
		b.WriteString(fmt.Sprintf("  - %-12s %s %s\n", svc.Name, svc.State, svc.Health))
	}
	return b.String()
}

func (d *dashboardScreen) viewCert() string {
	var b strings.Builder
	b.WriteString("Certificado\n")
	if d.status.certErr != nil || d.status.cert == nil {
		b.WriteString("  " + styleError.Render("no disponible") + styleDim.Render(" — "+shortErr(d.status.certErr)) + "\n")
		return b.String()
	}
	c := d.status.cert
	if len(c.Certs) == 0 {
		b.WriteString("  sin certificados registrados\n")
		return b.String()
	}
	for _, e := range c.Certs {
		line := fmt.Sprintf("  - %s · %s · vence %s (%d días)", e.Subject, e.Status, e.ExpiresAt.Format("2006-01-02"), e.DaysLeft)
		if e.Status != "valid" {
			line = styleError.Render(line)
		}
		b.WriteString(line + "\n")
	}
	return b.String()
}

func (d *dashboardScreen) viewTenant() string {
	var b strings.Builder
	b.WriteString("Tenant\n")
	if d.status.tenantErr != nil || d.status.tenant == nil {
		b.WriteString("  " + styleError.Render("no disponible") + styleDim.Render(" — "+shortErr(d.status.tenantErr)) + "\n")
		return b.String()
	}
	t := d.status.tenant
	b.WriteString(fmt.Sprintf("  activo: %s (ctx %s)\n", t.Alias, t.Context))
	return b.String()
}

// shortErr renders a compact, single-line error description.
func shortErr(err error) string {
	if err == nil {
		return "sin datos"
	}
	s := err.Error()
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		s = s[:i]
	}
	if len(s) > 80 {
		s = s[:77] + "…"
	}
	return s
}

func orDash(s string) string {
	if s == "" {
		return "—"
	}
	return s
}
