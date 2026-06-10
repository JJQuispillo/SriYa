package core

import (
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/compose"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// fakeInfraAPI is a controllable api.Client for infra handler tests.
// All non-InfraStatus methods are unused by the infra handlers but the
// interface requires them. The Health and Ready methods are split into
// their own function fields so each test can drive the responses
// independently. healthHits / readyHits are counters for assertions
// about "called /health vs /health/ready exactly once".
type fakeInfraAPI struct {
	healthFn   func(ctx context.Context) (api.Health, error)
	readyFn    func(ctx context.Context) (api.Health, error)
	healthHits int32
	readyHits  int32
}

func (f *fakeInfraAPI) Health(ctx context.Context) (api.Health, error) {
	atomic.AddInt32(&f.healthHits, 1)
	if f.healthFn != nil {
		return f.healthFn(ctx)
	}
	// Default to the verified contract: "Healthy" PascalCase. The
	// previous v1 default was "ok", which is fabricated and would
	// fail every status comparison in the new code path.
	return api.Health{Status: "Healthy"}, nil
}

func (f *fakeInfraAPI) Ready(ctx context.Context) (api.Health, error) {
	atomic.AddInt32(&f.readyHits, 1)
	if f.readyFn != nil {
		return f.readyFn(ctx)
	}
	return api.Health{Status: "Ready"}, nil
}

func (f *fakeInfraAPI) BootstrapTenant(context.Context, api.BootstrapRequest) (api.BootstrapResponse, error) {
	return api.BootstrapResponse{}, nil
}

func (f *fakeInfraAPI) CertStatus(context.Context, string) ([]api.Certificate, error) {
	return nil, nil
}

// fakeComposeRunner is an in-memory compose.Runner. It points at a temp
// install dir so the handler's filesystem helpers (readEnvVar,
// writeEnvVar) work against a real .env file the test set up. Run/Stream
// return canned output and record their invocations. RunTo records
// the bytes streamed to it (so we can assert the streaming path
// produced a binary-safe write, not a buffered string).
type fakeComposeRunner struct {
	installDir string

	runFn    func(args ...string) (compose.Result, error)
	streamFn func(w io.Writer, args ...string) error
	runToFn  func(w io.Writer, args ...string) error

	runCalls    [][]string
	streamCalls [][]string
	runToCalls  [][]string
}

func (f *fakeComposeRunner) Run(ctx context.Context, args ...string) (compose.Result, error) {
	f.runCalls = append(f.runCalls, append([]string(nil), args...))
	if f.runFn != nil {
		return f.runFn(args...)
	}
	return compose.Result{}, nil
}

func (f *fakeComposeRunner) Stream(ctx context.Context, w io.Writer, args ...string) error {
	f.streamCalls = append(f.streamCalls, append([]string(nil), args...))
	if f.streamFn != nil {
		return f.streamFn(w, args...)
	}
	_, _ = w.Write([]byte("streamed\n"))
	return nil
}

func (f *fakeComposeRunner) RunTo(ctx context.Context, w io.Writer, args ...string) error {
	f.runToCalls = append(f.runToCalls, append([]string(nil), args...))
	if f.runToFn != nil {
		return f.runToFn(w, args...)
	}
	_, _ = w.Write([]byte("default-streamed\n"))
	return nil
}

func (f *fakeComposeRunner) InstallDir() string { return f.installDir }

func (f *fakeComposeRunner) ValidateInstallDir() error {
	if f.installDir == "" {
		return errs.New(errs.CodeInstallDirInvalid, "no install dir", "pass --dir or set SRIYACTL_HOME")
	}
	for _, name := range []string{".env", "docker-compose.yml"} {
		if _, err := os.Stat(filepath.Join(f.installDir, name)); err != nil {
			return errs.New(errs.CodeInstallDirInvalid, "missing "+name, "point to the directory produced by install.sh")
		}
	}
	return nil
}

// makeInstallDir creates a temp dir with a .env and a docker-compose.yml
// so it satisfies both the handler's readEnvVar and the fake's
// ValidateInstallDir.
func makeInstallDir(t *testing.T, envContent string) string {
	t.Helper()
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, ".env"), []byte(envContent), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "docker-compose.yml"), []byte("services: {}\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	return dir
}

