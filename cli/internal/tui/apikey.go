package tui

import (
	"strings"

	tea "github.com/charmbracelet/bubbletea"
)

// apiKeyScreen is the one-time apiKey reveal (design "Secretos"):
//
//   - BLOCKING: it is the only thing on screen; there is no way to
//     navigate elsewhere while it is up.
//   - One-time: the key lives ONLY in this model's byte slice. On exit
//     the slice is overwritten (best-effort in Go) and the screen pops —
//     the value is not recoverable from the TUI afterwards.
//   - Exit requires EXPLICIT confirmation: esc/q/enter first ask
//     "¿ya la guardaste?"; only `y` actually leaves.
//   - The key is never written to logs or the status bar; View is the
//     single place it renders.
type apiKeyScreen struct {
	key        []byte
	confirming bool
	wiped      bool
}

func newAPIKeyScreen(key string) *apiKeyScreen {
	return &apiKeyScreen{key: []byte(key)}
}

// wipe overwrites the in-memory copy of the key (best-effort).
func (a *apiKeyScreen) wipe() {
	for i := range a.key {
		a.key[i] = 0
	}
	a.key = nil
	a.wiped = true
}

// Init implements Screen.
func (a *apiKeyScreen) Init() tea.Cmd { return nil }

// Update implements Screen.
func (a *apiKeyScreen) Update(msg tea.Msg) (Screen, tea.Cmd) {
	key, ok := msg.(tea.KeyMsg)
	if !ok {
		return a, nil
	}
	if a.confirming {
		if strings.ToLower(key.String()) == "y" {
			a.wipe()
			return a, navPop
		}
		// Anything else: keep showing the key.
		a.confirming = false
		return a, nil
	}
	switch key.String() {
	case "esc", "q", "enter":
		a.confirming = true
	}
	return a, nil
}

// View implements Screen.
func (a *apiKeyScreen) View() string {
	if a.wiped {
		return styleDim.Render("apiKey ya no disponible (se mostró una sola vez)")
	}
	var b strings.Builder
	b.WriteString(styleWarn.Render("API key del primer tenant — se muestra UNA sola vez"))
	b.WriteString("\n\n")
	b.WriteString(styleBox.Render(string(a.key)))
	b.WriteString("\n\n")
	b.WriteString("Guárdala ahora en tu gestor de secretos. Al salir de esta pantalla no podrás verla de nuevo.\n\n")
	if a.confirming {
		b.WriteString(styleError.Render("¿Ya guardaste la apiKey? Pulsa y para salir definitivamente, cualquier otra tecla para seguir viéndola."))
	} else {
		b.WriteString(styleHelp.Render("esc/enter salir (pedirá confirmación)"))
	}
	return b.String()
}
