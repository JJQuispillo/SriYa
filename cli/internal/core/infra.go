package core

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/compose"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/installer"
)

// Stack naming constants for the real SriYa Postgres service. These match
// docker-compose.prod.yml + the rendered .env (installer.EnvKeys):
//
//   - dbService  = the compose service name of Postgres.
//   - dbName     = the application database.
//   - defaultDBUser = the owner/bootstrap role when BILLING_DB_USER is unset
//     (mirrors installer.defaultDBUser; kept here so the core package does
//     not import an unexported installer symbol).
//
// F1 (sriyactl-installer, design OQ-a): runBackup previously hardcoded the
// nonexistent `postgres` / `-U postgres billing`, which broke every backup
// (and the pre-upgrade mandate). The user is READ from .env, never
// hardcoded, so a `--db-user` custom install still backs up.
const (
	dbService     = "billing-db"
	dbName        = "qora_billing"
	defaultDBUser = "billing_user"
)

// InfraDeps bundles infra handler dependencies. Health checks go through
// the api.Client; lifecycle goes through compose.Runner.
type InfraDeps struct {
	API     api.Client
	Compose compose.Runner
	// Probe is the docker binary/daemon probe used by the PRE-install
	// doctor path (InfraDoctorRequest.PreInstall). It is optional: a nil
	// Probe falls back to the production exec-based probe inside the
	// installer package. It exists as a seam so the pre-install checks can
	// be unit-tested without a real docker binary, and so they can run
	// BEFORE an install dir exists (the post-install daemon check still
	// goes through compose.Runner, which requires the dir).
	Probe installer.DockerProbe
}

// InfraStatusResult aggregates compose ps, /health, /health/ready, and
// the resolved image tag.
type InfraStatusResult struct {
	InstallDir string            `json:"installDir" yaml:"installDir"`
	ImageTag   string            `json:"imageTag"   yaml:"imageTag"`
	Services   []InfraServiceRow `json:"services"   yaml:"services"`
	Health     *api.Health       `json:"health,omitempty" yaml:"health,omitempty"`
	Ready      *api.Health       `json:"ready,omitempty"  yaml:"ready,omitempty"`
	Degraded   bool              `json:"degraded"   yaml:"degraded"`
}

// InfraServiceRow is a single row of `infra status`.
type InfraServiceRow struct {
	Name    string `json:"name"    yaml:"name"`
	State   string `json:"state"   yaml:"state"`
	Health  string `json:"health"  yaml:"health"`
	Service string `json:"service" yaml:"service"`
}

// InfraStatusHandler implements core.Handler. Aggregates compose ps +
// /health + /health/ready + .env BILLING_IMAGE_TAG.
//
// Verified contract (HealthEndpoints.cs:20-49): liveness is GET /health
// (returns 200 {status:"Healthy"}) and readiness is GET /health/ready
// (returns 200 {status:"Ready"} on success, 503 on DB outage). The
// previous v1 implementation called /health twice and fabricated
// readiness; the fix is to call /health/ready explicitly. Status
// values are PascalCase (the previous "ok" comparison is gone).
func InfraStatusHandler(d InfraDeps) Handler[struct{}, InfraStatusResult] {
	return func(ctx context.Context, _ struct{}) (Output[InfraStatusResult], error) {
		out := InfraStatusResult{InstallDir: d.Compose.InstallDir()}

		// Read .env for BILLING_IMAGE_TAG. We do this via the compose
		// helper to keep fs access in one place (compose package owns
		// the install dir).
		tag, _ := readEnvVar(d.Compose.InstallDir(), "BILLING_IMAGE_TAG")
		out.ImageTag = tag

		// `docker compose ps --format json` returns one JSON object per
		// line in compose v2. We tolerate the older array shape too.
		res, err := d.Compose.Run(ctx, "ps", "--format", "json")
		if err != nil {
			return Output[InfraStatusResult]{}, err
		}
		out.Services = parseComposePS(res.Stdout)

		// Liveness probe. A failure to reach /health is reported as
		// degraded (we surface the health field as nil).
		h, herr := d.API.Health(ctx)
		if herr == nil {
			out.Health = &h
		}
		// Readiness probe (separate endpoint). A 503 / network failure
		// here is NOT fatal — we mark degraded and let the operator see
		// the status of both probes. The CLI middleware will emit the
		// payload (table/JSON) AND the degraded sentinel thanks to
		// MarkRenderable (design §#4).
		r, rerr := d.API.Ready(ctx)
		if rerr == nil {
			out.Ready = &r
		}

		// Degraded = any service not running OR liveness != "Healthy"
		// OR readiness != "Ready" OR readiness 503 (out.Ready == nil
		// while the liveness call succeeded).
		for _, s := range out.Services {
			if s.State != "running" {
				out.Degraded = true
				break
			}
		}
		if out.Health == nil || out.Health.Status != "Healthy" {
			out.Degraded = true
		}
		if out.Ready == nil || out.Ready.Status != "Ready" {
			out.Degraded = true
		}

		if out.Degraded {
			return NewOutput("InfraStatus", out), errs.New(
				errs.CodeNetwork,
				"infra is degraded",
				"check `sriyactl infra logs` and `sriyactl infra doctor`",
			).MarkRenderable()
		}
		return NewOutput("InfraStatus", out), nil
	}
}