// withPollInterval shrinks upgradeHealthPollInterval for the duration of
// the test and restores the original value via t.Cleanup. The production
// default is 5s; tests use 5ms for fast feedback.
func withPollInterval(t *testing.T, d time.Duration) {
	t.Helper()
	orig := upgradeHealthPollInterval
	upgradeHealthPollInterval = d
	t.Cleanup(func() { upgradeHealthPollInterval = orig })
}

// withLookPath swaps lookPath for the duration of the test so the
// `docker` binary check in `infra doctor` is deterministic regardless of
// the host environment.
func withLookPath(t *testing.T, fn func(string) (string, error)) {
	t.Helper()
	orig := lookPath
	lookPath = fn
	t.Cleanup(func() { lookPath = orig })
}

// withRestoreViaStdin swaps restoreViaStdin for the duration of the test
// so `infra restore` (confirmed path) can be exercised without a real
// docker daemon.
func withRestoreViaStdin(t *testing.T, fn func(InfraDeps, string) error) {
	t.Helper()
	orig := restoreViaStdin
	restoreViaStdin = fn
	t.Cleanup(func() { restoreViaStdin = orig })
}

// -----------------------------------------------------------------------------
// 1. infra status — healthy: liveness=Healthy, readiness=Ready, distinct
//    endpoints, no degraded flag.
// -----------------------------------------------------------------------------

func TestInfra_Status_Healthy(t *testing.T) {
	dir := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\n")
	fc := &fakeComposeRunner{
		installDir: dir,
		runFn: func(args ...string) (compose.Result, error) {
			if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
				return compose.Result{Stdout: `{"Name":"sriya-billing-1","State":"running","Health":"healthy","Service":"billing"}` + "\n"}, nil
			}
			return compose.Result{}, nil
		},
	}
	a := &fakeInfraAPI{
		healthFn: func(_ context.Context) (api.Health, error) {
			return api.Health{Status: "Healthy"}, nil
		},
		readyFn: func(_ context.Context) (api.Health, error) {
			return api.Health{Status: "Ready"}, nil
		},
	}
	h := InfraStatusHandler(InfraDeps{API: a, Compose: fc})
	out, err := h(context.Background(), struct{}{})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Kind != "InfraStatus" {
		t.Errorf("kind: got %q want InfraStatus", out.Kind)
	}
	if out.Data.Degraded {
		t.Errorf("expected not degraded, got: %+v", out.Data)
	}
	if out.Data.ImageTag != "v1.0.0" {
		t.Errorf("imageTag: got %q want v1.0.0", out.Data.ImageTag)
	}
	if len(out.Data.Services) != 1 || out.Data.Services[0].Name != "sriya-billing-1" {
		t.Errorf("services: %+v", out.Data.Services)
	}
	// Real contract: liveness=Healthy AND readiness=Ready.
	if out.Data.Health == nil || out.Data.Health.Status != "Healthy" {
		t.Errorf("health: %+v", out.Data.Health)
	}
	if out.Data.Ready == nil || out.Data.Ready.Status != "Ready" {
		t.Errorf("ready: %+v", out.Data.Ready)
	}
	// CRITICAL: the previous bug was calling /health TWICE. We must
	// hit /health once AND /health/ready once (distinct endpoints).
	if a.healthHits != 1 {
		t.Errorf("expected /health called once, got %d", a.healthHits)
	}
	if a.readyHits != 1 {
		t.Errorf("expected /health/ready called once, got %d", a.readyHits)
	}
	if errs.ExitCode(err) != 0 {
		t.Errorf("expected exit 0 for healthy stack, got %d", errs.ExitCode(err))
	}
}

// -----------------------------------------------------------------------------
// 2. infra status — degraded: readiness 503 (DB down). Liveness still
//    up, but Ready returns CLIError(db_unavailable). The handler must
//    still emit the payload (renderable sentinel) and a non-zero exit.
// -----------------------------------------------------------------------------

