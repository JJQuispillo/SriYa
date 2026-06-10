// Package tui is the interactive terminal frontend for sriyactl. It is a
// bubbletea (Elm: Model/Update/View) app layered on the SAME wiring as
// the cobra commands (internal/ops): both frontends invoke the same core
// handlers behind core.GuardMutation, so guards, dry-run and confirmation
// semantics are identical by construction.
//
// The TUI never prints to stdout/stderr directly and never uses the
// render package — output is the screen; headless output stays the
// exclusive domain of the cobra layer (ai-contract intact).
package tui

import (
	"os"
	"strings"

	tea "github.com/charmbracelet/bubbletea"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// resultMsg carries the outcome of a core handler executed via
// runHandler. kind is the envelope Kind (e.g. "TenantList"); data is the
// typed Output[T].Data payload (screens type-assert on kind).
type resultMsg struct {
	kind string
	data any
	err  error
}

// runHandler is the TUI's mini-pipeline: it executes a core handler in a
// tea.Cmd (bubbletea's goroutine) through the SAME safety chain as the
// cobra middleware (cli.RunHandler):
//
//  1. opts.BuildContext() decorates ctx with readonly / dry-run / mutable.
//  2. If the action is Mutating, core.GuardMutation enforces the
//     readonly guard — a mutation from the TUI is blocked exactly like
//     the equivalent cobra subcommand (spec: paridad de guards).
//  3. The handler runs; its typed core.Output[T] (or error) is delivered
//     to the screen as a resultMsg.
//
// The TUI never renders the envelope (no render package): screens
// project the typed Data directly.
func runHandler[In any, Out any](opts ops.Options, handler core.Handler[In, Out], in In) tea.Cmd {
	return func() tea.Msg {
		ctx := opts.BuildContext()
		if opts.Mutating {
			if gerr := core.GuardMutation(ctx); gerr != nil {
				return resultMsg{err: gerr}
			}
		}
		out, err := handler(ctx, in)
		return resultMsg{kind: out.Kind, data: out.Data, err: err}
	}
}

// App is the root bubbletea model: a stack of screens plus the shared
// session state (options, lazily-built deps, terminal size).
type App struct {
	version string
	opts    *ops.Options

	stack  []Screen
	width  int
	height int

	// deps is built lazily on first use (dashboard/tenants need it; the
	// install wizard must work BEFORE any config/context exists).
	deps     *ops.Deps
	depsErr  error
	depsInit bool

	quitting bool
}

// newApp builds the root model. opts carries --dir/--tenant (and any
// readonly/dry-run state) from the invoking frontend.
func newApp(version string, opts *ops.Options) *App {
	if opts == nil {
		opts = &ops.Options{}
	}
	a := &App{
		version: version,
		opts:    opts,
	}
	// The session opens on the dashboard (spec: menú principal +
	// dashboard al inicio); Esc reveals the menu underneath.
	a.stack = []Screen{newMenuScreen(), newDashboardScreen(a)}
	return a
}

// Deps returns the shared ops.Deps, building them on first call. The
// error is sticky for the session (screens render it as "no disponible"
// instead of crashing); `r`-style refreshes may retry via ResetDeps.
func (a *App) Deps() (*ops.Deps, error) {
	if !a.depsInit {
		a.deps, a.depsErr = ops.BuildDeps(a.opts)
		a.depsInit = true
	}
	return a.deps, a.depsErr
}

// ResetDeps drops the cached deps so the next Deps() call re-runs the
// wiring (e.g. after the install wizard seeds a brand-new context).
func (a *App) ResetDeps() { a.depsInit = false; a.deps = nil; a.depsErr = nil }

// Init implements tea.Model.
func (a *App) Init() tea.Cmd {
	if len(a.stack) == 0 {
		return tea.Quit
	}
	return a.stack[len(a.stack)-1].Init()
}

// top returns the current screen (top of the stack).
func (a *App) top() Screen { return a.stack[len(a.stack)-1] }

// Update implements tea.Model. Navigation (push/pop), terminal size and
// the global quit chord are handled here; everything else is delegated
// to the current screen.
func (a *App) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	switch msg := msg.(type) {
	case tea.WindowSizeMsg:
		a.width, a.height = msg.Width, msg.Height
		// Forward the size to every screen on the stack so a pop never
		// reveals a stale layout.
		var cmds []tea.Cmd
		for i, s := range a.stack {
			ns, cmd := s.Update(msg)
			a.stack[i] = ns
			cmds = append(cmds, cmd)
		}
		return a, tea.Batch(cmds...)

	case navPushMsg:
		a.stack = append(a.stack, msg.screen)
		cmds := []tea.Cmd{msg.screen.Init()}
		if a.width > 0 {
			ns, cmd := msg.screen.Update(tea.WindowSizeMsg{Width: a.width, Height: a.height})
			a.stack[len(a.stack)-1] = ns
			cmds = append(cmds, cmd)
		}
		return a, tea.Batch(cmds...)

	case navPopMsg:
		if len(a.stack) <= 1 {
			a.quitting = true
			return a, tea.Quit
		}
		a.stack = a.stack[:len(a.stack)-1]
		return a, nil

	case menuSelectMsg:
		return a, a.openMenuItem(msg.item)

	case tea.KeyMsg:
		if msg.Type == tea.KeyCtrlC {
			a.quitting = true
			return a, tea.Quit
		}
	}

	ns, cmd := a.top().Update(msg)
	a.stack[len(a.stack)-1] = ns
	return a, cmd
}