var composePSLine = regexp.MustCompile(`[\{\[].*[\}\]]`)

func parseComposePS(stdout string) []InfraServiceRow {
	var rows []InfraServiceRow
	for _, ln := range strings.Split(stdout, "\n") {
		ln = strings.TrimSpace(ln)
		if ln == "" {
			continue
		}
		// Try parsing as a single JSON object (newer compose), or as
		// part of a JSON array. We do a lightweight scan to keep the
		// dep-free shape: each non-empty line that starts with `{` is
		// a self-contained object in compose v2's default output.
		if !strings.HasPrefix(ln, "{") {
			continue
		}
		var s struct {
			Name    string `json:"Name"`
			State   string `json:"State"`
			Health  string `json:"Health"`
			Service string `json:"Service"`
		}
		if err := jsonUnmarshal(ln, &s); err == nil && s.Name != "" {
			rows = append(rows, InfraServiceRow{
				Name:    s.Name,
				State:   s.State,
				Health:  s.Health,
				Service: s.Service,
			})
		}
	}
	_ = composePSLine // reserved for future use
	return rows
}

// InfraLogsRequest is the input to InfraLogsHandler.
type InfraLogsRequest struct {
	Follow  bool
	Service string
}

// InfraLogsHandler streams compose logs. It is special-cased in cli: it
// does NOT produce a structured Output — it streams to stdout. The
// handler returns nil and the cli layer manages the streaming directly.
func InfraLogsHandler(d InfraDeps) func(ctx context.Context, in InfraLogsRequest, w writerLike) error {
	return func(ctx context.Context, in InfraLogsRequest, w writerLike) error {
		args := []string{"logs"}
		if in.Follow {
			args = append(args, "-f")
		}
		if in.Service != "" {
			args = append(args, in.Service)
		}
		return d.Compose.Stream(ctx, w, args...)
	}
}

// writerLike is the minimal interface we need from the cobra writer.
// Avoids importing io in the public signature.
type writerLike interface {
	Write(p []byte) (int, error)
}

// upgradeHealthPollInterval is the wait interval between `/health/ready`
// probes during `infra upgrade`. Exposed as a var so unit tests can
// shrink it (default 5s would make the success path slow).
var upgradeHealthPollInterval = 5 * time.Second

// InfraUpgradeRequest is the input to InfraUpgradeHandler.
type InfraUpgradeRequest struct {
	TargetTag string
	Timeout   time.Duration
}