func TestInfra_Status_ReadyDegraded(t *testing.T) {
	dir := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\n")
	fc := &fakeComposeRunner{
		installDir: dir,
		runFn: func(args ...string) (compose.Result, error) {
			if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
				return compose.Result{Stdout: `{"Name":"sriya-billing-1","State":"running","Health":"healthy","Service":"billing"}` + "\n"}, nil
			}
			return compose.Result{}, nil
		},
	}
	a := &fakeInfraAPI{
		healthFn: func(_ context.Context) (api.Health, error) {
			return api.Health{Status: "Healthy"}, nil
		},
		readyFn: func(_ context.Context) (api.Health, error) {
			// Simulate the 503 → db_unavailable path returned by the
			// api.Ready wrapper.
			return api.Health{}, errs.New(errs.CodeDBUnavailable, "readiness 503", "the database is not reachable").MarkRetryable()
		},
	}
	h := InfraStatusHandler(InfraDeps{API: a, Compose: fc})
	out, err := h(context.Background(), struct{}{})
	if err == nil {
		t.Fatal("expected error for degraded readiness")
	}
	if !out.Data.Degraded {
		t.Error("expected Degraded=true when /health/ready is 503")
	}
	// Even though readiness is down, liveness up, the payload is
	// still emitted (renderable sentinel).
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if !ce.Renderable() {
		t.Error("expected the degraded sentinel to be Renderable (design §#4)")
	}
	if errs.ExitCode(err) == 0 {
		t.Error("expected non-zero exit code for degraded stack")
	}
	// Service row is still present.
	if len(out.Data.Services) != 1 || out.Data.Services[0].State != "running" {
		t.Errorf("services: %+v", out.Data.Services)
	}
}

// -----------------------------------------------------------------------------
// 3. infra status — degraded: liveness down. Distinct from the
//    readiness-down case but the same degraded handling.
// -----------------------------------------------------------------------------

func TestInfra_Status_Degraded(t *testing.T) {
	dir := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\n")
	fc := &fakeComposeRunner{
		installDir: dir,
		runFn: func(args ...string) (compose.Result, error) {
			if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
				return compose.Result{Stdout: `{"Name":"sriya-billing-1","State":"exited","Health":"","Service":"billing"}` + "\n"}, nil
			}
			return compose.Result{}, nil
		},
	}
	a := &fakeInfraAPI{
		healthFn: func(_ context.Context) (api.Health, error) {
			return api.Health{Status: "down"}, nil
		},
		readyFn: func(_ context.Context) (api.Health, error) {
			return api.Health{Status: "Ready"}, nil
		},
	}
	h := InfraStatusHandler(InfraDeps{API: a, Compose: fc})
	out, err := h(context.Background(), struct{}{})
	if err == nil {
		t.Fatal("expected error for degraded stack")
	}
	if !out.Data.Degraded {
		t.Error("expected Degraded=true")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeNetwork {
		t.Errorf("expected code=network, got %s", ce.Code)
	}
	if errs.ExitCode(err) == 0 {
		t.Error("expected non-zero exit code for degraded stack")
	}
	if len(out.Data.Services) != 1 || out.Data.Services[0].State != "exited" {
		t.Errorf("services: %+v", out.Data.Services)
	}
}

// -----------------------------------------------------------------------------
// 4. infra logs — follow flag and service filter are forwarded to compose
// -----------------------------------------------------------------------------

func TestInfra_Logs_FollowAndService(t *testing.T) {
	dir := makeInstallDir(t, "")
	fc := &fakeComposeRunner{installDir: dir}
	h := InfraLogsHandler(InfraDeps{Compose: fc})
	var buf strings.Builder
	if err := h(context.Background(), InfraLogsRequest{Follow: true, Service: "billing"}, &buf); err != nil {
		t.Fatalf("handler: %v", err)
	}
	if len(fc.streamCalls) != 1 {
		t.Fatalf("expected 1 stream call, got %d", len(fc.streamCalls))
	}
	got := fc.streamCalls[0]
	if len(got) != 3 || got[0] != "logs" || got[1] != "-f" || got[2] != "billing" {
		t.Errorf("stream args: %v", got)
	}
	if buf.Len() == 0 {
		t.Error("expected writer to receive bytes from the fake stream")
	}
}

// -----------------------------------------------------------------------------
// 5. infra upgrade — success: backup first, then tag, pull, up, ready
// -----------------------------------------------------------------------------

