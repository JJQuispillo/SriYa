package installer

import (
	"context"
	"os/exec"
	"runtime"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// OS is a coarse classification of the host platform, enough to drive the
// install/guidance flow (`--auto-install` differs macOS vs Linux, and
// Windows is unsupported by the stack).
type OS string

const (
	OSMacOS   OS = "macos"
	OSLinux   OS = "linux"
	OSWindows OS = "windows"
	OSOther   OS = "other"
)

// goosToOS maps runtime.GOOS to our coarse OS classification. Exposed
// separately from DetectOS so it can be tested as a pure function (no
// dependency on the build platform).
func goosToOS(goos string) OS {
	switch goos {
	case "darwin":
		return OSMacOS
	case "linux":
		return OSLinux
	case "windows":
		return OSWindows
	default:
		return OSOther
	}
}

// detectOSFn resolves the host OS classification. It is a package-level var
// so the auto-install flow (autoinstall.go) can be unit-tested on either
// branch (macOS brew/colima vs Linux guide-only) regardless of the build
// platform. Production wiring leaves it as the runtime.GOOS mapping.
var detectOSFn = func() OS { return goosToOS(runtime.GOOS) }

// DetectOS returns the host OS classification.
func DetectOS() OS { return detectOSFn() }

// DockerProbe abstracts the two external observations DetectDocker makes —
// "is the docker binary on PATH?" and "does the daemon answer?" — so the
// detector can be unit-tested with a fake instead of requiring a real
// docker install. The production impl is execDockerProbe.
type DockerProbe interface {
	// BinaryPath returns the resolved path to the docker binary, or an
	// error if it is not on PATH.
	BinaryPath() (string, error)
	// DaemonUp reports whether the docker daemon answers (e.g. via
	// `docker info`). It returns nil when the daemon is reachable.
	DaemonUp(ctx context.Context) error
}

// DockerStatus is the result of DetectDocker.
type DockerStatus struct {
	BinaryPresent bool   `json:"binaryPresent" yaml:"binaryPresent"`
	BinaryPath    string `json:"binaryPath,omitempty" yaml:"binaryPath,omitempty"`
	DaemonUp      bool   `json:"daemonUp" yaml:"daemonUp"`
}

// Ready reports whether docker is fully usable (binary present AND daemon
// reachable).
func (s DockerStatus) Ready() bool { return s.BinaryPresent && s.DaemonUp }

// DetectDocker probes the docker binary and daemon via the given probe and
// returns a DockerStatus. It NEVER returns an error for a "not installed"
// or "daemon down" condition — those are normal observations encoded in the
// returned status. The caller (preflight) decides whether that is fatal.
//
// Passing a nil probe uses the production exec-based probe.
func DetectDocker(ctx context.Context, p DockerProbe) DockerStatus {
	if p == nil {
		p = execDockerProbe{}
	}
	var st DockerStatus
	if path, err := p.BinaryPath(); err == nil {
		st.BinaryPresent = true
		st.BinaryPath = path
	}
	if st.BinaryPresent {
		if err := p.DaemonUp(ctx); err == nil {
			st.DaemonUp = true
		}
	}
	return st
}

// EnsureDockerReady is the preflight gate used before provisioning. It runs
// DetectDocker and, if docker is not ready, returns a CLIError with the
// stable `docker_unavailable` code and an OS-appropriate hint. Auto-install
// (the `--auto-install` flow that runs `brew install colima …`) is NOT
// implemented here — that lands in Fase 4 (T-INST-030). This function only
// detects and guides.
func EnsureDockerReady(ctx context.Context, p DockerProbe) error {
	st := DetectDocker(ctx, p)
	if st.Ready() {
		return nil
	}
	hint := dockerHint(DetectOS(), st)
	return errs.New(errs.CodeDockerUnavailable, "docker is not available", hint)
}

// dockerHint returns an OS-appropriate, actionable hint for a docker that
// is missing or whose daemon is down.
func dockerHint(os OS, st DockerStatus) string {
	if !st.BinaryPresent {
		switch os {
		case OSMacOS:
			return "install Docker: `brew install colima docker docker-compose` (or re-run with --auto-install), then `colima start`"
		case OSLinux:
			return "install Docker Engine + Compose v2: https://docs.docker.com/engine/install/"
		default:
			return "install Docker: https://docs.docker.com/get-docker/"
		}
	}
	// Binary present but daemon down.
	switch os {
	case OSMacOS:
		return "start the docker daemon (`colima start` or open Docker Desktop) and retry"
	default:
		return "start the docker daemon (e.g. `sudo systemctl start docker`) and retry"
	}
}

// execDockerProbe is the production DockerProbe. BinaryPath uses
// exec.LookPath; DaemonUp shells out to `docker info`.
type execDockerProbe struct{}

func (execDockerProbe) BinaryPath() (string, error) {
	return exec.LookPath("docker")
}

func (execDockerProbe) DaemonUp(ctx context.Context) error {
	// `docker info` returns non-zero when the daemon is unreachable. We
	// discard output; we only care about the exit status.
	cmd := exec.CommandContext(ctx, "docker", "info")
	return cmd.Run()
}
