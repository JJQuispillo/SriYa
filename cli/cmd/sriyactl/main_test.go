package main

import (
	"bytes"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/cli"
)

// env builds a getenv stub from a map.
func env(m map[string]string) func(string) string {
	return func(k string) string { return m[k] }
}

// TestShouldLaunchTUI exhausts the activation decision table: bare
// invocation × stdin TTY × stdout TTY × SRIYACTL_NO_TUI.
func TestShouldLaunchTUI(t *testing.T) {
	bare := []string{"sriyactl"}
	withArgs := []string{"sriyactl", "infra", "status"}
	cases := []struct {
		name     string
		args     []string
		inTTY    bool
		outTTY   bool
		noTUIEnv string
		want     bool
	}{
		{"bare_full_tty_launches", bare, true, true, "", true},
		{"args_never_launch", withArgs, true, true, "", false},
		{"piped_stdout_never_launches", bare, true, false, "", false},
		{"piped_stdin_never_launches", bare, false, true, "", false},
		{"no_tty_at_all_never_launches", bare, false, false, "", false},
		{"kill_switch_wins_on_tty", bare, true, true, "1", false},
		{"kill_switch_other_value_ignored", bare, true, true, "0", true},
		{"kill_switch_and_args", withArgs, true, true, "1", false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := shouldLaunchTUI(tc.args, tc.inTTY, tc.outTTY, env(map[string]string{"SRIYACTL_NO_TUI": tc.noTUIEnv}))
			if got != tc.want {
				t.Errorf("shouldLaunchTUI(%v, in=%v, out=%v, NO_TUI=%q) = %v, want %v",
					tc.args, tc.inTTY, tc.outTTY, tc.noTUIEnv, got, tc.want)
			}
		})
	}
}

// TestNonTTYNoArgsPrintsHelp guards the headless contract: when the gate
// does NOT fire (pipe/CI), executing the root command with no args keeps
// emitting cobra's help — byte-identical to the pre-TUI behavior, because
// the cobra path is untouched. We assert the output equals cobra's own
// help rendering for the same command.
func TestNonTTYNoArgsPrintsHelp(t *testing.T) {
	// What cobra itself prints for an explicit --help (the canonical
	// help path, including the auto-added help/completion commands).
	ref := cli.NewRootCmd("test")
	var want bytes.Buffer
	ref.SetOut(&want)
	ref.SetErr(&want)
	ref.SetArgs([]string{"--help"})
	if err := ref.Execute(); err != nil {
		t.Fatalf("--help: %v", err)
	}

	// What an argless Execute() prints (the main.go fallback path).
	cmd := cli.NewRootCmd("test")
	var got bytes.Buffer
	cmd.SetOut(&got)
	cmd.SetErr(&got)
	cmd.SetArgs([]string{})
	if err := cmd.Execute(); err != nil {
		t.Fatalf("Execute() with no args: %v", err)
	}

	if got.String() != want.String() {
		t.Errorf("argless help drifted from cobra help.\n--- got ---\n%s\n--- want ---\n%s", got.String(), want.String())
	}
	if !bytes.Contains(got.Bytes(), []byte("Usage:")) {
		t.Errorf("expected help output to contain Usage:, got: %s", got.String())
	}
	// The new `ui` subcommand must be listed (it is part of the help now,
	// which is fine — the contract is help-on-no-args, not a frozen text).
	if !bytes.Contains(got.Bytes(), []byte("ui")) {
		t.Errorf("expected help to list the ui subcommand, got: %s", got.String())
	}
}