func TestInfra_Upgrade_Success(t *testing.T) {
	withPollInterval(t, 5*time.Millisecond)
	dir := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\n")
	fc := &fakeComposeRunner{
		installDir: dir,
		runFn: func(args ...string) (compose.Result, error) {
			// The pre-upgrade backup first calls `compose ps` to
			// confirm postgres is up. Report it running.
			if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
				return compose.Result{Stdout: `{"Name":"sriya-billing-db-1","State":"running","Health":"healthy","Service":"billing-db"}` + "\n"}, nil
			}
			// `pull` and `up -d` are also handled here.
			return compose.Result{}, nil
		},
		runToFn: func(w io.Writer, _ ...string) error {
			// Pretend pg_dump streamed a small SQL header.
			_, _ = w.Write([]byte("-- pre-upgrade backup\n"))
			return nil
		},
	}
	a := &fakeInfraAPI{
		healthFn: func(_ context.Context) (api.Health, error) {
			return api.Health{Status: "Healthy"}, nil
		},
		readyFn: func(_ context.Context) (api.Health, error) {
			return api.Health{Status: "Ready"}, nil
		},
	}
	h := InfraUpgradeHandler(InfraDeps{API: a, Compose: fc})
	ctx := MarkMutable(context.Background())
	out, err := h(ctx, InfraUpgradeRequest{TargetTag: "v1.4.0", Timeout: 2 * time.Second})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Data.NewTag != "v1.4.0" {
		t.Errorf("newTag: got %q want v1.4.0", out.Data.NewTag)
	}
	if out.Data.PreviousTag != "v1.0.0" {
		t.Errorf("previousTag: got %q want v1.0.0", out.Data.PreviousTag)
	}
	if out.Data.RolledBack {
		t.Error("expected no rollback on success")
	}
	// CRITICAL: the pre-upgrade backup MUST have been written.
	if out.Data.BackupPath == "" {
		t.Error("expected BackupPath set after pre-upgrade backup")
	}
	if _, err := os.Stat(out.Data.BackupPath); err != nil {
		t.Errorf("expected backup file on disk at %q: %v", out.Data.BackupPath, err)
	}
	// Verify the .env was bumped.
	got, _ := readEnvVar(dir, "BILLING_IMAGE_TAG")
	if got != "v1.4.0" {
		t.Errorf("env tag: got %q want v1.4.0", got)
	}
	// Verify the migration-aware flow ran backup → pull → up -d.
	// We assert: 1) RunTo was called for pg_dump, 2) Run was called
	// for pull + up -d. Order is enforced by the handler itself.
	if len(fc.runToCalls) == 0 {
		t.Error("expected RunTo (streaming pg_dump) to be called for the pre-upgrade backup")
	}
	sawPull, sawUp := false, false
	for _, c := range fc.runCalls {
		if len(c) > 0 && c[0] == "pull" {
			sawPull = true
		}
		if len(c) >= 2 && c[0] == "up" && c[1] == "-d" {
			sawUp = true
		}
	}
	if !sawPull {
		t.Errorf("expected `pull` invocation; calls: %v", fc.runCalls)
	}
	if !sawUp {
		t.Errorf("expected `up -d` invocation; calls: %v", fc.runCalls)
	}
	// Readyz must have been probed at least once (verified endpoint).
	if a.readyHits == 0 {
		t.Error("expected at least one /health/ready probe during wait")
	}
}

// -----------------------------------------------------------------------------
// 6. infra upgrade — health never recovers: tag rolled back, error code
// -----------------------------------------------------------------------------

