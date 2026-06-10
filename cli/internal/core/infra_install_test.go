package core

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/compose"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// -----------------------------------------------------------------------------
// Fakes for the install handler. Every external effect is behind a seam:
//   - docker detection  -> fakeDockerProbe (installer.DockerProbe)
//   - compose download  -> fakeFetcher (installer.Fetcher)
//   - compose lifecycle -> fakeComposeRunner (from infra_test.go) + factory
//   - health polling    -> a ReadyProbe closure
// No live network, daemon, or filesystem outside the test's temp dir.
// -----------------------------------------------------------------------------

type fakeDockerProbe struct {
	binPath   string
	binErr    error
	daemonErr error
}

func (f fakeDockerProbe) BinaryPath() (string, error) {
	if f.binErr != nil {
		return "", f.binErr
	}
	if f.binPath == "" {
		return "/usr/local/bin/docker", nil
	}
	return f.binPath, nil
}

func (f fakeDockerProbe) DaemonUp(context.Context) error { return f.daemonErr }

type fakeFetcher struct {
	body  string
	err   error
	calls int
}

func (f *fakeFetcher) Fetch(_ context.Context, _ string) (io.ReadCloser, error) {
	f.calls++
	if f.err != nil {
		return nil, f.err
	}
	return io.NopCloser(strings.NewReader(f.body)), nil
}

// readyProbeScript drives the health-wait: it returns "Ready" on the
// readyAfter-th call (1-based) and "not-ready" before that.
func readyProbeScript(readyAfter int) (ReadyProbe, *int) {
	calls := 0
	p := func(context.Context) (api.Health, error) {
		calls++
		if calls >= readyAfter {
			return api.Health{Status: "Ready"}, nil
		}
		return api.Health{Status: "Starting"}, nil
	}
	return p, &calls
}

// newComposeFactory returns a ComposeRunnerFactory that always hands back
// the provided fake runner (with its installDir patched to the requested
// dir so the fake's ValidateInstallDir passes once the files exist).
func newComposeFactory(fc *fakeComposeRunner) ComposeRunnerFactory {
	return func(dir string) (compose.Runner, error) {
		fc.installDir = dir
		return fc, nil
	}
}

// -----------------------------------------------------------------------------
// 1. Happy path: clean install renders .env, downloads compose, pull+up,
//    becomes ready, exit 0, NextStep points at bootstrap.
// -----------------------------------------------------------------------------