// InfraUpgradeResult is the success payload.
type InfraUpgradeResult struct {
	PreviousTag string `json:"previousTag" yaml:"previousTag"`
	NewTag      string `json:"newTag"      yaml:"newTag"`
	WaitedMs    int64  `json:"waitedMs"    yaml:"waitedMs"`
	RolledBack  bool   `json:"rolledBack"  yaml:"rolledBack"`
	BackupPath  string `json:"backupPath,omitempty" yaml:"backupPath,omitempty"`
}

// InfraUpgradeHandler implements core.Handler. Migration-aware flow:
// GuardMutation → validate --to → (Confirm gate) → backup → bump tag
// → pull → up -d → wait /health/ready → return.
//
// Order is critical (sriyactl-v1-fixes, design §#7 + proposal
// finding #7): the backup MUST run BEFORE we mutate .env. If the
// backup fails, the upgrade aborts with no side effect, and the
// operator can fix the underlying issue (e.g. postgres not running)
// and retry.
//
// The Confirm gate is wired by the cli layer via SharedFlags.RequiresConfirm
// (design §#1) — this handler trusts that destructive intent has
// already been confirmed and focuses on the actual flow.
//
// On /health/ready timeout, restore the previous tag in .env and fail
// with CodeUpgradeTimeout.
func InfraUpgradeHandler(d InfraDeps) Handler[InfraUpgradeRequest, InfraUpgradeResult] {
	return func(ctx context.Context, in InfraUpgradeRequest) (Output[InfraUpgradeResult], error) {
		if err := GuardMutation(ctx); err != nil {
			return Output[InfraUpgradeResult]{}, err
		}
		if in.TargetTag == "" {
			return Output[InfraUpgradeResult]{}, errs.New(errs.CodeUsage, "missing --to <tag>", "pass the target image tag")
		}
		prev, _ := readEnvVar(d.Compose.InstallDir(), "BILLING_IMAGE_TAG")
		if IsDryRun(ctx) {
			// Plan only — no .env mutation, no pull, no up.
			_ = Plan{Action: "infra.upgrade", Target: in.TargetTag, Details: map[string]any{"previousTag": prev}}
			return NewOutput("InfraUpgrade", InfraUpgradeResult{PreviousTag: prev, NewTag: in.TargetTag}), nil
		}

		// Step 1: backup. If the backup fails, we abort the upgrade
		// WITHOUT mutating .env. This is the pre-upgrade mandate
		// (design §#7): every upgrade must be backed up first.
		backupPath, backupErr := runBackup(ctx, d)
		if backupErr != nil {
			// Preserve the original error code (e.g. db_unavailable)
			// so CI / agents can branch on it; the upgrade-level
			// message + hint frame the failure in the upgrade
			// context.
			code := errs.CodeGeneric
			var ce *errs.CLIError
			if errors.As(backupErr, &ce) {
				code = ce.Code
			}
			return Output[InfraUpgradeResult]{}, errs.Wrap(
				code, backupErr,
				"backup failed; aborting upgrade before mutating .env",
				"check postgres is running (`sriyactl infra status`) and disk has space, then retry",
			)
		}

		// Step 2: write the new tag.
		if err := writeEnvVar(d.Compose.InstallDir(), "BILLING_IMAGE_TAG", in.TargetTag); err != nil {
			return Output[InfraUpgradeResult]{}, errs.Wrap(errs.CodeGeneric, err, "write new tag to .env", "")
		}
		// Step 3: pull + up -d. On any error, roll back the .env tag.
		if _, err := d.Compose.Run(ctx, "pull"); err != nil {
			_ = writeEnvVar(d.Compose.InstallDir(), "BILLING_IMAGE_TAG", prev)
			return Output[InfraUpgradeResult]{}, errs.Wrap(errs.CodeGeneric, err, "compose pull failed; previous tag restored in .env", "inspect the compose output and retry")
		}
		if _, err := d.Compose.Run(ctx, "up", "-d"); err != nil {
			_ = writeEnvVar(d.Compose.InstallDir(), "BILLING_IMAGE_TAG", prev)
			return Output[InfraUpgradeResult]{}, errs.Wrap(errs.CodeGeneric, err, "compose up -d failed; previous tag restored in .env", "inspect the compose output and retry")
		}
		// Step 4: wait for /health/ready.
		timeout := in.Timeout
		if timeout == 0 {
			timeout = 5 * time.Minute
		}
		waitCtx, cancel := context.WithTimeout(ctx, timeout)
		defer cancel()
		started := time.Now()
		for {
			select {
			case <-waitCtx.Done():
				_ = writeEnvVar(d.Compose.InstallDir(), "BILLING_IMAGE_TAG", prev)
				return NewOutput("InfraUpgrade", InfraUpgradeResult{
						PreviousTag: prev, NewTag: in.TargetTag, RolledBack: true,
						BackupPath: backupPath,
					}), errs.New(
						errs.CodeUpgradeTimeout,
						fmt.Sprintf("health never recovered within %s", timeout),
						"check `sriyactl infra logs` and `sriyactl infra doctor`; the previous tag has been restored in .env",
					)
			case <-time.After(upgradeHealthPollInterval):
			}
			r, err := d.API.Ready(waitCtx)
			if err == nil && r.Status == "Ready" {
				return NewOutput("InfraUpgrade", InfraUpgradeResult{
					PreviousTag: prev, NewTag: in.TargetTag,
					WaitedMs:   time.Since(started).Milliseconds(),
					BackupPath: backupPath,
				}), nil
			}
		}
	}
}