func TestInfra_Upgrade_HealthTimeoutRollback(t *testing.T) {
	withPollInterval(t, 5*time.Millisecond)
	dir := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\n")
	fc := &fakeComposeRunner{
		installDir: dir,
		runFn: func(args ...string) (compose.Result, error) {
			if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
				return compose.Result{Stdout: `{"Name":"sriya-billing-db-1","State":"running","Health":"healthy","Service":"billing-db"}` + "\n"}, nil
			}
			return compose.Result{}, nil
		},
		runToFn: func(w io.Writer, _ ...string) error {
			_, _ = w.Write([]byte("-- pre-upgrade backup\n"))
			return nil
		},
	}
	a := &fakeInfraAPI{
		healthFn: func(_ context.Context) (api.Health, error) {
			return api.Health{Status: "Healthy"}, nil
		},
		readyFn: func(_ context.Context) (api.Health, error) {
			return api.Health{Status: "down"}, nil
		},
	}
	h := InfraUpgradeHandler(InfraDeps{API: a, Compose: fc})
	ctx := MarkMutable(context.Background())
	out, err := h(ctx, InfraUpgradeRequest{TargetTag: "v1.4.0", Timeout: 50 * time.Millisecond})
	if err == nil {
		t.Fatal("expected timeout error")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeUpgradeTimeout {
		t.Errorf("expected code=upgrade_health_timeout, got %s", ce.Code)
	}
	// NEW: exit code is 10 (distinct from the network/retryable 6).
	if got := errs.ExitCode(err); got != 10 {
		t.Errorf("expected exit 10 for upgrade_health_timeout, got %d", got)
	}
	if !out.Data.RolledBack {
		t.Error("expected RolledBack=true on timeout")
	}
	if out.Data.NewTag != "v1.4.0" {
		t.Errorf("newTag: got %q want v1.4.0", out.Data.NewTag)
	}
	if out.Data.PreviousTag != "v1.0.0" {
		t.Errorf("previousTag: got %q want v1.0.0", out.Data.PreviousTag)
	}
	// Verify the .env was rolled back to the previous tag.
	got, _ := readEnvVar(dir, "BILLING_IMAGE_TAG")
	if got != "v1.0.0" {
		t.Errorf("env tag after rollback: got %q want v1.0.0", got)
	}
}

// -----------------------------------------------------------------------------
// 7. infra upgrade — backup fails: abort BEFORE mutating .env
// -----------------------------------------------------------------------------

func TestInfra_Upgrade_BackupFailsAbortsBeforeMutation(t *testing.T) {
	dir := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\n")
	fc := &fakeComposeRunner{
		installDir: dir,
		runFn: func(args ...string) (compose.Result, error) {
			// First call: compose ps → NO postgres running.
			if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
				return compose.Result{Stdout: `{"Name":"sriya-billing-1","State":"running","Health":"healthy","Service":"billing"}` + "\n"}, nil
			}
			// pg_dump should never be called because the postgres-up
			// check fails first.
			t.Errorf("unexpected compose call after backup failure: %v", args)
			return compose.Result{}, nil
		},
	}
	a := &fakeInfraAPI{}
	h := InfraUpgradeHandler(InfraDeps{API: a, Compose: fc})
	ctx := MarkMutable(context.Background())
	_, err := h(ctx, InfraUpgradeRequest{TargetTag: "v1.4.0"})
	if err == nil {
		t.Fatal("expected backup-failure error")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeDBUnavailable {
		t.Errorf("expected code=db_unavailable, got %s", ce.Code)
	}
	// CRITICAL: .env MUST NOT have been mutated.
	got, _ := readEnvVar(dir, "BILLING_IMAGE_TAG")
	if got != "v1.0.0" {
		t.Errorf(".env was mutated despite backup failure: BILLING_IMAGE_TAG=%q", got)
	}
	// CRITICAL: pull / up -d MUST NOT have been called.
	for _, c := range fc.runCalls {
		if len(c) > 0 && (c[0] == "pull" || c[0] == "up") {
			t.Errorf("compose lifecycle was invoked despite backup failure: %v", c)
		}
	}
}

// -----------------------------------------------------------------------------
// 8. infra backup — success: file written, size reported, RunTo used
//    (NOT the in-memory string buffer the old impl used).
// -----------------------------------------------------------------------------

