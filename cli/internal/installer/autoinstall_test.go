package installer

import (
	"bytes"
	"context"
	"errors"
	"io"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// fakeCmd is a deterministic DepCommand. It records every Run invocation and
// can simulate a binary being absent (lookErr per name) or a command failing
// (runErr per name).
type fakeCmd struct {
	present map[string]bool  // name -> on PATH
	runErr  map[string]error // name -> error returned by Run
	runs    [][]string       // recorded [name, args...] in order
}

func (f *fakeCmd) Look(name string) (string, error) {
	if f.present[name] {
		return "/opt/fake/" + name, nil
	}
	return "", errors.New(name + " not found")
}

func (f *fakeCmd) Run(_ context.Context, out, _ io.Writer, name string, args ...string) error {
	rec := append([]string{name}, args...)
	f.runs = append(f.runs, rec)
	if out != nil {
		_, _ = out.Write([]byte(name + " ran\n"))
	}
	if f.runErr != nil {
		return f.runErr[name]
	}
	return nil
}

// withDetectOS forces the OS classification for the duration of a test.
func withDetectOS(t *testing.T, os OS) {
	t.Helper()
	orig := detectOSFn
	detectOSFn = func() OS { return os }
	t.Cleanup(func() { detectOSFn = orig })
}

// -----------------------------------------------------------------------------
// Already-ready: EnsureDocker returns nil and never touches brew/colima.
// -----------------------------------------------------------------------------

func TestEnsureDocker_AlreadyReady(t *testing.T) {
	cmd := &fakeCmd{}
	err := EnsureDocker(context.Background(), true, AutoInstallDeps{
		Cmd:   cmd,
		Probe: fakeProbe{path: "/usr/bin/docker"}, // ready
	})
	if err != nil {
		t.Fatalf("expected nil when docker ready, got %v", err)
	}
	if len(cmd.runs) != 0 {
		t.Errorf("no brew/colima commands expected when docker is ready; got %v", cmd.runs)
	}
}

// -----------------------------------------------------------------------------
// Default (no --auto-install): detect + guide only, docker_unavailable, no cmds.
// -----------------------------------------------------------------------------

func TestEnsureDocker_DefaultGuideOnly(t *testing.T) {
	withDetectOS(t, OSMacOS)
	cmd := &fakeCmd{present: map[string]bool{"brew": true}}
	err := EnsureDocker(context.Background(), false, AutoInstallDeps{
		Cmd:   cmd,
		Probe: fakeProbe{pathErr: errors.New("absent")}, // docker missing
	})
	if err == nil {
		t.Fatal("expected docker_unavailable without --auto-install")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeDockerUnavailable {
		t.Fatalf("expected docker_unavailable, got %v", err)
	}
	if len(cmd.runs) != 0 {
		t.Errorf("guide-only must not run brew/colima; got %v", cmd.runs)
	}
}

// -----------------------------------------------------------------------------
// macOS + brew + --auto-install: runs brew install then colima start, then the
// re-probe reports ready → nil.
// -----------------------------------------------------------------------------

func TestEnsureDocker_MacOSAutoInstall_Success(t *testing.T) {
	withDetectOS(t, OSMacOS)
	cmd := &fakeCmd{present: map[string]bool{"brew": true}}

	// Probe: docker absent on the first probe (before install), ready after.
	calls := 0
	probe := scriptProbe(func() DockerStatus {
		calls++
		if calls == 1 {
			return DockerStatus{} // not ready → triggers install
		}
		return DockerStatus{BinaryPresent: true, DaemonUp: true}
	})

	var progress bytes.Buffer
	err := EnsureDocker(context.Background(), true, AutoInstallDeps{
		Cmd:      cmd,
		Probe:    probe,
		Progress: &progress,
	})
	if err != nil {
		t.Fatalf("expected success after brew+colima, got %v", err)
	}
	if len(cmd.runs) != 2 {
		t.Fatalf("expected exactly 2 commands (brew install, colima start), got %v", cmd.runs)
	}
	if got := cmd.runs[0]; got[0] != "brew" || got[1] != "install" {
		t.Errorf("first cmd: got %v want `brew install colima docker docker-compose`", got)
	}
	// Verify the full brew install arg set.
	wantBrew := []string{"brew", "install", "colima", "docker", "docker-compose"}
	if !equalStrs(cmd.runs[0], wantBrew) {
		t.Errorf("brew args: got %v want %v", cmd.runs[0], wantBrew)
	}
	if got := cmd.runs[1]; got[0] != "colima" || got[1] != "start" {
		t.Errorf("second cmd: got %v want `colima start`", got)
	}
	if progress.Len() == 0 {
		t.Error("expected progress output to be written")
	}
}

// -----------------------------------------------------------------------------
// macOS without brew + --auto-install: cannot auto-install → docker_unavailable
// with a hint about installing Homebrew; no commands run.
// -----------------------------------------------------------------------------

func TestEnsureDocker_MacOSNoBrew(t *testing.T) {
	withDetectOS(t, OSMacOS)
	cmd := &fakeCmd{present: map[string]bool{}} // brew absent
	err := EnsureDocker(context.Background(), true, AutoInstallDeps{
		Cmd:   cmd,
		Probe: fakeProbe{pathErr: errors.New("absent")},
	})
	if err == nil {
		t.Fatal("expected docker_unavailable when brew is absent")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeDockerUnavailable {
		t.Fatalf("expected docker_unavailable, got %v", err)
	}
	if len(cmd.runs) != 0 {
		t.Errorf("no commands should run when brew is absent; got %v", cmd.runs)
	}
}

// -----------------------------------------------------------------------------
// macOS + brew but `brew install` fails: error is surfaced, colima never runs.
// -----------------------------------------------------------------------------

func TestEnsureDocker_MacOSBrewInstallFails(t *testing.T) {
	withDetectOS(t, OSMacOS)
	cmd := &fakeCmd{
		present: map[string]bool{"brew": true},
		runErr:  map[string]error{"brew": errors.New("brew exploded")},
	}
	err := EnsureDocker(context.Background(), true, AutoInstallDeps{
		Cmd:   cmd,
		Probe: fakeProbe{pathErr: errors.New("absent")},
	})
	if err == nil {
		t.Fatal("expected error when brew install fails")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeDockerUnavailable {
		t.Fatalf("expected docker_unavailable wrapping the brew failure, got %v", err)
	}
	// colima start must NOT have run after brew install failed.
	for _, r := range cmd.runs {
		if r[0] == "colima" {
			t.Error("colima start must not run after brew install fails")
		}
	}
}

// -----------------------------------------------------------------------------
// Linux + --auto-install: NO sudo/apt auto-run; guide-only, docker_unavailable,
// no commands.
// -----------------------------------------------------------------------------

func TestEnsureDocker_LinuxGuideOnly(t *testing.T) {
	withDetectOS(t, OSLinux)
	cmd := &fakeCmd{present: map[string]bool{"apt": true}}
	err := EnsureDocker(context.Background(), true, AutoInstallDeps{
		Cmd:   cmd,
		Probe: fakeProbe{pathErr: errors.New("absent")},
	})
	if err == nil {
		t.Fatal("expected docker_unavailable on Linux (guide-only)")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeDockerUnavailable {
		t.Fatalf("expected docker_unavailable, got %v", err)
	}
	if len(cmd.runs) != 0 {
		t.Errorf("Linux auto-install must NOT shell out (no sudo/apt); got %v", cmd.runs)
	}
}

// -----------------------------------------------------------------------------
// macOS + brew + install succeeds but the daemon never comes up: re-probe still
// not ready → docker_unavailable (we don't silently claim success).
// -----------------------------------------------------------------------------

func TestEnsureDocker_MacOSStillNotReadyAfterInstall(t *testing.T) {
	withDetectOS(t, OSMacOS)
	cmd := &fakeCmd{present: map[string]bool{"brew": true}}
	// Always not-ready (install ran but daemon never answers).
	probe := fakeProbe{path: "/usr/bin/docker", daemon: errors.New("still down")}
	err := EnsureDocker(context.Background(), true, AutoInstallDeps{Cmd: cmd, Probe: probe})
	if err == nil {
		t.Fatal("expected docker_unavailable when still not ready after install")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) || ce.Code != errs.CodeDockerUnavailable {
		t.Fatalf("expected docker_unavailable, got %v", err)
	}
	// Both install steps ran (we attempted the full flow).
	if len(cmd.runs) != 2 {
		t.Errorf("expected brew install + colima start to both run; got %v", cmd.runs)
	}
}

// scriptProbeImpl lets a closure drive DetectDocker's observations so a test
// can return a different status on each probe (e.g. not-ready then ready).
type scriptProbeImpl struct{ next func() DockerStatus }

func (s scriptProbeImpl) BinaryPath() (string, error) {
	if s.next == nil {
		return "", errors.New("absent")
	}
	st := s.next()
	if st.BinaryPresent {
		return "/usr/bin/docker", nil
	}
	return "", errors.New("absent")
}

// DaemonUp is consulted by DetectDocker only when BinaryPath succeeded; we
// cannot re-pull the status here without advancing the script, so we report
// "up" and let BinaryPath drive readiness. To model "binary present, daemon
// down" use fakeProbe instead.
func (s scriptProbeImpl) DaemonUp(context.Context) error { return nil }

// scriptProbe builds a DockerProbe whose readiness is driven by next(). Each
// call to DetectDocker advances the script once (via BinaryPath).
func scriptProbe(next func() DockerStatus) DockerProbe { return scriptProbeImpl{next: next} }

func equalStrs(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}