// openMenuItem maps a menu selection to the screen to push. Centralizing
// the mapping here keeps the menu pure (it only emits menuSelectMsg).
func (a *App) openMenuItem(item menuItem) tea.Cmd {
	switch item {
	case menuQuit:
		a.quitting = true
		return tea.Quit
	case menuDashboard:
		return navPush(newDashboardScreen(a))
	case menuTenants:
		return navPush(newTenantsScreen(a))
	case menuInstall:
		return navPush(newInstallScreen(a))
	case menuLogs:
		return navPush(newLogsScreen(a))
	}
	return nil
}

// View implements tea.Model: current screen body + status bar.
func (a *App) View() string {
	if a.quitting || len(a.stack) == 0 {
		return ""
	}
	body := a.top().View()
	return body + "\n" + a.statusBar()
}

// statusBar renders the bottom bar: app/version, resolved context and
// tenant (when deps are available), and mode badges. It NEVER triggers
// the lazy deps build on its own — it only reports what is already
// loaded, so opening the TUI without a config does not error here.
func (a *App) statusBar() string {
	parts := []string{"sriyactl " + a.version}
	if a.depsInit && a.depsErr == nil && a.deps != nil {
		if a.deps.ContextName != "" {
			parts = append(parts, "ctx:"+a.deps.ContextName)
		}
		tenant := a.opts.Tenant
		if tenant == "" {
			if lc, err := a.deps.Loader.Load(); err == nil && lc.CurrentTenant != "" {
				tenant = lc.CurrentTenant
			}
		}
		if tenant != "" {
			parts = append(parts, "tenant:"+tenant)
		}
	}
	bar := styleStatusBar.Render(strings.Join(parts, " · "))
	var badges []string
	if a.opts.ReadOnly || os.Getenv("SRIYACTL_READONLY") == "1" {
		badges = append(badges, styleBadge.Render("READONLY"))
	}
	if a.opts.DryRun {
		badges = append(badges, styleBadge.Render("DRY-RUN"))
	}
	if len(badges) > 0 {
		bar += " " + strings.Join(badges, " ")
	}
	return bar
}

// Run starts the TUI. It is the single entry point used by the main.go
// TTY gate and the `sriyactl ui` subcommand. opts propagates --dir and
// --tenant (plus readonly/dry-run) into the shared wiring.
func Run(version string, opts *ops.Options) error {
	p := tea.NewProgram(newApp(version, opts), tea.WithAltScreen())
	_, err := p.Run()
	return err
}
