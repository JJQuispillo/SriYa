package ops

// Decision is the outcome of the destructive-op confirmation decision
// table (TTY × bypass). It is PURE — no I/O — so both frontends can share
// it: the cobra layer turns Prompt into a bufio y/N prompt, the TUI turns
// it into a modal. Refuse maps to confirmation_required (exit 2) in both.
//
// Decision table:
//
//	| env       | --yes/--no-input | decision |
//	|-----------|------------------|----------|
//	| TTY       | no               | Prompt   |
//	| TTY       | yes              | Proceed  |
//	| non-TTY   | no               | Refuse   |
//	| non-TTY   | yes              | Proceed  |
type Decision int

const (
	// DecisionProceed: explicit non-interactive intent (--yes/--no-input).
	// The action runs without asking.
	DecisionProceed Decision = iota
	// DecisionPrompt: interactive terminal, no bypass. The frontend MUST
	// ask the operator (y/N, default N) before running the action.
	DecisionPrompt
	// DecisionRefuse: non-interactive without bypass. The frontend MUST
	// refuse with confirmation_required and never run the action.
	DecisionRefuse
)

// String implements fmt.Stringer for test/diagnostic output.
func (d Decision) String() string {
	switch d {
	case DecisionProceed:
		return "proceed"
	case DecisionPrompt:
		return "prompt"
	case DecisionRefuse:
		return "refuse"
	}
	return "unknown"
}

// ConfirmDecision evaluates the confirmation decision table for a
// destructive operation. isTTY reports whether the operator is attached
// to an interactive terminal (the caller decides how to detect it; the
// cobra layer probes os.Stdin, the TUI is interactive by construction).
func ConfirmDecision(opts Options, isTTY bool) Decision {
	if opts.Yes || opts.NoInput {
		return DecisionProceed
	}
	if !isTTY {
		return DecisionRefuse
	}
	return DecisionPrompt
}
