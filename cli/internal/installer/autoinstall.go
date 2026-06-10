package installer

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/exec"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// DepCommand abstracts running an external command (brew, colima) so the
// auto-install flow can be unit-tested with a fake instead of shelling out.
// The production impl is execCommand. Out/Err receive the child's streams so
// the operator sees brew/colima progress live.
type DepCommand interface {
	// Look reports whether a binary is on PATH (e.g. "brew"). It returns the
	// resolved path or an error when absent.
	Look(name string) (string, error)
	// Run executes name with args, streaming child stdout/stderr to out/errw.
	// It returns a non-nil error on a non-zero exit.
	Run(ctx context.Context, out, errw io.Writer, name string, args ...string) error
}

// execCommand is the production DepCommand: Look uses exec.LookPath and Run
// shells out, wiring the child's stdout/stderr to the provided writers.
type execCommand struct{}

func (execCommand) Look(name string) (string, error) { return exec.LookPath(name) }

func (execCommand) Run(ctx context.Context, out, errw io.Writer, name string, args ...string) error {
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Stdout = out
	cmd.Stderr = errw
	return cmd.Run()
}

// AutoInstallDeps bundles the auto-install flow's injectable seams. Both are
// optional: a nil Cmd uses the production exec-based runner, a nil Probe uses
// the production docker probe, and a nil Progress discards step output.
type AutoInstallDeps struct {
	// Cmd runs brew/colima. nil → production execCommand.
	Cmd DepCommand
	// Probe re-checks docker after an install attempt. nil → production
	// exec docker probe.
	Probe DockerProbe
	// Progress receives human-readable step lines (Stderr in production so it
	// does not pollute the structured stdout payload). nil → io.Discard.
	Progress io.Writer
}

// EnsureDocker is the day-1 docker preflight WITH optional auto-install
// (Fase 4, T-INST-030). It is the richer counterpart to EnsureDockerReady:
//
//   - If docker is already usable (binary + daemon), it returns nil.
//   - If docker is missing/down and autoInstall is false (the default), it
//     returns docker_unavailable with an OS-appropriate hint — detect + guide
//     only, no side effects.
//   - If autoInstall is true:
//   - macOS: if `brew` is present, run
//     `brew install colima docker docker-compose` then `colima start`
//     (design OQ-b = run colima start so the daemon is actually up), then
//     re-probe. If brew is absent, fall back to guidance.
//   - Linux: guidance only (NO sudo/apt auto-run, per the locked decision);
//     returns docker_unavailable pointing at the install docs.
//   - other OS: guidance only.
//
// All external effects go through deps.Cmd / deps.Probe so the flow is fully
// unit-testable with fakes (no real brew/colima/docker on the test host).
func EnsureDocker(ctx context.Context, autoInstall bool, deps AutoInstallDeps) error {
	probe := deps.Probe
	progress := deps.Progress
	if progress == nil {
		progress = io.Discard
	}

	// Fast path: already usable.
	if DetectDocker(ctx, probe).Ready() {
		return nil
	}

	if !autoInstall {
		// Detect + guide only.
		st := DetectDocker(ctx, probe)
		return errs.New(errs.CodeDockerUnavailable, "docker is not available", dockerHint(DetectOS(), st))
	}

	cmd := deps.Cmd
	if cmd == nil {
		cmd = execCommand{}
	}

	switch DetectOS() {
	case OSMacOS:
		return ensureDockerMacOS(ctx, cmd, probe, progress)
	case OSLinux:
		// No sudo/apt auto-run (locked decision). Guide only.
		fmt.Fprintln(progress, "==> Linux: automatic docker install is not performed (it would require sudo).")
		st := DetectDocker(ctx, probe)
		return errs.New(
			errs.CodeDockerUnavailable,
			"docker is not available and Linux auto-install is not supported",
			dockerHint(OSLinux, st),
		)
	default:
		st := DetectDocker(ctx, probe)
		return errs.New(
			errs.CodeDockerUnavailable,
			"docker is not available and auto-install is unsupported on this platform",
			dockerHint(OSOther, st),
		)
	}
}

// ensureDockerMacOS runs the brew/colima auto-install on macOS. It requires
// brew to be present (we never install brew itself — that needs an
// interactive, sudo-y flow we will not automate). After installing Colima +
// docker + compose, it runs `colima start` so the daemon is actually up, then
// re-probes.
func ensureDockerMacOS(ctx context.Context, cmd DepCommand, probe DockerProbe, progress io.Writer) error {
	if _, err := cmd.Look("brew"); err != nil {
		// brew absent → cannot auto-install; guide.
		st := DetectDocker(ctx, probe)
		return errs.New(
			errs.CodeDockerUnavailable,
			"docker is not available and Homebrew is not installed",
			"install Homebrew first (https://brew.sh), then re-run with --auto-install, or install docker manually: "+dockerHint(OSMacOS, st),
		)
	}

	fmt.Fprintln(progress, "==> Installing Colima + docker + docker-compose via Homebrew…")
	if err := cmd.Run(ctx, progress, progress, "brew", "install", "colima", "docker", "docker-compose"); err != nil {
		return errs.Wrap(
			errs.CodeDockerUnavailable, err,
			"`brew install colima docker docker-compose` failed",
			"inspect the brew output above, resolve the issue, and retry; or install docker manually",
		)
	}

	fmt.Fprintln(progress, "==> Starting the Colima VM (`colima start`)…")
	if err := cmd.Run(ctx, progress, progress, "colima", "start"); err != nil {
		return errs.Wrap(
			errs.CodeDockerUnavailable, err,
			"`colima start` failed after installing Colima",
			"run `colima start` manually and re-run install, or check `colima status`",
		)
	}

	// Re-probe: the daemon should now answer. We use a fresh DetectDocker so
	// the post-install state is authoritative (binary now on PATH + daemon up).
	if !DetectDocker(ctx, probe).Ready() {
		st := DetectDocker(ctx, probe)
		return errs.New(
			errs.CodeDockerUnavailable,
			"docker was installed but is still not ready",
			dockerHint(OSMacOS, st),
		)
	}
	fmt.Fprintln(progress, "==> Docker is ready.")
	return nil
}

// ensure os import stays live (defensive for future PATH diagnostics).
var _ = os.Getenv