func TestInfraInstall_HappyPath(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	fetcher := &fakeFetcher{body: "services: {}\n"}
	ready, readyCalls := readyProbeScript(1)

	deps := InfraInstallDeps{
		Fetcher:            fetcher,
		Probe:              fakeDockerProbe{}, // docker present + daemon up
		NewCompose:         newComposeFactory(fc),
		Ready:              ready,
		HealthTimeout:      2 * time.Second,
		HealthPollInterval: 10 * time.Millisecond,
	}
	h := InfraInstallHandler(deps)
	// Provision-only: skip the bootstrap chain (covered by the dedicated
	// Fase 5 tests). NextStep should still point the operator at the next
	// command.
	out, err := h(context.Background(), InfraInstallRequest{Version: "1.0.0", Dir: dir, NoBootstrap: true})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if out.Kind != "InfraInstall" {
		t.Errorf("kind: got %q want InfraInstall", out.Kind)
	}
	if out.SchemaVersion != SchemaVersion {
		t.Errorf("schemaVersion: got %q want %q", out.SchemaVersion, SchemaVersion)
	}
	d := out.Data
	if !d.EnvCreated {
		t.Error("expected EnvCreated=true on clean install")
	}
	if !d.ComposeCreated {
		t.Error("expected ComposeCreated=true on clean install")
	}
	if !d.Healthy {
		t.Error("expected Healthy=true")
	}
	if d.ImageTag != "1.0.0" {
		t.Errorf("imageTag: got %q want 1.0.0", d.ImageTag)
	}
	if !strings.Contains(d.NextStep, "bootstrap") {
		t.Errorf("expected NextStep to mention bootstrap, got %q", d.NextStep)
	}
	// .env was written, chmod 600, with the 9 keys.
	envBytes, rerr := os.ReadFile(filepath.Join(dir, ".env"))
	if rerr != nil {
		t.Fatalf("read .env: %v", rerr)
	}
	if !strings.Contains(string(envBytes), "BILLING_DB_PASSWORD=") {
		t.Error("expected BILLING_DB_PASSWORD in rendered .env")
	}
	info, _ := os.Stat(filepath.Join(dir, ".env"))
	if info != nil && info.Mode().Perm() != 0o600 {
		t.Errorf(".env perms: got %v want 0600", info.Mode().Perm())
	}
	// compose was fetched once and saved as docker-compose.yml.
	if fetcher.calls != 1 {
		t.Errorf("fetcher calls: got %d want 1", fetcher.calls)
	}
	if _, serr := os.Stat(filepath.Join(dir, "docker-compose.yml")); serr != nil {
		t.Errorf("expected docker-compose.yml written: %v", serr)
	}
	// pull then up -d were invoked, in that order.
	if len(fc.runCalls) < 2 {
		t.Fatalf("expected at least 2 compose runs, got %v", fc.runCalls)
	}
	if fc.runCalls[0][0] != "pull" {
		t.Errorf("first compose call: got %v want pull", fc.runCalls[0])
	}
	if fc.runCalls[1][0] != "up" || len(fc.runCalls[1]) < 2 || fc.runCalls[1][1] != "-d" {
		t.Errorf("second compose call: got %v want [up -d]", fc.runCalls[1])
	}
	if *readyCalls < 1 {
		t.Error("expected at least one readiness probe")
	}
	if errs.ExitCode(err) != 0 {
		t.Errorf("expected exit 0, got %d", errs.ExitCode(err))
	}
}

// -----------------------------------------------------------------------------
// 2. Idempotent re-run: pre-existing .env and compose are NOT clobbered;
//    pull+up still run; reports created=false but Healthy=true.
// -----------------------------------------------------------------------------

