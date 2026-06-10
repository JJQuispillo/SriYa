package tui

import tea "github.com/charmbracelet/bubbletea"

// Screen is the contract every TUI screen implements. The root App model
// keeps a stack of Screens (push/pop navigation) and delegates Init /
// Update / View to the top of the stack. Screens are plain Elm models:
// they MUST NOT perform I/O in Update — side effects go in tea.Cmd.
type Screen interface {
	// Init returns the screen's initial command (e.g. first data load).
	Init() tea.Cmd
	// Update handles a message and returns the (possibly replaced)
	// screen plus a follow-up command.
	Update(msg tea.Msg) (Screen, tea.Cmd)
	// View renders the screen body (the App adds the status bar).
	View() string
}

// navPushMsg asks the App to push a new screen onto the stack.
type navPushMsg struct{ screen Screen }

// navPopMsg asks the App to pop the current screen (back to the previous
// one). Popping the last screen quits the program.
type navPopMsg struct{}

// navPush wraps a screen into a tea.Cmd the current screen can return.
func navPush(s Screen) tea.Cmd {
	return func() tea.Msg { return navPushMsg{screen: s} }
}

// navPop is a tea.Cmd that pops the current screen.
func navPop() tea.Msg { return navPopMsg{} }
