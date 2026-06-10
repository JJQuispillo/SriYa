package tui

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strings"

	"github.com/charmbracelet/bubbles/viewport"
	tea "github.com/charmbracelet/bubbletea"

	"github.com/JJQuispillo/billing/cli/internal/core"
)

// logScreen is a scrollable log viewer that follows compose logs in real
// time. It connects to InfraLogsHandler with follow=true via the stream
// adapter and renders lines into a viewport.
type logScreen struct {
	app    *App
	vp     viewport.Model
	cancel context.CancelFunc

	lines     []string
	done      bool
	streamErr error
	wireErr   error

	streamCmd tea.Cmd
}

func newLogsScreen(a *App) *logScreen {
	return &logScreen{app: a}
}

// Init implements Screen. It builds deps and starts the log stream.
func (s *logScreen) Init() tea.Cmd {
	deps, err := s.app.Deps()
	if err != nil {
		s.wireErr = err
		return nil
	}
	ctx := s.app.opts.BuildContext()
	ctx, s.cancel = context.WithCancel(ctx)

	s.lines = nil
	s.done = false
	s.streamErr = nil
	s.streamCmd = startStream(ctx, func(w io.Writer) error {
		handler := core.InfraLogsHandler(core.InfraDeps{
			Compose: deps.Compose,
			API:     deps.API,
		})
		return handler(ctx, core.InfraLogsRequest{Follow: true}, w)
	})
	return s.streamCmd
}

// Update implements Screen.
func (s *logScreen) Update(msg tea.Msg) (Screen, tea.Cmd) {
	switch msg := msg.(type) {
	case StreamLineMsg:
		s.lines = append(s.lines, string(msg))
		s.vp.SetContent(strings.Join(s.lines, "\n"))
		s.vp.GotoBottom()
		return s, s.streamCmd

	case StreamDoneMsg:
		s.done = true
		s.streamErr = msg.Err
		s.lines = append(s.lines, "— Log stream ended —")
		if msg.Err != nil && !errors.Is(msg.Err, context.Canceled) {
			s.lines = append(s.lines, fmt.Sprintf("error: %v", msg.Err))
		}
		s.vp.SetContent(strings.Join(s.lines, "\n"))
		return s, nil

	case tea.WindowSizeMsg:
		headerHeight := 3
		s.vp.Width = msg.Width
		s.vp.Height = msg.Height - headerHeight
		return s, nil

	case tea.KeyMsg:
		switch msg.String() {
		case "esc", "q":
			if s.cancel != nil {
				s.cancel()
			}
			return s, navPop
		case "r":
			if s.done {
				return s, s.Init()
			}
		}
	}

	var cmd tea.Cmd
	s.vp, cmd = s.vp.Update(msg)
	return s, cmd
}

// View implements Screen.
func (s *logScreen) View() string {
	var b strings.Builder
	b.WriteString(styleTitle.Render("Logs"))
	b.WriteString("\n")
	if s.wireErr != nil {
		b.WriteString(styleError.Render("no disponible") + styleDim.Render(" — "+shortErr(s.wireErr)))
		b.WriteString("\n\n")
		b.WriteString(styleHelp.Render("esc menú"))
		return b.String()
	}
	if len(s.lines) == 0 && !s.done {
		b.WriteString(styleDim.Render("conectando al stream de logs…"))
		b.WriteString("\n")
	} else {
		b.WriteString(s.vp.View())
	}
	b.WriteString("\n")
	if s.done {
		b.WriteString(styleHelp.Render("r re-conectar · esc menú"))
	} else {
		b.WriteString(styleHelp.Render("esc/q detener"))
	}
	return b.String()
}
