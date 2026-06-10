package tui

import "github.com/charmbracelet/lipgloss"

// Centralized lipgloss styles. Screens MUST use these instead of ad-hoc
// styles so the TUI stays visually coherent and a future theme swap is a
// one-file change.
var (
	// styleTitle heads each screen.
	styleTitle = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color("212")).Padding(0, 1)

	// styleStatusBar is the bottom bar with context/tenant/badges.
	styleStatusBar = lipgloss.NewStyle().
			Foreground(lipgloss.Color("230")).
			Background(lipgloss.Color("62")).
			Padding(0, 1)

	// styleBadge marks operating modes (READONLY / DRY-RUN) on the
	// status bar.
	styleBadge = lipgloss.NewStyle().
			Foreground(lipgloss.Color("230")).
			Background(lipgloss.Color("160")).
			Bold(true).
			Padding(0, 1)

	// styleError renders handler/load failures inline (never crash).
	styleError = lipgloss.NewStyle().Foreground(lipgloss.Color("160")).Bold(true)

	// styleHelp renders the per-screen key hints.
	styleHelp = lipgloss.NewStyle().Foreground(lipgloss.Color("241"))

	// styleSelected highlights the focused menu/table row.
	styleSelected = lipgloss.NewStyle().Foreground(lipgloss.Color("212")).Bold(true)

	// styleDim renders secondary text (values not focused).
	styleDim = lipgloss.NewStyle().Foreground(lipgloss.Color("245"))

	// styleWarn renders the one-time apiKey warning banner.
	styleWarn = lipgloss.NewStyle().
			Foreground(lipgloss.Color("230")).
			Background(lipgloss.Color("166")).
			Bold(true).
			Padding(0, 1)

	// styleBox draws a rounded border around modal-like content.
	styleBox = lipgloss.NewStyle().
			Border(lipgloss.RoundedBorder()).
			Padding(1, 2)
)