// InfraBackupResult is the success payload.
type InfraBackupResult struct {
	Path      string `json:"path"     yaml:"path"`
	SizeBytes int64  `json:"sizeBytes" yaml:"sizeBytes"`
}

// InfraBackupHandler runs `pg_dump` via compose exec and reports the
// artifact path. The actual dump is STREAMED directly to a file in the
// install dir via compose.RunTo (binary-safe; no in-memory buffer). On
// mid-stream failure, the partial file is removed before the error is
// propagated (design §#8).
func InfraBackupHandler(d InfraDeps) Handler[struct{}, InfraBackupResult] {
	return func(ctx context.Context, _ struct{}) (Output[InfraBackupResult], error) {
		fullPath, err := runBackup(ctx, d)
		if err != nil {
			return Output[InfraBackupResult]{}, err
		}
		info, _ := fileInfo(fullPath)
		var size int64
		if info != nil {
			size = info.Size()
		}
		return NewOutput("InfraBackup", InfraBackupResult{Path: fullPath, SizeBytes: size}), nil
	}
}

// runBackup is the shared core of both InfraBackupHandler (user-facing)
// and InfraUpgradeHandler (pre-upgrade mandate, design §#7). It
// returns the path of the freshly written dump file, or an error. The
// file is streamed via RunTo (no in-memory buffering) and removed on
// mid-stream failure so a partial dump never lingers on disk.
func runBackup(ctx context.Context, d InfraDeps) (string, error) {
	// Step A: ensure the DB is up: `compose ps` and look for a service in
	// state=running that looks like the DB. The real stack names the
	// Postgres service `billing-db` (NOT `postgres`) — F1 fix
	// (sriyactl-installer, design OQ-a): the previous `postgres` literal
	// never matched, so the running-check always reported db_unavailable on
	// a healthy stack.
	ps, err := d.Compose.Run(ctx, "ps", "--format", "json")
	if err != nil {
		return "", errs.Wrap(errs.CodeGeneric, err, "compose ps failed; cannot confirm the database is up", "")
	}
	if !hasRunningServiceNamed(ps.Stdout, dbService) {
		return "", errs.New(
			errs.CodeDBUnavailable,
			"the "+dbService+" service is not running",
			"start the stack with `docker compose up -d` and retry",
		)
	}
	// Step B: open the destination file. We use the install dir as the
	// landing zone so a relative path is meaningful to the operator
	// (and so the default doctor / status prints a sensible path).
	ts := time.Now().UTC().Format("20060102T150405Z")
	backupName := fmt.Sprintf("sriya-backup-%s.sql", ts)
	fullPath := filepath.Join(d.Compose.InstallDir(), backupName)
	f, err := os.OpenFile(fullPath, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600)
	if err != nil {
		return "", errs.Wrap(errs.CodeGeneric, err, "open backup file", "check disk space and permissions on the install dir")
	}
	// Step C: stream pg_dump directly to the file. RunTo writes the
	// child's stdout to f as it's produced; large/binary dumps never
	// sit in memory.
	//
	// F1 fix (design OQ-a = read from .env, do NOT hardcode the user): the
	// real stack uses service `billing-db`, role `billing_user` (read from
	// BILLING_DB_USER so a custom --db-user install still backs up), and DB
	// `qora_billing`. The previous `-U postgres billing` targeted names that
	// do not exist in the stack, so pg_dump always failed.
	dbUser, _ := readEnvVar(d.Compose.InstallDir(), "BILLING_DB_USER")
	if dbUser == "" {
		dbUser = defaultDBUser
	}
	cmd := []string{"exec", "-T", dbService, "pg_dump", "-U", dbUser, dbName}
	if rerr := d.Compose.RunTo(ctx, f, cmd...); rerr != nil {
		// Mid-stream failure: close + remove the partial file before
		// propagating the error so a corrupt dump never lingers on
		// disk (design §#8). Closing is best-effort; the remove is
		// what guarantees the invariant.
		_ = f.Close()
		if rerr2 := os.Remove(fullPath); rerr2 != nil && !errors.Is(rerr2, os.ErrNotExist) {
			// Surface the secondary failure in a wrapping hint so the
			// operator can clean up manually if needed.
			return "", errs.Wrap(errs.CodeGeneric, rerr, "backup mid-stream failure; partial file could not be removed", rerr2.Error())
		}
		return "", errs.Wrap(errs.CodeGeneric, rerr, "pg_dump failed mid-stream; partial file removed", "check docker compose logs and disk space, then retry")
	}
	if cerr := f.Close(); cerr != nil {
		// pg_dump exited 0 but the final flush/close failed. Treat as
		// a hard error and clean up so we never report a corrupt file
		// as success.
		_ = os.Remove(fullPath)
		return "", errs.Wrap(errs.CodeGeneric, cerr, "close backup file", "check disk space and permissions")
	}
	return fullPath, nil
}

