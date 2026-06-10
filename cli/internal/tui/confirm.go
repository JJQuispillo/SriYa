package tui

import (
	"strings"

	tea "github.com/charmbracelet/bubbletea"

	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// confirmScreen is the destructive-op confirmation modal. It is governed
// by the SAME pure decision table as the cobra prompt
// (ops.ConfirmDecision) so both frontends can never diverge:
//
//   - DecisionProceed (--yes / --no-input): the action runs immediately,
//     no modal is shown — identical to cobra's bypass.
//   - DecisionPrompt: the y/N modal is shown. ONLY `y` runs the action;
//     anything else (n, enter, esc — the default is N) cancels and
//     returns to the previous screen WITHOUT executing.
//   - DecisionRefuse cannot occur inside the TUI (it is interactive by
//     construction: isTTY=true), but the table is still consulted —
//     never re-implemented.
//
// v1 ships no destructive TUI action (tenants/install are not
// RequiresConfirm); the modal is the ready-made pattern for v1.1
// (restore/upgrade).
type confirmScreen struct {
	opts        ops.Options
	description string
	action      tea.Cmd
}

// newConfirmScreen builds the modal for an action described by
// description (e.g. "restore dump.sql into the postgres container").
func newConfirmScreen(opts ops.Options, description string, action tea.Cmd) *confirmScreen {
	return &confirmScreen{opts: opts, description: description, action: action}
}

// Init implements Screen: consult the decision table. A bypass pops the
// modal and runs the action straight away.
func (c *confirmScreen) Init() tea.Cmd {
	switch ops.ConfirmDecision(c.opts, true /* the TUI is interactive */) {
	case ops.DecisionProceed:
		return tea.Batch(navPop, c.action)
	case ops.DecisionRefuse:
		// Unreachable in a TTY, kept for table completeness: refuse →
		// never execute.
		return navPop
	}
	return nil // DecisionPrompt: wait for y/N
}

// Update implements Screen.
func (c *confirmScreen) Update(msg tea.Msg) (Screen, tea.Cmd) {
	key, ok := msg.(tea.KeyMsg)
	if !ok {
		return c, nil
	}
	switch strings.ToLower(key.String()) {
	case "y":
		// Same meaning as answering `y` to the cobra prompt.
		return c, tea.Batch(navPop, c.action)
	default:
		// Default N: anything else cancels without executing.
		return c, navPop
	}
}

// View implements Screen.
func (c *confirmScreen) View() string {
	body := "Confirmación requerida\n\n" +
		"Vas a " + c.description + ".\n\n" +
		"¿Continuar? " + styleSelected.Render("[y/N]") + "\n\n" +
		styleHelp.Render("y ejecutar · cualquier otra tecla cancelar")
	return styleBox.Render(body)
}
