package tui

import (
	"strings"
	"testing"

	tea "github.com/charmbracelet/bubbletea"
)

func keyMsg(s string) tea.KeyMsg {
	switch s {
	case "enter":
		return tea.KeyMsg{Type: tea.KeyEnter}
	case "esc":
		return tea.KeyMsg{Type: tea.KeyEsc}
	case "up":
		return tea.KeyMsg{Type: tea.KeyUp}
	case "down":
		return tea.KeyMsg{Type: tea.KeyDown}
	default:
		return tea.KeyMsg{Type: tea.KeyRunes, Runes: []rune(s)}
	}
}

// TestMenu_Navigation drives the cursor with arrows + vim keys and
// asserts selection messages.
func TestMenu_Navigation(t *testing.T) {
	m := newMenuScreen()

	// Initial selection is the first item (dashboard).
	s, cmd := m.Update(keyMsg("enter"))
	m = s.(*menuScreen)
	if cmd == nil {
		t.Fatal("enter must emit a command")
	}
	if got := cmd(); got != (menuSelectMsg{item: menuDashboard}) {
		t.Errorf("expected dashboard selection, got %#v", got)
	}

	// Down twice (mixing arrow and vim key) → tenants.
	s, _ = m.Update(keyMsg("down"))
	m = s.(*menuScreen)
	s, _ = m.Update(keyMsg("j"))
	m = s.(*menuScreen)
	s, cmd = m.Update(keyMsg("enter"))
	m = s.(*menuScreen)
	if got := cmd(); got != (menuSelectMsg{item: menuTenants}) {
		t.Errorf("expected tenants selection, got %#v", got)
	}

	// Up once → install.
	s, _ = m.Update(keyMsg("k"))
	m = s.(*menuScreen)
	s, cmd = m.Update(keyMsg("enter"))
	m = s.(*menuScreen)
	if got := cmd(); got != (menuSelectMsg{item: menuInstall}) {
		t.Errorf("expected install selection, got %#v", got)
	}

	// Cursor clamps at the top.
	for i := 0; i < 10; i++ {
		s, _ = m.Update(keyMsg("up"))
		m = s.(*menuScreen)
	}
	if m.cursor != 0 {
		t.Errorf("cursor must clamp at 0, got %d", m.cursor)
	}
	// And at the bottom (quit).
	for i := 0; i < 10; i++ {
		s, _ = m.Update(keyMsg("down"))
		m = s.(*menuScreen)
	}
	s, cmd = m.Update(keyMsg("enter"))
	_ = s
	if got := cmd(); got != (menuSelectMsg{item: menuQuit}) {
		t.Errorf("expected quit selection at bottom, got %#v", got)
	}
}

// TestMenu_QuitKeys: q / esc pop the (root) screen.
func TestMenu_QuitKeys(t *testing.T) {
	for _, k := range []string{"q", "esc"} {
		m := newMenuScreen()
		_, cmd := m.Update(keyMsg(k))
		if cmd == nil {
			t.Fatalf("%s must emit a command", k)
		}
		if _, ok := cmd().(navPopMsg); !ok {
			t.Errorf("%s must emit navPopMsg", k)
		}
	}
}

// TestMenu_ViewListsEntries: the view shows every destination and the
// key hints.
func TestMenu_ViewListsEntries(t *testing.T) {
	v := newMenuScreen().View()
	for _, want := range []string{"Dashboard", "Install", "Tenants", "Logs", "Salir"} {
		if !strings.Contains(v, want) {
			t.Errorf("menu view missing %q:\n%s", want, v)
		}
	}
}