// InfraRestoreRequest is the input to InfraRestoreHandler.
type InfraRestoreRequest struct {
	Path string
}

// InfraRestoreResult is the success payload.
type InfraRestoreResult struct {
	Path     string `json:"path"      yaml:"path"`
	Restored bool   `json:"restored"  yaml:"restored"`
}

// InfraRestoreHandler is destructive: it requires --yes or non-TTY
// (enforced at the cli layer via SharedFlags.RequiresConfirm +
// RunHandler's Confirm gate, design §#1). The handler itself trusts
// that confirmation has happened and focuses on the file-not-found
// and dry-run cases.
func InfraRestoreHandler(d InfraDeps) Handler[InfraRestoreRequest, InfraRestoreResult] {
	return func(ctx context.Context, in InfraRestoreRequest) (Output[InfraRestoreResult], error) {
		if err := GuardMutation(ctx); err != nil {
			return Output[InfraRestoreResult]{}, err
		}
		if in.Path == "" {
			return Output[InfraRestoreResult]{}, errs.New(errs.CodeUsage, "missing dump file path", "pass the file as the first argument")
		}
		if !fileExists(in.Path) {
			return Output[InfraRestoreResult]{}, errs.New(errs.CodeNotFound, "dump file not found: "+in.Path, "check the path")
		}
		if IsDryRun(ctx) {
			_ = Plan{Action: "infra.restore", Target: in.Path}
			return NewOutput("InfraRestore", InfraRestoreResult{Path: in.Path, Restored: false}), nil
		}
		// Stream the file into `compose exec -T postgres psql`.
		// Compose.Run captures stdout; for input we need a different
		// path. We shell out via a small helper.
		if err := restoreViaStdin(d, in.Path); err != nil {
			return Output[InfraRestoreResult]{}, err
		}
		return NewOutput("InfraRestore", InfraRestoreResult{Path: in.Path, Restored: true}), nil
	}
}

