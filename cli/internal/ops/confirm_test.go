package ops

import "testing"

// TestConfirmDecision_Table exhausts the TTY × bypass decision table.
// This is the SINGLE source of truth for confirmation semantics: the
// cobra prompt (cli.Confirm) and the TUI modal both consume it, so a
// regression here would desync both frontends at once.
func TestConfirmDecision_Table(t *testing.T) {
	cases := []struct {
		name    string
		yes     bool
		noInput bool
		isTTY   bool
		want    Decision
	}{
		{name: "tty_no_bypass_prompts", isTTY: true, want: DecisionPrompt},
		{name: "tty_yes_proceeds", yes: true, isTTY: true, want: DecisionProceed},
		{name: "tty_no_input_proceeds", noInput: true, isTTY: true, want: DecisionProceed},
		{name: "tty_both_bypasses_proceed", yes: true, noInput: true, isTTY: true, want: DecisionProceed},
		{name: "non_tty_no_bypass_refuses", isTTY: false, want: DecisionRefuse},
		{name: "non_tty_yes_proceeds", yes: true, isTTY: false, want: DecisionProceed},
		{name: "non_tty_no_input_proceeds", noInput: true, isTTY: false, want: DecisionProceed},
		{name: "non_tty_both_bypasses_proceed", yes: true, noInput: true, isTTY: false, want: DecisionProceed},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			opts := Options{Yes: tc.yes, NoInput: tc.noInput}
			if got := ConfirmDecision(opts, tc.isTTY); got != tc.want {
				t.Errorf("ConfirmDecision(yes=%v noInput=%v tty=%v) = %v, want %v",
					tc.yes, tc.noInput, tc.isTTY, got, tc.want)
			}
		})
	}
}

// TestDecision_String guards the diagnostic labels (used in test output
// and potential status-bar badges).
func TestDecision_String(t *testing.T) {
	pairs := map[Decision]string{
		DecisionProceed: "proceed",
		DecisionPrompt:  "prompt",
		DecisionRefuse:  "refuse",
		Decision(99):    "unknown",
	}
	for d, want := range pairs {
		if got := d.String(); got != want {
			t.Errorf("Decision(%d).String() = %q, want %q", d, got, want)
		}
	}
}
