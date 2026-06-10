package installer

import (
	"bufio"
	"fmt"
	"io"
	"os"
	"strings"

	"golang.org/x/term"
)

// isTerminalFn is a package-level TTY check, swappable in tests (mirrors
// the cli package's confirm.go pattern). Production wiring uses the real
// term.IsTerminal against the given fd.
var isTerminalFn = func(fd int) bool { return term.IsTerminal(fd) }

// IsInteractive reports whether the process is attached to a TTY on stdin.
// `infra install` uses this to decide whether to prompt for the non-secret
// values or fall back to flags/defaults (headless / `curl | bash`-style).
func IsInteractive() bool {
	return isTerminalFn(int(os.Stdin.Fd()))
}

// Prompter reads the non-secret install config from an interactive
// operator. Secrets are NEVER prompted — they are always random (see
// GenSecret). The reader/writer are injected so the prompt loop can be
// unit-tested with strings.Reader / bytes.Buffer.
type Prompter struct {
	In  io.Reader
	Out io.Writer
}

// NewPrompter wires a Prompter to os.Stdin / os.Stdout.
func NewPrompter() *Prompter {
	return &Prompter{In: os.Stdin, Out: os.Stdout}
}

// PromptConfig prompts for the three non-secret values (port, CORS origin,
// DB user), pre-filling each prompt with the current value of c as the
// default. An empty answer keeps the default. The returned EnvConfig has
// every field populated (defaults applied), so it is safe to hand straight
// to RenderEnv.
//
// This mirrors install.sh's interactive block: only port / CORS / db-user
// are asked; Version is not prompted (it comes from the binary's pinned
// tag), and secrets are never asked.
func (p *Prompter) PromptConfig(c EnvConfig) EnvConfig {
	c = c.withDefaults()
	r := bufio.NewReader(p.In)

	c.Port = p.ask(r, "Puerto del API en el host", c.Port)
	c.CorsOrigin = p.ask(r, "Origen CORS del frontend", c.CorsOrigin)
	c.DBUser = p.ask(r, "Usuario propietario de la BD", c.DBUser)
	return c
}

// ask prints "label [def]: " and returns the trimmed answer, or def when
// the answer is empty (or on EOF).
func (p *Prompter) ask(r *bufio.Reader, label, def string) string {
	if p.Out != nil {
		fmt.Fprintf(p.Out, "  %s [%s]: ", label, def)
	}
	line, err := r.ReadString('\n')
	ans := strings.TrimSpace(line)
	if ans == "" || (err != nil && ans == "") {
		return def
	}
	return ans
}