func TestInfra_Backup_Success(t *testing.T) {
	// Seed a custom BILLING_DB_USER so we can prove F1 reads the role from
	// .env (instead of hardcoding it) while still using the real service +
	// db names (billing-db / qora_billing).
	dir := makeInstallDir(t, "BILLING_DB_USER=custom_owner\n")
	dump := "--\n-- PostgreSQL database dump\n--\nCREATE TABLE foo (id int);\n"
	var dumpArgs []string
	fc := &fakeComposeRunner{
		installDir: dir,
		runFn: func(args ...string) (compose.Result, error) {
			if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
				// First call: `compose ps --format json` (billing-db up).
				return compose.Result{Stdout: `{"Name":"sriya-billing-db-1","State":"running","Health":"healthy","Service":"billing-db"}` + "\n"}, nil
			}
			// Should NOT be called anymore — backup is streamed.
			t.Errorf("unexpected Run call for backup; expected RunTo: %v", args)
			return compose.Result{}, nil
		},
		runToFn: func(w io.Writer, args ...string) error {
			// pg_dump path: stream the dump bytes directly.
			if len(args) >= 2 && args[0] == "exec" {
				dumpArgs = append([]string(nil), args...)
				_, _ = w.Write([]byte(dump))
				return nil
			}
			t.Errorf("unexpected RunTo args: %v", args)
			return nil
		},
	}
	h := InfraBackupHandler(InfraDeps{API: &fakeInfraAPI{}, Compose: fc})
	out, err := h(context.Background(), struct{}{})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Kind != "InfraBackup" {
		t.Errorf("kind: got %q want InfraBackup", out.Kind)
	}
	if out.Data.Path == "" {
		t.Fatal("expected non-empty path")
	}
	if !strings.HasPrefix(out.Data.Path, dir) {
		t.Errorf("path not under install dir: %q (dir=%q)", out.Data.Path, dir)
	}
	if out.Data.SizeBytes != int64(len(dump)) {
		t.Errorf("sizeBytes: got %d want %d", out.Data.SizeBytes, len(dump))
	}
	body, err := os.ReadFile(out.Data.Path)
	if err != nil {
		t.Fatalf("read backup: %v", err)
	}
	if string(body) != dump {
		t.Errorf("backup content mismatch: got %q want %q", string(body), dump)
	}
	if !strings.HasPrefix(filepath.Base(out.Data.Path), "sriya-backup-") {
		t.Errorf("path filename: got %q want sriya-backup-*.sql prefix", filepath.Base(out.Data.Path))
	}
	if len(fc.runToCalls) != 1 {
		t.Errorf("expected 1 RunTo call (streaming), got %d", len(fc.runToCalls))
	}
	// F1: pg_dump MUST target the real stack — service billing-db, role read
	// from BILLING_DB_USER (custom_owner here), db qora_billing.
	wantDump := []string{"exec", "-T", "billing-db", "pg_dump", "-U", "custom_owner", "qora_billing"}
	if strings.Join(dumpArgs, " ") != strings.Join(wantDump, " ") {
		t.Errorf("pg_dump args: got %v want %v", dumpArgs, wantDump)
	}
}

// -----------------------------------------------------------------------------
// 9. infra backup — postgres not running: db_unavailable, no dump file
// -----------------------------------------------------------------------------

func TestInfra_Backup_PostgresDown(t *testing.T) {
	dir := makeInstallDir(t, "")
	fc := &fakeComposeRunner{
		installDir: dir,
		runFn: func(args ...string) (compose.Result, error) {
			if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
				// Postgres is NOT in the running set; only billing is up.
				return compose.Result{Stdout: `{"Name":"sriya-billing-1","State":"running","Health":"healthy","Service":"billing"}` + "\n"}, nil
			}
			t.Errorf("unexpected `compose exec` after postgres check; args=%v", args)
			return compose.Result{}, nil
		},
	}
	h := InfraBackupHandler(InfraDeps{API: &fakeInfraAPI{}, Compose: fc})
	_, err := h(context.Background(), struct{}{})
	if err == nil {
		t.Fatal("expected error when postgres is down")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeDBUnavailable {
		t.Errorf("expected code=db_unavailable, got %s", ce.Code)
	}
	// No dump file should have been created in the install dir.
	entries, _ := os.ReadDir(dir)
	for _, e := range entries {
		if strings.HasPrefix(e.Name(), "sriya-backup-") {
			t.Errorf("no dump file should be created on db_unavailable; found %s", e.Name())
		}
	}
}

// -----------------------------------------------------------------------------
// 10. infra backup — mid-stream failure: partial file is removed
// -----------------------------------------------------------------------------

