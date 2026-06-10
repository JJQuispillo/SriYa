// Package compose is the safe wrapper around `docker compose` used by
// the infra commands. It shells out to the system binary rather than
// linking the Docker SDK, matching install.sh semantics and avoiding
// daemon-API version drift.
package compose

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// EnvHomeOverride is the env var that overrides the install dir.
const EnvHomeOverride = "SRIYACTL_HOME"

// Runner is the interface handlers depend on. The production impl is
// ExecRunner; tests use FakeRunner from runner_fake_test.go (in the same
// package, only built under test).
type Runner interface {
	// Run executes a compose command, captures stdout/stderr, and returns
	// a Result. Returns a CLIError with code install_dir_invalid when
	// the install dir is missing or malformed.
	Run(ctx context.Context, args ...string) (Result, error)
	// Stream executes a compose command and streams its combined output
	// to w in real time. Used by `infra logs -f`.
	Stream(ctx context.Context, w io.Writer, args ...string) error
	// RunTo executes a compose command and streams the child process
	// stdout directly to w (binary-safe). Stderr is captured to a buffer
	// and surfaced as part of the error if the command fails. Used by
	// `infra backup` to stream pg_dump without buffering it in memory.
	RunTo(ctx context.Context, w io.Writer, args ...string) error
	// InstallDir returns the resolved install dir (useful for diagnostics
	// and for `infra doctor` to print).
	InstallDir() string
	// ValidateInstallDir ensures both .env and docker-compose.yml are
	// present; returns a CLIError with code install_dir_invalid otherwise.
	ValidateInstallDir() error
}

// Result captures stdout/stderr/exit from a single compose invocation.
type Result struct {
	Stdout   string
	Stderr   string
	ExitCode int
}

// ServiceStatus is a single row in `compose ps` JSON output.
type ServiceStatus struct {
	Name    string `json:"name"    yaml:"name"`
	State   string `json:"state"   yaml:"state"`
	Health  string `json:"health"  yaml:"health"`
	Image   string `json:"image"   yaml:"image"`
	Service string `json:"service" yaml:"service"`
}

// ExecRunner is the production Runner. It shells out to `docker compose`.
type ExecRunner struct {
	Dir         string        // install dir; resolved from --dir / SRIYACTL_HOME / auto-detect
	ComposeBin  string        // path to docker binary (default "docker")
	EnvFile     string        // .env filename (default ".env")
	ComposeFile string        // compose filename (default "docker-compose.yml")
	Timeout     time.Duration // per-invocation timeout (default 5m)
}

// NewExecRunner resolves the install dir using the precedence:
//
//	--dir (passed by CLI) > SRIYACTL_HOME > $HOME/sriya (or $HOME/qora) > ./.
//
// The fallback is intentional: it lets a developer run the CLI from a
// checkout without setting env vars.
func NewExecRunner(override string) (*ExecRunner, error) {
	dir, err := resolveInstallDir(override)
	if err != nil {
		return nil, err
	}
	return &ExecRunner{
		Dir:         dir,
		ComposeBin:  "docker",
		EnvFile:     ".env",
		ComposeFile: "docker-compose.yml",
		Timeout:     5 * time.Minute,
	}, nil
}

// resolveInstallDir applies the precedence chain.
func resolveInstallDir(override string) (string, error) {
	candidates := []string{}
	if override != "" {
		candidates = append(candidates, override)
	}
	if h := os.Getenv(EnvHomeOverride); h != "" {
		candidates = append(candidates, h)
	}
	if home, err := os.UserHomeDir(); err == nil {
		candidates = append(candidates, filepath.Join(home, "sriya"))
		candidates = append(candidates, filepath.Join(home, "qora"))
	}
	if cwd, err := os.Getwd(); err == nil {
		candidates = append(candidates, cwd)
	}
	for _, c := range candidates {
		if isInstallDir(c) {
			abs, err := filepath.Abs(c)
			if err != nil {
				continue
			}
			return abs, nil
		}
	}
	return "", errs.New(
		errs.CodeInstallDirInvalid,
		"no valid install dir found (looked for .env + docker-compose.yml)",
		"pass --dir <path> or set SRIYACTL_HOME=<path>",
	)
}

func isInstallDir(dir string) bool {
	if dir == "" {
		return false
	}
	st, err := os.Stat(dir)
	if err != nil || !st.IsDir() {
		return false
	}
	_, errEnv := os.Stat(filepath.Join(dir, ".env"))
	_, errYml := os.Stat(filepath.Join(dir, "docker-compose.yml"))
	return errEnv == nil && errYml == nil
}

