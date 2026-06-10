package installer

import (
	"bytes"
	"strings"
	"testing"
)

func TestPromptConfig_UsesAnswers(t *testing.T) {
	// Three answers: port, cors, db-user.
	in := strings.NewReader("9090\nhttps://app.example.com\nowner\n")
	var out bytes.Buffer
	p := &Prompter{In: in, Out: &out}

	cfg := p.PromptConfig(EnvConfig{})
	if cfg.Port != "9090" {
		t.Errorf("Port = %q, want 9090", cfg.Port)
	}
	if cfg.CorsOrigin != "https://app.example.com" {
		t.Errorf("CorsOrigin = %q", cfg.CorsOrigin)
	}
	if cfg.DBUser != "owner" {
		t.Errorf("DBUser = %q, want owner", cfg.DBUser)
	}
	// Prompts must have echoed the defaults in brackets.
	if !strings.Contains(out.String(), "[8080]") {
		t.Errorf("expected default port prompt, got %q", out.String())
	}
}

func TestPromptConfig_EmptyAnswerKeepsDefault(t *testing.T) {
	// All three answers blank → keep defaults.
	in := strings.NewReader("\n\n\n")
	var out bytes.Buffer
	p := &Prompter{In: in, Out: &out}

	cfg := p.PromptConfig(EnvConfig{})
	if cfg.Port != defaultPort {
		t.Errorf("Port = %q, want default %q", cfg.Port, defaultPort)
	}
	if cfg.CorsOrigin != defaultCorsOrigin {
		t.Errorf("CorsOrigin = %q, want default", cfg.CorsOrigin)
	}
	if cfg.DBUser != defaultDBUser {
		t.Errorf("DBUser = %q, want default", cfg.DBUser)
	}
}

func TestPromptConfig_PrefillsProvidedDefaults(t *testing.T) {
	// Pre-set values become the prompt defaults; blank answers keep them.
	in := strings.NewReader("\n\n\n")
	var out bytes.Buffer
	p := &Prompter{In: in, Out: &out}

	cfg := p.PromptConfig(EnvConfig{Port: "7000", DBUser: "pre"})
	if cfg.Port != "7000" {
		t.Errorf("Port = %q, want pre-filled 7000", cfg.Port)
	}
	if cfg.DBUser != "pre" {
		t.Errorf("DBUser = %q, want pre", cfg.DBUser)
	}
	if !strings.Contains(out.String(), "[7000]") {
		t.Errorf("expected pre-filled default in prompt, got %q", out.String())
	}
}

func TestPromptConfig_EOFKeepsDefaults(t *testing.T) {
	// Reader exhausts before all three prompts (simulated Ctrl-D): the
	// remaining prompts must fall back to defaults without panicking.
	in := strings.NewReader("9090")
	var out bytes.Buffer
	p := &Prompter{In: in, Out: &out}

	cfg := p.PromptConfig(EnvConfig{})
	if cfg.Port != "9090" {
		t.Errorf("Port = %q, want 9090", cfg.Port)
	}
	if cfg.CorsOrigin != defaultCorsOrigin {
		t.Errorf("CorsOrigin = %q, want default after EOF", cfg.CorsOrigin)
	}
	if cfg.DBUser != defaultDBUser {
		t.Errorf("DBUser = %q, want default after EOF", cfg.DBUser)
	}
}

func TestIsInteractive_NonTTY(t *testing.T) {
	// Override the TTY check to be deterministic.
	orig := isTerminalFn
	defer func() { isTerminalFn = orig }()
	isTerminalFn = func(int) bool { return false }
	if IsInteractive() {
		t.Error("IsInteractive should be false when isTerminalFn returns false")
	}
	isTerminalFn = func(int) bool { return true }
	if !IsInteractive() {
		t.Error("IsInteractive should be true when isTerminalFn returns true")
	}
}