// InfraDoctorResult is the success payload.
type InfraDoctorResult struct {
	Checks []InfraDoctorCheck `json:"checks" yaml:"checks"`
}

// InfraDoctorCheck is one row of `infra doctor`.
type InfraDoctorCheck struct {
	Name   string `json:"name"   yaml:"name"`
	Status string `json:"status" yaml:"status"` // pass | warn | fail
	Hint   string `json:"hint,omitempty" yaml:"hint,omitempty"`
}

// InfraDoctorRequest is the input to InfraDoctorHandler.
//
// PreInstall splits the preflight into the subset that is meaningful
// BEFORE the stack is provisioned (sriyactl-installer, design §#5, key
// finding F6). When true, the doctor runs ONLY the docker binary +
// daemon checks via the installer.DockerProbe seam — it does NOT require
// an install dir, an .env, or compose to exist (they do not yet). When
// false (the default, the user-facing `infra doctor`), it runs the full
// post-install check set: docker binary/daemon (via compose), install
// dir, .env keys, ENCRYPTION_KEY length, and service-token guidance.
type InfraDoctorRequest struct {
	// PreInstall selects the pre-provisioning subset of checks. It is set
	// by InfraInstallHandler before it has created the install dir; the
	// user-facing `infra doctor` command leaves it false.
	PreInstall bool
}

// InfraDoctorHandler runs preflight checks. See InfraDoctorRequest for the
// PreInstall vs post-install split.
func InfraDoctorHandler(d InfraDeps) Handler[InfraDoctorRequest, InfraDoctorResult] {
	return func(ctx context.Context, in InfraDoctorRequest) (Output[InfraDoctorResult], error) {
		if in.PreInstall {
			return runPreInstallDoctor(ctx, d)
		}
		return runPostInstallDoctor(ctx, d)
	}
}

// runPreInstallDoctor runs only the docker binary + daemon checks, with NO
// dependency on an install dir / .env / compose file (they do not exist
// yet during `infra install`). It probes docker via the installer
// DockerProbe seam (InfraDeps.Probe; nil → production exec probe).
func runPreInstallDoctor(ctx context.Context, d InfraDeps) (Output[InfraDoctorResult], error) {
	var checks []InfraDoctorCheck
	anyFail := false

	st := installer.DetectDocker(ctx, d.Probe)
	if st.BinaryPresent {
		checks = append(checks, InfraDoctorCheck{Name: "docker-binary", Status: "pass"})
	} else {
		checks = append(checks, InfraDoctorCheck{Name: "docker-binary", Status: "fail", Hint: "install Docker Engine + Compose v2 (macOS: `brew install colima docker docker-compose`)"})
		anyFail = true
	}
	if st.DaemonUp {
		checks = append(checks, InfraDoctorCheck{Name: "docker-daemon", Status: "pass"})
	} else {
		checks = append(checks, InfraDoctorCheck{Name: "docker-daemon", Status: "fail", Hint: "start the Docker daemon (e.g. `colima start`, `open -a Docker`) and retry"})
		anyFail = true
	}

	res := InfraDoctorResult{Checks: checks}
	if anyFail {
		return NewOutput("InfraDoctor", res), errs.New(
			errs.CodeDoctorCheckFailed,
			"one or more preflight checks failed",
			"see the checks above for actionable hints",
		).MarkRenderable()
	}
	return NewOutput("InfraDoctor", res), nil
}