func TestInfra_Backup_MidStreamFailureRemovesPartialFile(t *testing.T) {
	dir := makeInstallDir(t, "")
	fc := &fakeComposeRunner{
		installDir: dir,
		runFn: func(args ...string) (compose.Result, error) {
			if len(args) >= 2 && args[0] == "ps" && args[1] == "--format" {
				return compose.Result{Stdout: `{"Name":"sriya-billing-db-1","State":"running","Health":"healthy","Service":"billing-db"}` + "\n"}, nil
			}
			return compose.Result{}, nil
		},
		runToFn: func(_ io.Writer, _ ...string) error {
			// Simulate pg_dump failure mid-stream. The handler must
			// detect the failure, close the file, and remove it so a
			// corrupt partial never lingers on disk (design §#8).
			return errs.New(errs.CodeGeneric, "pg_dump failed mid-stream", "simulated")
		},
	}
	h := InfraBackupHandler(InfraDeps{API: &fakeInfraAPI{}, Compose: fc})
	_, err := h(context.Background(), struct{}{})
	if err == nil {
		t.Fatal("expected error on mid-stream failure")
	}
	// Verify no partial file was left behind.
	entries, _ := os.ReadDir(dir)
	for _, e := range entries {
		if strings.HasPrefix(e.Name(), "sriya-backup-") {
			t.Errorf("partial dump must be removed on mid-stream failure; found %s", e.Name())
		}
	}
}

// -----------------------------------------------------------------------------
// 11. infra restore — dry-run: no side effects, plan returned
// -----------------------------------------------------------------------------

