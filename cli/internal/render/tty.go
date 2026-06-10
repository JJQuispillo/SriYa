package render

import (
	"os"

	"golang.org/x/term"
)

// IsTerminal reports whether the given file descriptor is a terminal. We
// rely on golang.org/x/term because it works correctly across all three
// supported platforms (macOS, Linux, Windows) including the common
// CI pitfall where stdout is a pipe but stderr is a TTY.
func IsTerminal(fd int) bool {
	return term.IsTerminal(fd)
}

// AutoFormat returns the default format based on the TTY state of stdout.
// Per ai-contract REQ-AUTO-NONTTY: when stdout is NOT a TTY, default to
// JSON; when it IS a TTY, default to table. The caller can override by
// passing an explicit --output value (which takes precedence in cli/root.go).
func AutoFormat() Format {
	if IsTerminal(int(os.Stdout.Fd())) {
		return FormatTable
	}
	return FormatJSON
}
