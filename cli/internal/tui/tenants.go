package tui

import (
	"fmt"
	"strings"

	"github.com/charmbracelet/bubbles/table"
	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/huh"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// tenantHandlers groups the core handlers the screen drives. Production
// wiring builds them from ops.Deps; tests inject fakes — the screen
// itself never talks to config/API directly.
type tenantHandlers struct {
	list   core.Handler[struct{}, core.TenantListResult]
	use    core.Handler[core.TenantUseRequest, core.TenantUseResult]
	create core.Handler[core.TenantCreateRequest, core.TenantCreateResult]
}

// productionTenantHandlers wires the real handlers over the shared deps.
func productionTenantHandlers(a *App) (tenantHandlers, error) {
	deps, err := a.Deps()
	if err != nil {
		return tenantHandlers{}, err
	}
	td := core.TenantDeps{
		API:         deps.API,
		Store:       deps.TenantStore,
		Secret:      deps.Secret,
		Config:      ops.LoadRawConfig(),
		ContextName: deps.ContextName,
	}
	return tenantHandlers{
		list:   core.TenantListHandler(td),
		use:    core.TenantUseHandler(td),
		create: core.TenantCreateHandler(td),
	}, nil
}

// tenantsMode is the screen's sub-state.
type tenantsMode int

const (
	tenantsList tenantsMode = iota
	tenantsCreate
)

// tenantCreateForm carries the huh-bound fields for `create`.
type tenantCreateForm struct {
	alias           string
	ruc             string
	razonSocial     string
	ownerName       string
	password        string
	certPath        string
	nombreComercial string
	correoContacto  string
}

// tenantsScreen lists tenants, activates one (use) and creates new ones.
type tenantsScreen struct {
	opts     ops.Options
	handlers tenantHandlers
	wireErr  error // deps could not be built (e.g. no config yet)

	mode    tenantsMode
	table   table.Model
	rows    []core.TenantListEntry
	loaded  bool
	statusL string // last action feedback line
	lastErr error

	form     *huh.Form
	formData *tenantCreateForm
}

func newTenantsScreen(a *App) *tenantsScreen {
	handlers, err := productionTenantHandlers(a)
	return newTenantsScreenWith(*a.opts, handlers, err)
}

// newTenantsScreenWith is the test-friendly constructor.
func newTenantsScreenWith(opts ops.Options, handlers tenantHandlers, wireErr error) *tenantsScreen {
	t := table.New(
		table.WithColumns([]table.Column{
			{Title: "Alias", Width: 16},
			{Title: "RUC", Width: 14},
			{Title: "Env", Width: 8},
			{Title: "Activo", Width: 6},
			{Title: "ID", Width: 36},
		}),
		table.WithFocused(true),
		table.WithHeight(8),
	)
	return &tenantsScreen{opts: opts, handlers: handlers, wireErr: wireErr, table: t}
}

// loadCmd refreshes the tenant list through the shared pipeline
// (read-only: Mutating=false).
func (s *tenantsScreen) loadCmd() tea.Cmd {
	if s.wireErr != nil || s.handlers.list == nil {
		return nil
	}
	opts := s.opts
	opts.Mutating = false
	return runHandler(opts, s.handlers.list, struct{}{})
}

// useCmd activates the selected tenant. It is a MUTATION: the pipeline
// marks the context mutable so core.GuardMutation blocks it under
// readonly — same outcome as `sriyactl tenant use` (paridad de guards).
func (s *tenantsScreen) useCmd(alias string) tea.Cmd {
	opts := s.opts
	opts.Mutating = true
	return runHandler(opts, s.handlers.use, core.TenantUseRequest{Alias: alias})
}

// createCmd onboards a new tenant (mutation, same guard parity).
func (s *tenantsScreen) createCmd(f tenantCreateForm) tea.Cmd {
	opts := s.opts
	opts.Mutating = true
	req := core.TenantCreateRequest{
		Alias:           f.alias,
		RUC:             f.ruc,
		RazonSocial:     f.razonSocial,
		OwnerName:       f.ownerName,
		Password:        f.password,
		CertificatePath: f.certPath,
		NombreComercial: f.nombreComercial,
		CorreoContacto:  f.correoContacto,
		APIKeyName:      "bootstrap",
		// ShowAPIKey stays false: the TUI never prints the apiKey here;
		// it is auto-captured to the keychain by the handler.
	}
	return runHandler(opts, s.handlers.create, req)
}

// newCreateForm builds the huh form. The password field is masked
// (EchoModePassword) per spec.
func (s *tenantsScreen) newCreateForm() (*huh.Form, *tenantCreateForm) {
	data := &tenantCreateForm{}
	form := huh.NewForm(
		huh.NewGroup(
			huh.NewInput().Title("Alias").Value(&data.alias),
			huh.NewInput().Title("RUC").Value(&data.ruc),
			huh.NewInput().Title("Razón social").Value(&data.razonSocial),
			huh.NewInput().Title("Nombre del propietario").Value(&data.ownerName),
			huh.NewInput().Title("Contraseña del propietario").EchoMode(huh.EchoModePassword).Value(&data.password),
			huh.NewInput().Title("Ruta del certificado (.p12/.pfx)").Value(&data.certPath),
			huh.NewInput().Title("Nombre comercial (opcional)").Value(&data.nombreComercial),
			huh.NewInput().Title("Correo de contacto (opcional)").Value(&data.correoContacto),
		),
	)
	return form, data
}

// Init implements Screen.
func (s *tenantsScreen) Init() tea.Cmd { return s.loadCmd() }

// Update implements Screen.
func (s *tenantsScreen) Update(msg tea.Msg) (Screen, tea.Cmd) {
	if s.mode == tenantsCreate {
		return s.updateCreate(msg)
	}
	return s.updateList(msg)
}

func (s *tenantsScreen) updateList(msg tea.Msg) (Screen, tea.Cmd) {
	switch msg := msg.(type) {
	case resultMsg:
		return s.onResult(msg)
	case tea.KeyMsg:
		switch msg.String() {
		case "q", "esc":
			return s, navPop
		case "r":
			return s, s.loadCmd()
		case "c":
			if s.wireErr != nil {
				return s, nil
			}
			s.mode = tenantsCreate
			s.form, s.formData = s.newCreateForm()
			return s, s.form.Init()
		case "enter", "u":
			if alias := s.selectedAlias(); alias != "" {
				s.statusL = "activando " + alias + "…"
				return s, s.useCmd(alias)
			}
			return s, nil
		}
	}
	var cmd tea.Cmd
	s.table, cmd = s.table.Update(msg)
	return s, cmd
}

func (s *tenantsScreen) updateCreate(msg tea.Msg) (Screen, tea.Cmd) {
	if rm, ok := msg.(resultMsg); ok {
		return s.onResult(rm)
	}
	if key, ok := msg.(tea.KeyMsg); ok && key.String() == "esc" {
		// Abort the form: back to the list, nothing executed.
		s.mode = tenantsList
		s.form, s.formData = nil, nil
		s.statusL = "creación cancelada"
		return s, nil
	}
	model, cmd := s.form.Update(msg)
	if f, ok := model.(*huh.Form); ok {
		s.form = f
	}
	if s.form.State == huh.StateCompleted {
		data := *s.formData
		s.mode = tenantsList
		s.form, s.formData = nil, nil
		s.statusL = "creando tenant " + data.alias + "…"
		return s, s.createCmd(data)
	}
	if s.form.State == huh.StateAborted {
		s.mode = tenantsList
		s.form, s.formData = nil, nil
		s.statusL = "creación cancelada"
		return s, nil
	}
	return s, cmd
}

// onResult routes handler outcomes by envelope kind.
func (s *tenantsScreen) onResult(msg resultMsg) (Screen, tea.Cmd) {
	if msg.err != nil {
		s.lastErr = msg.err
		s.statusL = ""
		return s, nil
	}
	s.lastErr = nil
	switch data := msg.data.(type) {
	case core.TenantListResult:
		s.setRows(data)
		s.loaded = true
		return s, nil
	case core.TenantUseResult:
		s.statusL = fmt.Sprintf("tenant activo: %s", data.Alias)
		return s, s.loadCmd()
	case core.TenantCreateResult:
		s.statusL = "tenant creado (apiKey capturada en el keychain)"
		return s, s.loadCmd()
	}
	return s, nil
}

func (s *tenantsScreen) setRows(data core.TenantListResult) {
	s.rows = data.Tenants
	rows := make([]table.Row, 0, len(data.Tenants))
	for _, t := range data.Tenants {
		active := ""
		if t.IsActive {
			active = "✓"
		}
		rows = append(rows, table.Row{t.Alias, t.RUC, t.Env, active, t.ID})
	}
	s.table.SetRows(rows)
}

// selectedAlias returns the alias of the focused row ("" when empty).
func (s *tenantsScreen) selectedAlias() string {
	if len(s.rows) == 0 {
		return ""
	}
	row := s.table.SelectedRow()
	if len(row) == 0 {
		return ""
	}
	return row[0]
}

// View implements Screen.
func (s *tenantsScreen) View() string {
	var b strings.Builder
	b.WriteString(styleTitle.Render("Tenants"))
	b.WriteString("\n\n")
	if s.mode == tenantsCreate && s.form != nil {
		b.WriteString(s.form.View())
		b.WriteString("\n")
		b.WriteString(styleHelp.Render("enter siguiente · esc cancelar"))
		return b.String()
	}
	if s.wireErr != nil {
		b.WriteString(styleError.Render("no disponible") + styleDim.Render(" — "+shortErr(s.wireErr)))
		b.WriteString("\n\n")
		b.WriteString(styleHelp.Render("esc menú"))
		return b.String()
	}
	if !s.loaded {
		b.WriteString(styleDim.Render("cargando tenants…"))
		b.WriteString("\n")
	} else {
		b.WriteString(s.table.View())
		b.WriteString("\n")
	}
	if s.lastErr != nil {
		b.WriteString(styleError.Render("error: " + shortErr(s.lastErr)))
		b.WriteString("\n")
	}
	if s.statusL != "" {
		b.WriteString(styleDim.Render(s.statusL))
		b.WriteString("\n")
	}
	b.WriteString(styleHelp.Render("enter/u activar · c crear · r refrescar · esc menú"))
	return b.String()
}
