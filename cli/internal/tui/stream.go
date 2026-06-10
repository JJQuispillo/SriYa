package tui

import (
	"bufio"
	"context"
	"io"

	tea "github.com/charmbracelet/bubbletea"
)

// StreamLineMsg is one line of a live stream (compose logs, install
// progress). It is emitted by startStream for each line read from the
// handler's output pipe.
type StreamLineMsg string

// StreamDoneMsg signals the end of a stream. Err is nil on a clean
// close; non-nil when the handler returned an error or the context was
// cancelled.
type StreamDoneMsg struct {
	Err error
}

// startStream launches a streaming handler in a goroutine connected via
// io.Pipe. It returns a tea.Cmd that delivers StreamLineMsg per line and
// a final StreamDoneMsg when the handler finishes.
func startStream(ctx context.Context, run func(io.Writer) error) tea.Cmd {
	pr, pw := io.Pipe()
	ctx, cancel := context.WithCancel(ctx)

	// resultCh carries the error from the handler goroutine.
	resultCh := make(chan error, 1)

	go func() {
		err := run(pw)
		pw.Close()
		resultCh <- err
	}()

	go func() {
		<-ctx.Done()
		pw.Close()
	}()

	scanner := bufio.NewScanner(pr)
	return func() tea.Msg {
		if scanner.Scan() {
			return StreamLineMsg(scanner.Text())
		}
		defer cancel()
		err := scanner.Err()
		if err == nil {
			select {
			case <-ctx.Done():
				err = ctx.Err()
			case err = <-resultCh:
			default:
			}
		}
		return StreamDoneMsg{Err: err}
	}
}
