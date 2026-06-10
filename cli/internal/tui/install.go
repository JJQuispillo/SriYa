package tui

import (
	"bufio"
	"fmt"
	"io"
	"strings"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/huh"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// installPhase is the wizard's lifecycle.
type installPhase int

const (
	installForm installPhase = iota
	installRunning
	installDone
	installFailed
)

// installFormData carries the huh-bound fields. The form collects EVERY
// bootstrap field up-front (design D5): the handler is wired with
// interactive=false / interactor=nil, so it can never re-prompt under
// bubbletea (no nested prompts).
type installFormData struct {
	dir         string
	version     string
	port        string
	corsOrigin  string
	dbUser      string
	contextName string
	doBootstrap bool
	// First-tenant bootstrap fields.
	ruc             string
	razonSocial     string
	ownerName       string
	password        string
	certPath        string
	nombreComercial string
	correoContacto  string
	apiKeyName      string
}

// installExec runs the install request and returns the tea.Cmd that will
// deliver the resultMsg. Tests inject a fake; production goes through
// ops.BuildInstallDeps + runHandler (same pipeline as cobra).
type installExec func(req core.InfraInstallRequest) tea.Cmd

// installScreen is the day-1 wizard: form → run (live progress) →
// one-time apiKey / error.
type installScreen struct {
	app  *App
	opts ops.Options
	exec installExec

	phase    installPhase
	form     *huh.Form
	data     *installFormData
	progress []string
	result   *core.InfraInstallResult
	err      error

	streamCmd tea.Cmd
}

func newInstallScreen(a *App) *installScreen {
	s := &installScreen{app: a, opts: *a.opts}
	s.exec = s.defaultExec
	s.form, s.data = s.newForm()
	return s
}

// newInstallScreenWith is the test-friendly constructor.
func newInstallScreenWith(opts ops.Options, exec installExec) *installScreen {
	s := &installScreen{opts: opts, exec: exec}
	s.form, s.data = s.newForm()
	return s
}

// defaultExec wires the production handler. Progress is streamed through
// a pipe so lines arrive as StreamLineMsg; the handler result arrives as
// resultMsg as before.
func (s *installScreen) defaultExec(req core.InfraInstallRequest) tea.Cmd {
	opts := s.opts
	opts.Mutating = true
	pr, pw := io.Pipe()
	deps := ops.BuildInstallDeps(&opts, req.Dir, req.Port, pw, func() bool { return false }, nil)

	scanner := bufio.NewScanner(pr)
	s.streamCmd = func() tea.Msg {
		if scanner.Scan() {
			return StreamLineMsg(scanner.Text())
		}
		return StreamDoneMsg{}
	}

	handlerCmd := func() tea.Msg {
		defer pw.Close()
		ctx := opts.BuildContext()
		if opts.Mutating {
			if gerr := core.GuardMutation(ctx); gerr != nil {
				return resultMsg{err: gerr}
			}
		}
		out, err := core.InfraInstallHandler(deps)(ctx, req)
		return resultMsg{kind: out.Kind, data: out.Data, err: err}
	}

	return tea.Batch(s.streamCmd, handlerCmd)
}

// newForm builds the huh form with sensible defaults pre-filled.
func (s *installScreen) newForm() (*huh.Form, *installFormData) {
	data := &installFormData{
		port:        "8080",
		contextName: "local",
		apiKeyName:  "bootstrap",
		doBootstrap: true,
	}
	if dir, err := ops.ResolveInstallDir(&s.opts); err == nil {
		data.dir = dir
	}
	required := func(label string) func(string) error {
		return func(v string) error {
			if strings.TrimSpace(v) == "" {
				return fmt.Errorf("%s es obligatorio", label)
			}
			return nil
		}
	}
	form := huh.NewForm(
		huh.NewGroup(
			huh.NewInput().Title("Directorio de instalación").Value(&data.dir).Validate(required("el directorio")),
			huh.NewInput().Title("Versión (tag de imagen, vacío = 1.0.0)").Value(&data.version),
			huh.NewInput().Title("Puerto del API").Value(&data.port),
			huh.NewInput().Title("CORS origin (opcional)").Value(&data.corsOrigin),
			huh.NewInput().Title("Usuario de BD (opcional)").Value(&data.dbUser),
			huh.NewInput().Title("Alias del contexto local").Value(&data.contextName),
			huh.NewConfirm().Title("¿Crear el primer tenant (bootstrap)?").Value(&data.doBootstrap),
		),
		huh.NewGroup(
			huh.NewInput().Title("RUC").Value(&data.ruc),
			huh.NewInput().Title("Razón social").Value(&data.razonSocial),
			huh.NewInput().Title("Nombre del propietario").Value(&data.ownerName),
			huh.NewInput().Title("Contraseña del propietario").EchoMode(huh.EchoModePassword).Value(&data.password),
			huh.NewFilePicker().
				Title("Certificado (.p12/.pfx)").
				AllowedTypes([]string{".p12", ".pfx"}).
				FileAllowed(true).
				DirAllowed(false).
				Value(&data.certPath),
			huh.NewInput().Title("Nombre comercial (opcional)").Value(&data.nombreComercial),
			huh.NewInput().Title("Correo de contacto (opcional)").Value(&data.correoContacto),
			huh.NewInput().Title("Etiqueta de la api key").Value(&data.apiKeyName),
		).WithHideFunc(func() bool { return !data.doBootstrap }),
	)
	return form, data
}

// buildRequest maps the form into the typed install request.
func (s *installScreen) buildRequest() core.InfraInstallRequest {
	d := *s.data
	return core.InfraInstallRequest{
		Version:     d.version,
		Port:        d.port,
		CorsOrigin:  d.corsOrigin,
		DBUser:      d.dbUser,
		Dir:         d.dir,
		NoBootstrap: !d.doBootstrap,
		ContextName: d.contextName,
		LocalURL:    ops.LocalAPIURL(d.port),
		Boot: api.BootstrapRequest{
			RUC:             d.ruc,
			RazonSocial:     d.razonSocial,
			OwnerName:       d.ownerName,
			Password:        d.password,
			CertificatePath: d.certPath,
			NombreComercial: d.nombreComercial,
			CorreoContacto:  d.correoContacto,
			APIKeyName:      d.apiKeyName,
		},
	}
}

// Init implements Screen.
func (s *installScreen) Init() tea.Cmd {
	if s.phase == installForm && s.form != nil {
		return s.form.Init()
	}
	return nil
}

// Update implements Screen.
func (s *installScreen) Update(msg tea.Msg) (Screen, tea.Cmd) {
	switch s.phase {
	case installForm:
		return s.updateForm(msg)
	case installRunning:
		return s.updateRunning(msg)
	default: // installDone / installFailed
		if key, ok := msg.(tea.KeyMsg); ok {
			switch key.String() {
			case "enter", "esc", "q":
				return s, navPop
			}
		}
		return s, nil
	}
}

func (s *installScreen) updateForm(msg tea.Msg) (Screen, tea.Cmd) {
	model, cmd := s.form.Update(msg)
	if f, ok := model.(*huh.Form); ok {
		s.form = f
	}
	switch s.form.State {
	case huh.StateCompleted:
		req := s.buildRequest()
		s.phase = installRunning
		s.progress = append(s.progress, "instalando el stack…")
		return s, s.exec(req)
	case huh.StateAborted:
		// Cancelled: back to the menu, nothing executed.
		return s, navPop
	}
	return s, cmd
}

func (s *installScreen) updateRunning(msg tea.Msg) (Screen, tea.Cmd) {
	switch msg := msg.(type) {
	case StreamLineMsg:
		s.progress = append(s.progress, string(msg))
		return s, s.streamCmd
	case StreamDoneMsg:
		return s, nil
	case resultMsg:
		return s.onResult(msg)
	}
	return s, nil
}

// onResult ends the run: a failure becomes a recoverable error screen
// (back to the menu WITHOUT closing the TUI — spec scenario); a success
// with a bootstrap apiKey pushes the blocking one-time reveal.
func (s *installScreen) onResult(msg resultMsg) (Screen, tea.Cmd) {
	if msg.err != nil {
		s.phase = installFailed
		s.err = msg.err
		return s, nil
	}
	res, ok := msg.data.(core.InfraInstallResult)
	if !ok {
		s.phase = installFailed
		s.err = fmt.Errorf("resultado inesperado del install: %T", msg.data)
		return s, nil
	}
	s.phase = installDone
	if s.app != nil {
		// A fresh context/token may have been seeded: rebuild deps so the
		// dashboard/tenants pick it up.
		s.app.ResetDeps()
	}
	apiKey := res.APIKey
	res.APIKey = "" // the screen keeps NO copy of the secret
	res.TenantID = ""
	s.result = &res
	if apiKey != "" {
		return s, navPush(newAPIKeyScreen(apiKey))
	}
	return s, nil
}

// View implements Screen.
func (s *installScreen) View() string {
	var b strings.Builder
	b.WriteString(styleTitle.Render("Install — día 1"))
	b.WriteString("\n\n")
	switch s.phase {
	case installForm:
		b.WriteString(s.form.View())
	case installRunning:
		for _, line := range s.progress {
			b.WriteString(styleDim.Render(line))
			b.WriteString("\n")
		}
		b.WriteString("\n")
		b.WriteString(styleHelp.Render("instalando… (esto puede tardar; la salud se espera hasta 90s)"))
	case installFailed:
		b.WriteString(styleError.Render("el install falló: " + shortErr(s.err)))
		b.WriteString("\n\n")
		b.WriteString("El stack queda como esté; corrige la causa y reintenta desde el menú.\n\n")
		b.WriteString(styleHelp.Render("enter/esc volver al menú"))
	case installDone:
		if s.result != nil {
			r := s.result
			b.WriteString(fmt.Sprintf("instalación completa · dir %s · imagen %s · healthy=%t\n", r.InstallDir, orDash(r.ImageTag), r.Healthy))
			if r.NextStep != "" {
				b.WriteString("siguiente paso: " + r.NextStep + "\n")
			}
		}
		b.WriteString("\n")
		b.WriteString(styleHelp.Render("enter/esc volver al menú"))
	}
	return b.String()
}