// InstallDir implements Runner.
func (e *ExecRunner) InstallDir() string { return e.Dir }

// ValidateInstallDir implements Runner.
func (e *ExecRunner) ValidateInstallDir() error {
	if e.Dir == "" {
		return errs.New(errs.CodeInstallDirInvalid, "no install dir resolved", "pass --dir or set SRIYACTL_HOME")
	}
	if !isInstallDir(e.Dir) {
		return errs.New(
			errs.CodeInstallDirInvalid,
			"install dir is missing .env or docker-compose.yml: "+e.Dir,
			"point to the directory produced by install.sh",
		)
	}
	return nil
}

// Run implements Runner.
func (e *ExecRunner) Run(ctx context.Context, args ...string) (Result, error) {
	if err := e.ValidateInstallDir(); err != nil {
		return Result{}, err
	}
	ctx, cancel := context.WithTimeout(ctx, e.Timeout)
	defer cancel()

	cmd := exec.CommandContext(ctx, e.ComposeBin, append([]string{"compose"}, args...)...)
	cmd.Dir = e.Dir
	cmd.Env = append(os.Environ(),
		// Compose picks these up automatically when in the install dir,
		// but we set them explicitly so the binary finds .env even if
		// the working dir is overridden.
		"COMPOSE_ENV_FILE="+filepath.Join(e.Dir, e.EnvFile),
	)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	err := cmd.Run()
	res := Result{
		Stdout:   stdout.String(),
		Stderr:   stderr.String(),
		ExitCode: 0,
	}
	var ee *exec.ExitError
	if errors.As(err, &ee) {
		res.ExitCode = ee.ExitCode()
		return res, errs.New(
			errs.CodeGeneric,
			fmt.Sprintf("compose %s failed (exit %d): %s", strings.Join(args, " "), res.ExitCode, strings.TrimSpace(res.Stderr)),
			"inspect the compose output above and retry",
		)
	}
	if err != nil {
		return res, errs.Wrap(errs.CodeDockerUnavailable, err, "docker compose invocation failed", "is the docker daemon running?")
	}
	return res, nil
}

// Stream implements Runner. Combined stdout+stderr are written to w.
func (e *ExecRunner) Stream(ctx context.Context, w io.Writer, args ...string) error {
	if err := e.ValidateInstallDir(); err != nil {
		return err
	}
	cmd := exec.CommandContext(ctx, e.ComposeBin, append([]string{"compose"}, args...)...)
	cmd.Dir = e.Dir
	cmd.Stdout = w
	cmd.Stderr = w
	if err := cmd.Run(); err != nil {
		var ee *exec.ExitError
		if errors.As(err, &ee) {
			return errs.New(
				errs.CodeGeneric,
				fmt.Sprintf("compose %s exited %d", strings.Join(args, " "), ee.ExitCode()),
				"check the streamed output",
			)
		}
		return errs.Wrap(errs.CodeDockerUnavailable, err, "docker compose invocation failed", "is the docker daemon running?")
	}
	return nil
}

// RunTo implements Runner. Stdout is streamed directly to w (binary-safe;
// no intermediate string/buffer), and stderr is captured to an internal
// buffer that is included in the returned error message on failure. Use
// this for commands whose output is large or binary (pg_dump, pg_restore,
// archive operations) where buffering would corrupt data or balloon
// memory.
func (e *ExecRunner) RunTo(ctx context.Context, w io.Writer, args ...string) error {
	if err := e.ValidateInstallDir(); err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(ctx, e.Timeout)
	defer cancel()

	cmd := exec.CommandContext(ctx, e.ComposeBin, append([]string{"compose"}, args...)...)
	cmd.Dir = e.Dir
	cmd.Env = append(os.Environ(),
		"COMPOSE_ENV_FILE="+filepath.Join(e.Dir, e.EnvFile),
	)
	cmd.Stdout = w
	var stderr bytes.Buffer
	cmd.Stderr = &stderr

	err := cmd.Run()
	if err == nil {
		return nil
	}
	var ee *exec.ExitError
	if errors.As(err, &ee) {
		return errs.New(
			errs.CodeGeneric,
			fmt.Sprintf("compose %s exited %d: %s", strings.Join(args, " "), ee.ExitCode(), strings.TrimSpace(stderr.String())),
			"inspect the compose output above and retry",
		)
	}
	return errs.Wrap(errs.CodeDockerUnavailable, err, "docker compose invocation failed", "is the docker daemon running?")
}