// runPostInstallDoctor is the full preflight check set for an already
// provisioned install dir (the user-facing `infra doctor`).
func runPostInstallDoctor(ctx context.Context, d InfraDeps) (Output[InfraDoctorResult], error) {
	{
		var checks []InfraDoctorCheck
		anyFail := false

		// 1. docker present
		if _, err := lookPath("docker"); err != nil {
			checks = append(checks, InfraDoctorCheck{Name: "docker-binary", Status: "fail", Hint: "install Docker Engine + Compose v2"})
			anyFail = true
		} else {
			checks = append(checks, InfraDoctorCheck{Name: "docker-binary", Status: "pass"})
		}
		// 2. docker daemon reachable
		if _, err := d.Compose.Run(ctx, "ps"); err != nil {
			checks = append(checks, InfraDoctorCheck{Name: "docker-daemon", Status: "fail", Hint: "start the Docker daemon (e.g. `colima start`, `open -a Docker`) and retry"})
			anyFail = true
		} else {
			checks = append(checks, InfraDoctorCheck{Name: "docker-daemon", Status: "pass"})
		}
		// 3. install dir
		if err := d.Compose.ValidateInstallDir(); err != nil {
			var ce *errs.CLIError
			if errors.As(err, &ce) && ce.Code == errs.CodeInstallDirInvalid {
				checks = append(checks, InfraDoctorCheck{Name: "install-dir", Status: "fail", Hint: ce.Hint})
				anyFail = true
			}
		} else {
			checks = append(checks, InfraDoctorCheck{Name: "install-dir", Status: "pass"})
		}
		// 4. .env keys
		// F2 fix (sriyactl-installer, key finding F2): the installer writes
		// BILLING_DB_PASSWORD, NOT POSTGRES_PASSWORD. The previous check
		// looked for POSTGRES_PASSWORD (absent from the real .env) and so
		// reported env-keys:fail on an otherwise-valid stack.
		required := []string{"BILLING_IMAGE_TAG", "BILLING_DB_PASSWORD", "ENCRYPTION_KEY"}
		missing := []string{}
		for _, k := range required {
			if v, _ := readEnvVar(d.Compose.InstallDir(), k); v == "" {
				missing = append(missing, k)
			}
		}
		if len(missing) > 0 {
			checks = append(checks, InfraDoctorCheck{
				Name:   "env-keys",
				Status: "fail",
				Hint:   fmt.Sprintf("missing or empty keys in .env: %s", strings.Join(missing, ", ")),
			})
			anyFail = true
		} else {
			checks = append(checks, InfraDoctorCheck{Name: "env-keys", Status: "pass"})
		}
		// 5. ENCRYPTION_KEY length
		enc, _ := readEnvVar(d.Compose.InstallDir(), "ENCRYPTION_KEY")
		if len(enc) < 32 {
			checks = append(checks, InfraDoctorCheck{
				Name:   "encryption-key-len",
				Status: "fail",
				Hint:   "ENCRYPTION_KEY must be >= 32 chars (regenerate with `openssl rand -base64 32`)",
			})
			anyFail = true
		} else {
			checks = append(checks, InfraDoctorCheck{Name: "encryption-key-len", Status: "pass"})
		}
		// 6. service token configured (env or keychain)
		// (We don't deref the keychain here; we just check the env var
		// or whether the .env has a hint.)
		checks = append(checks, InfraDoctorCheck{Name: "service-token", Status: "warn", Hint: "ensure SRIYACTL_SERVICE_TOKEN or the keychain entry is set before invoking tenant/cert commands"})

		res := InfraDoctorResult{Checks: checks}
		if anyFail {
			// Mark the error renderable so RunHandler prints the checks
			// table/JSON to stdout before emitting the error envelope to
			// stderr (same pattern as cert/infra-status sentinels,
			// design §#4). Without this the operator only sees the error
			// envelope and never learns which checks failed.
			return NewOutput("InfraDoctor", res), errs.New(
				errs.CodeDoctorCheckFailed,
				"one or more preflight checks failed",
				"see the checks above for actionable hints",
			).MarkRenderable()
		}
		return NewOutput("InfraDoctor", res), nil
	}
}

// EnsureUnused keeps the io package import live in case future
// streaming helpers are added here. The RunTo path lives in
// compose.Runner and infra_helpers.go.
var _ = io.Discard
