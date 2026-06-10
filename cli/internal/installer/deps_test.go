package installer

import (
	"context"
	"errors"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// fakeProbe is a deterministic DockerProbe for tests.
type fakeProbe struct {
	path    string
	pathErr error
	daemon  error
}

func (f fakeProbe) BinaryPath() (string, error)      { return f.path, f.pathErr }
func (f fakeProbe) DaemonUp(_ context.Context) error { return f.daemon }

func TestGoosToOS(t *testing.T) {
	cases := map[string]OS{
		"darwin":  OSMacOS,
		"linux":   OSLinux,
		"windows": OSWindows,
		"plan9":   OSOther,
	}
	for in, want := range cases {
		if got := goosToOS(in); got != want {
			t.Errorf("goosToOS(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestDetectDocker_AllReady(t *testing.T) {
	st := DetectDocker(context.Background(), fakeProbe{path: "/usr/bin/docker"})
	if !st.BinaryPresent || !st.DaemonUp || !st.Ready() {
		t.Errorf("expected fully ready, got %+v", st)
	}
	if st.BinaryPath != "/usr/bin/docker" {
		t.Errorf("BinaryPath = %q", st.BinaryPath)
	}
}

func TestDetectDocker_NoBinary(t *testing.T) {
	st := DetectDocker(context.Background(), fakeProbe{pathErr: errors.New("not found")})
	if st.BinaryPresent {
		t.Error("BinaryPresent should be false")
	}
	if st.DaemonUp {
		t.Error("DaemonUp should be false when binary is absent (probe must not be consulted)")
	}
	if st.Ready() {
		t.Error("Ready should be false")
	}
}

func TestDetectDocker_BinaryButDaemonDown(t *testing.T) {
	st := DetectDocker(context.Background(), fakeProbe{path: "/usr/bin/docker", daemon: errors.New("cannot connect")})
	if !st.BinaryPresent {
		t.Error("BinaryPresent should be true")
	}
	if st.DaemonUp {
		t.Error("DaemonUp should be false when daemon errors")
	}
	if st.Ready() {
		t.Error("Ready should be false when daemon is down")
	}
}

func TestEnsureDockerReady_OKWhenReady(t *testing.T) {
	if err := EnsureDockerReady(context.Background(), fakeProbe{path: "/usr/bin/docker"}); err != nil {
		t.Errorf("expected nil error when docker ready, got %v", err)
	}
}

func TestEnsureDockerReady_DockerUnavailableCode(t *testing.T) {
	err := EnsureDockerReady(context.Background(), fakeProbe{pathErr: errors.New("nope")})
	if err == nil {
		t.Fatal("expected error when docker missing")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected *errs.CLIError, got %T", err)
	}
	if ce.Code != errs.CodeDockerUnavailable {
		t.Errorf("code = %q, want %q", ce.Code, errs.CodeDockerUnavailable)
	}
	if ce.Hint == "" {
		t.Error("expected a non-empty actionable hint")
	}
}

func TestDockerHint_DifferentiatesOSAndState(t *testing.T) {
	missingMac := dockerHint(OSMacOS, DockerStatus{})
	missingLinux := dockerHint(OSLinux, DockerStatus{})
	if missingMac == missingLinux {
		t.Error("macOS and Linux missing-binary hints should differ")
	}
	daemonDownMac := dockerHint(OSMacOS, DockerStatus{BinaryPresent: true})
	if daemonDownMac == missingMac {
		t.Error("daemon-down hint should differ from missing-binary hint")
	}
}
