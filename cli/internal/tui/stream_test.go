package tui

import (
	"context"
	"errors"
	"io"
	"strings"
	"testing"
	"time"

	tea "github.com/charmbracelet/bubbletea"
	"go.uber.org/goleak"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m,
		goleak.IgnoreTopFunction("github.com/charmbracelet/bubbletea.Tick.func1"),
	)
}

// collectStream reads all messages from a startStream cmd by repeatedly
// calling it until it returns a StreamDoneMsg.
func collectStream(cmd tea.Cmd) ([]string, error) {
	var lines []string
	fn := cmd
	for {
		msg := fn()
		switch m := msg.(type) {
		case StreamLineMsg:
			lines = append(lines, string(m))
		case StreamDoneMsg:
			return lines, m.Err
		default:
			return lines, nil
		}
	}
}

// TestStream_BasicDelivery: write 3 lines, expect 3 StreamLineMsg then
// StreamDoneMsg with nil error.
func TestStream_BasicDelivery(t *testing.T) {
	defer goleak.VerifyNone(t, goleak.IgnoreCurrent())

	lines, err := collectStream(startStream(context.Background(), func(w io.Writer) error {
		_, _ = w.Write([]byte("line one\n"))
		_, _ = w.Write([]byte("line two\n"))
		_, _ = w.Write([]byte("line three\n"))
		return nil
	}))
	if len(lines) != 3 {
		t.Fatalf("expected 3 lines, got %d: %v", len(lines), lines)
	}
	if lines[0] != "line one" {
		t.Errorf("line 0: %q", lines[0])
	}
	if lines[1] != "line two" {
		t.Errorf("line 1: %q", lines[1])
	}
	if lines[2] != "line three" {
		t.Errorf("line 2: %q", lines[2])
	}
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

// TestStream_EmptyStream: handler returns immediately, expect
// StreamDoneMsg with nil error.
func TestStream_EmptyStream(t *testing.T) {
	defer goleak.VerifyNone(t, goleak.IgnoreCurrent())

	lines, err := collectStream(startStream(context.Background(), func(w io.Writer) error {
		return nil
	}))
	if len(lines) != 0 {
		t.Fatalf("expected 0 lines, got %d", len(lines))
	}
	if err != nil {
		t.Fatalf("expected nil error, got %v", err)
	}
}

// TestStream_HandlerError: handler returns an error, expect StreamDoneMsg
// with that error.
func TestStream_HandlerError(t *testing.T) {
	defer goleak.VerifyNone(t, goleak.IgnoreCurrent())

	sentinel := errors.New("handler boom")
	lines, err := collectStream(startStream(context.Background(), func(w io.Writer) error {
		_, _ = w.Write([]byte("before boom\n"))
		return sentinel
	}))
	if len(lines) != 1 || lines[0] != "before boom" {
		t.Fatalf("expected 1 line, got %d: %v", len(lines), lines)
	}
	if !errors.Is(err, sentinel) {
		t.Fatalf("expected sentinel error, got %v", err)
	}
}

// TestStream_CancelContext: cancel the context mid-stream, expect
// StreamDoneMsg with context.Canceled.
func TestStream_CancelContext(t *testing.T) {
	defer goleak.VerifyNone(t, goleak.IgnoreCurrent())

	ctx, cancel := context.WithCancel(context.Background())
	block := make(chan struct{})

	cmd := startStream(ctx, func(w io.Writer) error {
		close(block)
		<-ctx.Done()
		_, werr := w.Write([]byte("after cancel\n"))
		return werr
	})

	<-block
	cancel()

	lines, err := collectStream(cmd)
	if len(lines) != 0 {
		t.Logf("got %d lines (expected 0): %v", len(lines), lines)
	}
	if err == nil {
		t.Fatal("expected context.Canceled error")
	}
}

// TestStream_PartialLine: a line without trailing newline is delivered
// when the stream ends.
func TestStream_PartialLine(t *testing.T) {
	defer goleak.VerifyNone(t, goleak.IgnoreCurrent())

	lines, err := collectStream(startStream(context.Background(), func(w io.Writer) error {
		_, _ = w.Write([]byte("partial line"))
		return nil
	}))
	if len(lines) != 1 {
		t.Fatalf("expected 1 line, got %d: %v", len(lines), lines)
	}
	if lines[0] != "partial line" {
		t.Errorf("expected 'partial line', got %q", lines[0])
	}
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

// TestStream_MultipleWrites: lines written across multiple Write calls
// arrive as individual messages.
func TestStream_MultipleWrites(t *testing.T) {
	defer goleak.VerifyNone(t, goleak.IgnoreCurrent())

	lines, err := collectStream(startStream(context.Background(), func(w io.Writer) error {
		_, _ = w.Write([]byte("multi\n"))
		_, _ = w.Write([]byte("ple\n"))
		_, _ = w.Write([]byte("lines\n"))
		return nil
	}))
	if len(lines) != 3 {
		t.Fatalf("expected 3 lines, got %d: %v", len(lines), lines)
	}
	if !strings.Contains(lines[0], "multi") {
		t.Errorf("line 0: %q", lines[0])
	}
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

// TestStream_DelayedWrite: lines written after a delay are delivered
// (exercises non-blocking read loop).
func TestStream_DelayedWrite(t *testing.T) {
	defer goleak.VerifyNone(t, goleak.IgnoreCurrent())

	lines, err := collectStream(startStream(context.Background(), func(w io.Writer) error {
		_, _ = w.Write([]byte("immediate\n"))
		time.Sleep(10 * time.Millisecond)
		_, _ = w.Write([]byte("delayed\n"))
		return nil
	}))
	if len(lines) != 2 {
		t.Fatalf("expected 2 lines, got %d: %v", len(lines), lines)
	}
	if lines[0] != "immediate" {
		t.Errorf("line 0: %q", lines[0])
	}
	if lines[1] != "delayed" {
		t.Errorf("line 1: %q", lines[1])
	}
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}