func TestInfra_Restore_DryRun(t *testing.T) {
	dir := makeInstallDir(t, "")
	fc := &fakeComposeRunner{installDir: dir}
	h := InfraRestoreHandler(InfraDeps{API: &fakeInfraAPI{}, Compose: fc})
	dumpPath := filepath.Join(t.TempDir(), "dump.sql")
	if err := os.WriteFile(dumpPath, []byte("-- fake dump --\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	var restoreCalled bool
	withRestoreViaStdin(t, func(_ InfraDeps, _ string) error {
		restoreCalled = true
		return nil
	})
	ctx := MarkMutable(WithDryRun(context.Background()))
	out, err := h(ctx, InfraRestoreRequest{Path: dumpPath})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Kind != "InfraRestore" {
		t.Errorf("kind: got %q want InfraRestore", out.Kind)
	}
	if out.Data.Restored {
		t.Error("dry-run must not set Restored=true")
	}
	if out.Data.Path != dumpPath {
		t.Errorf("path: got %q want %q", out.Data.Path, dumpPath)
	}
	if restoreCalled {
		t.Error("dry-run must not shell out to docker compose exec")
	}
}

// -----------------------------------------------------------------------------
// 12. infra doctor — all checks pass
// -----------------------------------------------------------------------------

func TestInfra_Doctor_AllChecksPass(t *testing.T) {
	withLookPath(t, func(_ string) (string, error) { return "/usr/local/bin/docker", nil })
	longKey := strings.Repeat("x", 32)
	dir := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\nBILLING_DB_PASSWORD=secret\nENCRYPTION_KEY="+longKey+"\n")
	fc := &fakeComposeRunner{installDir: dir}
	h := InfraDoctorHandler(InfraDeps{API: &fakeInfraAPI{}, Compose: fc})
	out, err := h(context.Background(), InfraDoctorRequest{})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Kind != "InfraDoctor" {
		t.Errorf("kind: got %q want InfraDoctor", out.Kind)
	}
	if len(out.Data.Checks) == 0 {
		t.Fatal("expected at least one check")
	}
	wantNames := map[string]bool{
		"docker-binary":      false,
		"docker-daemon":      false,
		"install-dir":        false,
		"env-keys":           false,
		"encryption-key-len": false,
		"service-token":      false,
	}
	for _, c := range out.Data.Checks {
		if _, ok := wantNames[c.Name]; ok {
			wantNames[c.Name] = true
		}
		if c.Status == "fail" {
			t.Errorf("unexpected fail on %q: %+v", c.Name, c)
		}
	}
	for name, seen := range wantNames {
		if !seen {
			t.Errorf("missing expected check %q", name)
		}
	}
	if errs.ExitCode(err) != 0 {
		t.Errorf("expected exit 0 for all-pass, got %d", errs.ExitCode(err))
	}
}

// -----------------------------------------------------------------------------
// 13. infra doctor — encryption key too short: fail + actionable hint
// -----------------------------------------------------------------------------

func TestInfra_Doctor_EncryptionKeyTooShort(t *testing.T) {
	withLookPath(t, func(_ string) (string, error) { return "/usr/local/bin/docker", nil })
	dir := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\nBILLING_DB_PASSWORD=secret\nENCRYPTION_KEY=short\n")
	fc := &fakeComposeRunner{installDir: dir}
	h := InfraDoctorHandler(InfraDeps{API: &fakeInfraAPI{}, Compose: fc})
	out, err := h(context.Background(), InfraDoctorRequest{})
	if err == nil {
		t.Fatal("expected error for short encryption key")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeDoctorCheckFailed {
		t.Errorf("expected code=doctor_check_failed, got %s", ce.Code)
	}
	// NEW: exit code is 11 (distinct from the network/retryable 6).
	if got := errs.ExitCode(err); got != 11 {
		t.Errorf("expected exit 11 for doctor_check_failed, got %d", got)
	}
	// The doctor failure sentinel MUST be Renderable so the cli layer
	// prints the checks table to stdout alongside the error envelope on
	// stderr (design §#4). Without this the operator never learns which
	// checks failed.
	if !ce.Renderable() {
		t.Error("expected the doctor_check_failed sentinel to be Renderable (design §#4)")
	}
	var encCheck *InfraDoctorCheck
	for i := range out.Data.Checks {
		if out.Data.Checks[i].Name == "encryption-key-len" {
			encCheck = &out.Data.Checks[i]
		}
	}
	if encCheck == nil {
		t.Fatalf("encryption-key-len check missing; got: %+v", out.Data.Checks)
	}
	if encCheck.Status != "fail" {
		t.Errorf("encryption-key-len status: got %q want fail", encCheck.Status)
	}
	if encCheck.Hint == "" {
		t.Error("expected actionable hint on encryption-key-len failure")
	}
}

// -----------------------------------------------------------------------------
// 13b. infra doctor — env-keys reads BILLING_DB_PASSWORD, not POSTGRES_PASSWORD
//      (F2). A valid .env that has BILLING_DB_PASSWORD passes the env-keys
//      check; a .env that is missing it fails with an actionable hint.
// -----------------------------------------------------------------------------

func TestInfra_Doctor_EnvKeysReadsBillingDBPassword(t *testing.T) {
	withLookPath(t, func(_ string) (string, error) { return "/usr/local/bin/docker", nil })
	longKey := strings.Repeat("x", 32)

	// (a) BILLING_DB_PASSWORD present → env-keys passes.
	dirOK := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\nBILLING_DB_PASSWORD=secret\nENCRYPTION_KEY="+longKey+"\n")
	hOK := InfraDoctorHandler(InfraDeps{API: &fakeInfraAPI{}, Compose: &fakeComposeRunner{installDir: dirOK}})
	outOK, errOK := hOK(context.Background(), InfraDoctorRequest{})
	if errOK != nil {
		t.Fatalf("doctor with BILLING_DB_PASSWORD present: %v", errOK)
	}
	for _, c := range outOK.Data.Checks {
		if c.Name == "env-keys" && c.Status != "pass" {
			t.Errorf("env-keys must pass when BILLING_DB_PASSWORD is present; got %+v", c)
		}
	}

	// (b) BILLING_DB_PASSWORD absent (only the legacy POSTGRES_PASSWORD) →
	//     env-keys fails, and the hint names the missing key.
	dirBad := makeInstallDir(t, "BILLING_IMAGE_TAG=v1.0.0\nPOSTGRES_PASSWORD=secret\nENCRYPTION_KEY="+longKey+"\n")
	hBad := InfraDoctorHandler(InfraDeps{API: &fakeInfraAPI{}, Compose: &fakeComposeRunner{installDir: dirBad}})
	outBad, errBad := hBad(context.Background(), InfraDoctorRequest{})
	if errBad == nil {
		t.Fatal("expected env-keys failure when BILLING_DB_PASSWORD is missing")
	}
	var envCheck *InfraDoctorCheck
	for i := range outBad.Data.Checks {
		if outBad.Data.Checks[i].Name == "env-keys" {
			envCheck = &outBad.Data.Checks[i]
		}
	}
	if envCheck == nil || envCheck.Status != "fail" {
		t.Fatalf("expected env-keys=fail; got %+v", outBad.Data.Checks)
	}
	if !strings.Contains(envCheck.Hint, "BILLING_DB_PASSWORD") {
		t.Errorf("env-keys hint must name BILLING_DB_PASSWORD; got %q", envCheck.Hint)
	}
}
