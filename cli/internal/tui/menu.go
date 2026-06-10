package tui

import (
	"strings"

	tea "github.com/charmbracelet/bubbletea"
)

// menuItem identifies a main-menu entry. The menu screen itself never
// constructs other screens — it emits a menuSelectMsg and the App decides
// what to push (keeps the menu pure and trivially testable).
type menuItem int

const (
	menuDashboard menuItem = iota
	menuInstall
	menuTenants
	menuLogs
	menuQuit
)

// label is the human label of the entry.
func (m menuItem) label() string {
	switch m {
	case menuDashboard:
		return "Dashboard (estado de la instalación)"
	case menuInstall:
		return "Install (provisionar el stack día-1)"
	case menuTenants:
		return "Tenants (listar / crear / activar)"
	case menuLogs:
		return "Logs (visor con follow)"
	case menuQuit:
		return "Salir"
	}
	return "?"
}

// menuSelectMsg is emitted when the operator activates an entry.
type menuSelectMsg struct{ item menuItem }

// menuScreen is the main navigable menu.
type menuScreen struct {
	items  []menuItem
	cursor int
}

func newMenuScreen() *menuScreen {
	return &menuScreen{
		items: []menuItem{menuDashboard, menuInstall, menuTenants, menuLogs, menuQuit},
	}
}

// Init implements Screen.
func (m *menuScreen) Init() tea.Cmd { return nil }

// Update implements Screen: ↑/↓ (or k/j) move, enter activates, q/esc
// quits (the menu is the root screen — popping it exits the app).
func (m *menuScreen) Update(msg tea.Msg) (Screen, tea.Cmd) {
	key, ok := msg.(tea.KeyMsg)
	if !ok {
		return m, nil
	}
	switch key.String() {
	case "up", "k":
		if m.cursor > 0 {
			m.cursor--
		}
	case "down", "j":
		if m.cursor < len(m.items)-1 {
			m.cursor++
		}
	case "enter":
		item := m.items[m.cursor]
		return m, func() tea.Msg { return menuSelectMsg{item: item} }
	case "q", "esc":
		return m, navPop
	}
	return m, nil
}

// View implements Screen.
func (m *menuScreen) View() string {
	var b strings.Builder
	b.WriteString(styleTitle.Render("SriYa — menú principal"))
	b.WriteString("\n\n")
	for i, it := range m.items {
		line := "  " + it.label()
		if i == m.cursor {
			line = styleSelected.Render("> " + it.label())
		}
		b.WriteString(line)
		b.WriteString("\n")
	}
	b.WriteString("\n")
	b.WriteString(styleHelp.Render("↑/↓ mover · enter abrir · q salir"))
	return b.String()
}