func TestInfraInstall_IdempotentNoClobber(t *testing.T) {
	dir := t.TempDir()
	// Pre-seed an existing .env (with a sentinel tag) + compose so the
	// no-clobber paths short-circuit.
	if err := os.WriteFile(filepath.Join(dir, ".env"), []byte("BILLING_IMAGE_TAG=v9.9.9\nSERVICE_AUTH_TOKEN=keepme\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "docker-compose.yml"), []byte("services: {}\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	fc := &fakeComposeRunner{}
	fetcher := &fakeFetcher{body: "SHOULD NOT BE WRITTEN"}
	ready, _ := readyProbeScript(1)

	deps := InfraInstallDeps{
		Fetcher:            fetcher,
		Probe:              fakeDockerProbe{},
		NewCompose:         newComposeFactory(fc),
		Ready:              ready,
		HealthTimeout:      2 * time.Second,
		HealthPollInterval: 10 * time.Millisecond,
	}
	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{Version: "1.0.0", Dir: dir, NoBootstrap: true})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	d := out.Data
	if d.EnvCreated {
		t.Error("expected EnvCreated=false on re-run (no-clobber)")
	}
	if d.ComposeCreated {
		t.Error("expected ComposeCreated=false on re-run (no-clobber)")
	}
	if !d.Healthy {
		t.Error("expected Healthy=true")
	}
	// The existing tag must be reported back, NOT the requested 1.0.0.
	if d.ImageTag != "v9.9.9" {
		t.Errorf("imageTag: got %q want v9.9.9 (read from existing .env)", d.ImageTag)
	}
	// Fetcher must NOT have been called (compose already present).
	if fetcher.calls != 0 {
		t.Errorf("fetcher must not be called on no-clobber; got %d calls", fetcher.calls)
	}
	// The pre-existing secret must be untouched.
	envBytes, _ := os.ReadFile(filepath.Join(dir, ".env"))
	if !strings.Contains(string(envBytes), "SERVICE_AUTH_TOKEN=keepme") {
		t.Error("re-run rotated/clobbered the existing .env secret")
	}
}

// -----------------------------------------------------------------------------
// 3. docker unavailable: pre-install doctor fails BEFORE any file is
//    written. No .env, no compose, no pull. Code docker_unavailable, exit 6.
// -----------------------------------------------------------------------------

func TestInfraInstall_DockerUnavailable(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	fetcher := &fakeFetcher{body: "services: {}\n"}
	ready, _ := readyProbeScript(1)

	deps := InfraInstallDeps{
		Fetcher:    fetcher,
		Probe:      fakeDockerProbe{binErr: errors.New("not on PATH")}, // docker absent
		NewCompose: newComposeFactory(fc),
		Ready:      ready,
	}
	h := InfraInstallHandler(deps)
	_, err := h(context.Background(), InfraInstallRequest{Version: "1.0.0", Dir: dir})
	if err == nil {
		t.Fatal("expected docker_unavailable error")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeDockerUnavailable {
		t.Errorf("code: got %s want docker_unavailable", ce.Code)
	}
	if got := errs.ExitCode(err); got != 6 {
		t.Errorf("exit: got %d want 6", got)
	}
	// Nothing should have been written or invoked.
	if _, serr := os.Stat(filepath.Join(dir, ".env")); serr == nil {
		t.Error("no .env should be written when docker is unavailable")
	}
	if fetcher.calls != 0 {
		t.Error("fetcher must not be called when docker is unavailable")
	}
	if len(fc.runCalls) != 0 {
		t.Errorf("no compose calls expected; got %v", fc.runCalls)
	}
}

// -----------------------------------------------------------------------------
// 4. install_health_timeout: stack comes up but never reaches /health/ready.
//    Result is Renderable (so the cli prints the payload), Healthy=false,
//    code install_health_timeout, exit 10, stack NOT torn down.
// -----------------------------------------------------------------------------

func TestInfraInstall_HealthTimeout(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	fetcher := &fakeFetcher{body: "services: {}\n"}
	// Never ready: probe always returns a non-Ready status.
	neverReady := func(context.Context) (api.Health, error) {
		return api.Health{Status: "Starting"}, nil
	}

	deps := InfraInstallDeps{
		Fetcher:            fetcher,
		Probe:              fakeDockerProbe{},
		NewCompose:         newComposeFactory(fc),
		Ready:              neverReady,
		HealthTimeout:      40 * time.Millisecond,
		HealthPollInterval: 5 * time.Millisecond,
	}
	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{Version: "1.0.0", Dir: dir})
	if err == nil {
		t.Fatal("expected install_health_timeout error")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeInstallHealthTimeout {
		t.Errorf("code: got %s want install_health_timeout", ce.Code)
	}
	if got := errs.ExitCode(err); got != 10 {
		t.Errorf("exit: got %d want 10", got)
	}
	if !ce.Renderable() {
		t.Error("install_health_timeout sentinel must be Renderable so the payload is printed (design §#4)")
	}
	if out.Data.Healthy {
		t.Error("Healthy must be false on timeout")
	}
	// .env and compose WERE provisioned (the failure is post-up), and the
	// stack was started (pull + up ran) — no rollback on a fresh install.
	if !out.Data.EnvCreated || !out.Data.ComposeCreated {
		t.Error("expected env+compose provisioned before the health wait")
	}
	if len(fc.runCalls) < 2 {
		t.Errorf("expected pull+up before the health wait; got %v", fc.runCalls)
	}
}

// -----------------------------------------------------------------------------
// 5. compose download failure: a failing fetch returns the download error
//    and writes NO compose file. .env may exist (rendered first), but the
//    stack is never brought up.
// -----------------------------------------------------------------------------

func TestInfraInstall_ComposeDownloadFailed(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	fetcher := &fakeFetcher{err: errors.New("404 tag not found")}
	ready, _ := readyProbeScript(1)

	deps := InfraInstallDeps{
		Fetcher:    fetcher,
		Probe:      fakeDockerProbe{},
		NewCompose: newComposeFactory(fc),
		Ready:      ready,
	}
	h := InfraInstallHandler(deps)
	_, err := h(context.Background(), InfraInstallRequest{Version: "1.0.0", Dir: dir})
	if err == nil {
		t.Fatal("expected a download error")
	}
	if _, serr := os.Stat(filepath.Join(dir, "docker-compose.yml")); serr == nil {
		t.Error("no compose file should be written when the download fails")
	}
	if len(fc.runCalls) != 0 {
		t.Errorf("stack must not be brought up after a failed download; got %v", fc.runCalls)
	}
}

// -----------------------------------------------------------------------------
// 6. JSON contract: the success envelope serializes with kind/schemaVersion
//    and the documented fields, and NEVER leaks the secret-bearing fields.
// -----------------------------------------------------------------------------

func TestInfraInstall_JSONContract_NoSecretLeak(t *testing.T) {
	r := InfraInstallResult{
		InstallDir:     "/home/op/sriya",
		ImageTag:       "1.0.0",
		EnvCreated:     true,
		ComposeCreated: true,
		Healthy:        true,
		NextStep:       "run bootstrap",
		TenantID:       "tnt_secret",
		APIKey:         "ak_supersecret",
	}
	out := NewOutput("InfraInstall", r)
	b, err := json.MarshalIndent(out, "", "  ")
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	js := string(b)
	for _, want := range []string{`"kind": "InfraInstall"`, `"schemaVersion": "1.0"`, `"installDir"`, `"healthy"`, `"nextStep"`} {
		if !strings.Contains(js, want) {
			t.Errorf("JSON missing %q\n%s", want, js)
		}
	}
	// Secret-bearing fields are json:"-" and MUST NOT appear.
	if strings.Contains(js, "ak_supersecret") || strings.Contains(js, "tnt_secret") {
		t.Errorf("secret leaked into JSON output:\n%s", js)
	}
	if strings.Contains(js, "TenantID") || strings.Contains(js, "apiKey") || strings.Contains(js, "tenantId") {
		t.Errorf("secret field key leaked into JSON output:\n%s", js)
	}
}

// -----------------------------------------------------------------------------
// 7. PRE-install doctor split (F6): PreInstall runs ONLY docker checks and
//    needs NO install dir / compose. A nil-Probe + no compose dir must not
//    panic; here we drive it explicitly via the probe seam.
// -----------------------------------------------------------------------------

func TestInfraDoctor_PreInstall_DockerOnly(t *testing.T) {
	// Pre-install: docker up. No compose runner provided at all (nil) to
	// prove the pre-install path never touches it.
	d := InfraDeps{Probe: fakeDockerProbe{}}
	h := InfraDoctorHandler(d)
	out, err := h(context.Background(), InfraDoctorRequest{PreInstall: true})
	if err != nil {
		t.Fatalf("pre-install doctor (docker up): %v", err)
	}
	names := map[string]string{}
	for _, c := range out.Data.Checks {
		names[c.Name] = c.Status
	}
	if len(out.Data.Checks) != 2 {
		t.Fatalf("pre-install should run exactly 2 checks, got %d: %+v", len(out.Data.Checks), out.Data.Checks)
	}
	if names["docker-binary"] != "pass" || names["docker-daemon"] != "pass" {
		t.Errorf("expected docker-binary+daemon pass, got %+v", names)
	}
	// install-dir / env-keys must NOT be present in the pre-install subset.
	if _, ok := names["install-dir"]; ok {
		t.Error("pre-install doctor must NOT run install-dir check")
	}
	if _, ok := names["env-keys"]; ok {
		t.Error("pre-install doctor must NOT run env-keys check")
	}
}

func TestInfraDoctor_PreInstall_DaemonDown(t *testing.T) {
	d := InfraDeps{Probe: fakeDockerProbe{daemonErr: errors.New("cannot connect")}}
	h := InfraDoctorHandler(d)
	out, err := h(context.Background(), InfraDoctorRequest{PreInstall: true})
	if err == nil {
		t.Fatal("expected doctor_check_failed when the daemon is down")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeDoctorCheckFailed {
		t.Errorf("code: got %s want doctor_check_failed", ce.Code)
	}
	if !ce.Renderable() {
		t.Error("doctor failure sentinel must be Renderable")
	}
	var daemon *InfraDoctorCheck
	for i := range out.Data.Checks {
		if out.Data.Checks[i].Name == "docker-daemon" {
			daemon = &out.Data.Checks[i]
		}
	}
	if daemon == nil || daemon.Status != "fail" {
		t.Errorf("expected docker-daemon=fail, got %+v", out.Data.Checks)
	}
}

// -----------------------------------------------------------------------------
// Fase 5 — install → bootstrap chaining + context/token seeding (F5).
// -----------------------------------------------------------------------------

// fakeBootstrapClient records the request and returns a canned response (or a
// canned error). It is the BootstrapClient seam.
type fakeBootstrapClient struct {
	resp    api.BootstrapResponse
	err     error
	called  bool
	gotReq  api.BootstrapRequest
}

func (f *fakeBootstrapClient) BootstrapTenant(_ context.Context, req api.BootstrapRequest) (api.BootstrapResponse, error) {
	f.called = true
	f.gotReq = req
	if f.err != nil {
		return api.BootstrapResponse{}, f.err
	}
	return f.resp, nil
}

// fakeSeeder records the seed input and returns a canned result.
type fakeSeeder struct {
	result  SeedResult
	err     error
	called  bool
	gotSeed SeedInput
}

func (f *fakeSeeder) Seed(_ context.Context, in SeedInput) (SeedResult, error) {
	f.called = true
	f.gotSeed = in
	if f.err != nil {
		return SeedResult{}, f.err
	}
	if f.result.ContextName == "" {
		f.result.ContextName = in.ContextName
	}
	return f.result, nil
}

// fakeInteractor returns a request with all required fields filled.
type fakeInteractor struct {
	fill func(api.BootstrapRequest) api.BootstrapRequest
}

func (f *fakeInteractor) Prompt(in api.BootstrapRequest) (api.BootstrapRequest, error) {
	if f.fill != nil {
		return f.fill(in), nil
	}
	return in, nil
}

// installDepsWithBootstrap returns a ready set of deps for a healthy stack
// with the given bootstrap seams.
func installDepsWithBootstrap(fc *fakeComposeRunner, boot *fakeBootstrapClient, seeder *fakeSeeder, interactive bool, interactor BootstrapInteractor) InfraInstallDeps {
	ready, _ := readyProbeScript(1)
	return InfraInstallDeps{
		Fetcher:            &fakeFetcher{body: "services: {}\n"},
		Probe:              fakeDockerProbe{},
		NewCompose:         newComposeFactory(fc),
		Ready:              ready,
		BootstrapAPI:       boot,
		Seeder:             seeder,
		Interactive:        func() bool { return interactive },
		Interactor:         interactor,
		HealthTimeout:      2 * time.Second,
		HealthPollInterval: 10 * time.Millisecond,
	}
}

// Fase 5.1 — headless happy path: all bootstrap flags supplied, seeder runs,
// POST /api/v1/bootstrap is called, TenantID/APIKey populated, exit 0.
func TestInfraInstall_Bootstrap_HeadlessHappy(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	boot := &fakeBootstrapClient{resp: api.BootstrapResponse{TenantID: "tnt_123", APIKey: "ak_live"}}
	seeder := &fakeSeeder{}
	deps := installDepsWithBootstrap(fc, boot, seeder, false /*non-TTY*/, nil)

	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{
		Version:     "1.0.0",
		Dir:         dir,
		ContextName: "local",
		LocalURL:    "http://localhost:8080",
		Boot: api.BootstrapRequest{
			RUC: "1790012345001", RazonSocial: "ACME", OwnerName: "Jane",
			Password: "pw", CertificatePath: "/tmp/cert.p12",
		},
	})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if !seeder.called {
		t.Error("expected the seeder to run before bootstrap")
	}
	if seeder.gotSeed.URL != "http://localhost:8080" || seeder.gotSeed.ContextName != "local" {
		t.Errorf("seed input: got %+v", seeder.gotSeed)
	}
	// The service token must be read from the rendered .env and passed to the
	// seeder (never via a flag).
	if seeder.gotSeed.ServiceToken == "" {
		t.Error("expected the seeder to receive the SERVICE_AUTH_TOKEN from the rendered .env")
	}
	if !boot.called {
		t.Fatal("expected BootstrapTenant to be called")
	}
	if boot.gotReq.RUC != "1790012345001" {
		t.Errorf("bootstrap req RUC: got %q", boot.gotReq.RUC)
	}
	if out.Data.TenantID != "tnt_123" {
		t.Errorf("TenantID: got %q want tnt_123", out.Data.TenantID)
	}
	if out.Data.APIKey != "ak_live" {
		t.Errorf("APIKey (in-memory only): got %q want ak_live", out.Data.APIKey)
	}
	if !strings.Contains(out.Data.NextStep, "tnt_123") {
		t.Errorf("NextStep should mention the tenant id; got %q", out.Data.NextStep)
	}
	if errs.ExitCode(err) != 0 {
		t.Errorf("expected exit 0, got %d", errs.ExitCode(err))
	}
}

// Fase 5.2 — headless missing required flag → bootstrap_input_required (exit 2),
// no POST, stack already healthy. Must NOT hang waiting for input.
func TestInfraInstall_Bootstrap_HeadlessMissingFlag(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	boot := &fakeBootstrapClient{}
	seeder := &fakeSeeder{}
	deps := installDepsWithBootstrap(fc, boot, seeder, false, nil)

	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{
		Version:  "1.0.0",
		Dir:      dir,
		LocalURL: "http://localhost:8080",
		// Missing RUC/cert/etc.
		Boot: api.BootstrapRequest{RazonSocial: "ACME"},
	})
	if err == nil {
		t.Fatal("expected bootstrap_input_required when headless and flags missing")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeBootstrapInputRequired {
		t.Errorf("code: got %s want bootstrap_input_required", ce.Code)
	}
	if got := errs.ExitCode(err); got != 2 {
		t.Errorf("exit: got %d want 2", got)
	}
	if boot.called {
		t.Error("bootstrap must NOT be POSTed when required input is missing")
	}
	// The stack is healthy and the payload is rendered alongside the error.
	if !out.Data.Healthy {
		t.Error("stack should still be healthy")
	}
	if !ce.Renderable() {
		t.Error("bootstrap_input_required should be Renderable so the install payload is shown")
	}
}

// Fase 5.3 — interactive (TTY): the interactor fills the missing fields, then
// bootstrap is POSTed.
func TestInfraInstall_Bootstrap_InteractiveFills(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	boot := &fakeBootstrapClient{resp: api.BootstrapResponse{TenantID: "tnt_tty", APIKey: "ak_tty"}}
	seeder := &fakeSeeder{}
	interactor := &fakeInteractor{fill: func(in api.BootstrapRequest) api.BootstrapRequest {
		in.RUC = "1790012345001"
		in.OwnerName = "Jane"
		in.Password = "pw"
		in.CertificatePath = "/tmp/cert.p12"
		return in
	}}
	deps := installDepsWithBootstrap(fc, boot, seeder, true /*TTY*/, interactor)

	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{
		Version:  "1.0.0",
		Dir:      dir,
		LocalURL: "http://localhost:8080",
		// Only razonSocial supplied; the rest come from the prompt.
		Boot: api.BootstrapRequest{RazonSocial: "ACME"},
	})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if !boot.called {
		t.Fatal("expected bootstrap to be POSTed after the interactive prompt filled the fields")
	}
	if boot.gotReq.RUC != "1790012345001" || boot.gotReq.RazonSocial != "ACME" {
		t.Errorf("merged bootstrap req wrong: %+v", boot.gotReq)
	}
	if out.Data.TenantID != "tnt_tty" {
		t.Errorf("TenantID: got %q", out.Data.TenantID)
	}
}

// Fase 5.4 — --no-bootstrap: stack provisioned, NO seed, NO POST, exit 0.
func TestInfraInstall_Bootstrap_NoBootstrapSkips(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	boot := &fakeBootstrapClient{}
	seeder := &fakeSeeder{}
	deps := installDepsWithBootstrap(fc, boot, seeder, false, nil)

	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{Version: "1.0.0", Dir: dir, NoBootstrap: true})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if boot.called {
		t.Error("--no-bootstrap must NOT POST bootstrap")
	}
	if seeder.called {
		t.Error("--no-bootstrap must NOT seed context/token")
	}
	if out.Data.TenantID != "" || out.Data.APIKey != "" {
		t.Error("no tenant should be created with --no-bootstrap")
	}
}

// Fase 5.5 — keychain fallback: the seeder reports TokenFallbackEnv; the
// NextStep informs the operator of the fallback mode (still exit 0).
func TestInfraInstall_Bootstrap_KeychainFallbackReported(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	boot := &fakeBootstrapClient{resp: api.BootstrapResponse{TenantID: "tnt_fb", APIKey: "ak_fb"}}
	seeder := &fakeSeeder{result: SeedResult{TokenFallbackEnv: true}}
	deps := installDepsWithBootstrap(fc, boot, seeder, false, nil)

	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{
		Version:  "1.0.0",
		Dir:      dir,
		LocalURL: "http://localhost:8080",
		Boot: api.BootstrapRequest{
			RUC: "1790012345001", RazonSocial: "ACME", OwnerName: "Jane",
			Password: "pw", CertificatePath: "/tmp/cert.p12",
		},
	})
	if err != nil {
		t.Fatalf("handler: %v", err)
	}
	if !strings.Contains(out.Data.NextStep, "SRIYACTL_SERVICE_TOKEN") {
		t.Errorf("expected NextStep to report the env-var fallback; got %q", out.Data.NextStep)
	}
}

// Fase 5.6 — bootstrap POST fails: the backend code is surfaced, the stack is
// left healthy, the payload is Renderable.
func TestInfraInstall_Bootstrap_PostFailsKeepsStack(t *testing.T) {
	dir := t.TempDir()
	fc := &fakeComposeRunner{}
	boot := &fakeBootstrapClient{err: errs.New(errs.CodeTenantDuplicate, "tenant already exists for this ruc", "pick a different ruc")}
	seeder := &fakeSeeder{}
	deps := installDepsWithBootstrap(fc, boot, seeder, false, nil)

	h := InfraInstallHandler(deps)
	out, err := h(context.Background(), InfraInstallRequest{
		Version:  "1.0.0",
		Dir:      dir,
		LocalURL: "http://localhost:8080",
		Boot: api.BootstrapRequest{
			RUC: "1790012345001", RazonSocial: "ACME", OwnerName: "Jane",
			Password: "pw", CertificatePath: "/tmp/cert.p12",
		},
	})
	if err == nil {
		t.Fatal("expected the bootstrap failure to surface")
	}
	var ce *errs.CLIError
	if !errors.As(err, &ce) {
		t.Fatalf("expected CLIError, got %T", err)
	}
	if ce.Code != errs.CodeTenantDuplicate {
		t.Errorf("code: got %s want tenant_duplicate (verbatim backend code)", ce.Code)
	}
	if !out.Data.Healthy {
		t.Error("the stack must be left healthy on a bootstrap failure")
	}
	if !ce.Renderable() {
		t.Error("the install payload must be Renderable alongside the bootstrap error")
	}
}
