# Archive Report: sriyactl-v1-fixes

**Change**: `sriyactl-v1-fixes`
**Archived**: 2026-06-06
**Origin change**: follow-up to `sriyactl-cli` (archived 2026-06-06)
**Repository**: `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/sriyactl/`

## Summary

`sriyactl-v1-fixes` addressed the 11 findings that prevented the `sriyactl` CLI from being
shippable as v1. The previous `verify` had signed off against a **fabricated** backend
contract; this change re-verified every contract assumption against the real .NET backend
in `billing/src/Qora.Billing.Api/` and corrected the DTOs, status codes, and heuristics
accordingly. All 3 blockers, 2 high, 3 medium, and 3 low findings are resolved, all 11
contract-cited test rewrites exercise the verified shape (camelCase cert DTO, 400 +
ProblemDetails for RUC duplicado, distinct `/health` and `/health/ready` with PascalCase
status), and 22/22 spec scenarios are COMPLIANT.

## Final Status

| Check | Status | Notes |
|-------|--------|-------|
| `go build ./...` | pass | clean |
| `go vet ./...` | pass | no findings |
| `gofmt -l .` | clean | empty output |
| `go test ./... -count=1` | **88 / 88** pass | 0 fail, 0 skip |
| `go test -race ./... -count=1` | pass | 88 / 88, no data-race reports |
| Spec compliance | **22 / 22** | all 4 specs (ai-contract, cert, infra, tenant) |
| Tasks | **15 / 15** sections complete (28/28 task checkboxes, 1 cosmetic checkbox deferred to BACKLOG) |
| OQ decisions | **9 / 9** verified in code | see below |
| Verdict (verify) | **PASS** | no CRITICAL, no warnings blocking archive |

## Spec Compliance Summary

| Domain | Requirements | Scenarios | COMPLIANT | PARTIAL | UNTESTED | FAILING |
|--------|--------------|-----------|-----------|---------|----------|---------|
| `ai-contract` | 9 (6 original + 3 added) | 8 | 8 | 0 | 0 | 0 |
| `cert` | 1 (modified) | 4 | 4 | 0 | 0 | 0 |
| `infra` | 6 (3 modified, 3 unchanged) | 8 | 8 | 0 | 0 | 0 |
| `tenant` | 4 (1 modified, 3 unchanged) | 2 (tenant create section) | 2 | 0 | 0 | 0 |
| **TOTAL** | **20** | **22** | **22** | **0** | **0** | **0** |

The 22-scenario total matches `state.yaml` (`spec_scenarios_total: 22` /
`spec_scenarios_compliant: 22`) and the verify report.

## Resolved Design Questions (9 OQ Decisions)

All 9 open questions from `design.md` "Open Questions" + the apply-phase decisions
documented in `apply-progress.md` "## Decisiones tomadas" are reflected in the code:

| # | OQ Decision | Where implemented |
|---|-------------|-------------------|
| 1 | `Issuer` removed from `CertStatusEntry` (design OQ; no longer a dead column) | `internal/core/cert.go:56-62` (no `Issuer` field) + comment at `cert.go:48` |
| 2 | Backup pre-upgrade: always create a new one (no "recent backup" check) | `internal/core/infra.go:232` `runBackup(ctx, d)` called unconditionally before `writeEnvVar` |
| 3 | `internal/cli/runner.go` mentioned in tasks 4.2 was a typo for `internal/compose/runner.go` | `internal/compose/runner.go:227-256` `RunTo` |
| 4 | Health status comparison uses PascalCase `"Healthy"` / `"Ready"` (not `"ok"`) | `internal/core/infra.go:97-102`; `internal/api/client_test.go:53,89` assert PascalCase |
| 5 | Confirm TTY check via package-level `isTerminalFn` var (overridable in tests) | `internal/cli/confirm.go:41`; `cli/confirm_test.go:18-23` `withFakeTTY` |
| 6 | Renderable flow: payload to stdout FIRST, then error to stderr | `internal/cli/middleware.go:131-136` |
| 7 | Precedence gate: `GuardMutation` (exit 7) → `Confirm` (exit 2) → handler | `internal/core/infra.go:216`; `internal/cli/middleware.go:118-125` |
| 8 | Backup code preservation (don't collapse `db_unavailable` to `generic`) | `internal/core/infra.go:238-247` extracts `ce.Code` and `errs.Wrap(code, ...)` |
| 9 | Build-time version vars + canonical `ldflags` | `cmd/sriyactl/main.go:13-17` `version/commit/date` with defaults; `.goreleaser.yaml:30-35` uses `-X main.version={{.Version}}` etc. (NOT repeated `-ldflags=-X`) |

## Test Rewrites (11+ rewrites against cited contracts)

Each rewrite was verified by `sdd-verify` against the corresponding backend `.cs` source
file (see `verify-report.md` §"Test Rewrite Cross-Check"):

| # | Test | Old assumption (fabricated) | New contract |
|---|------|------------------------------|--------------|
| 1 | `TestHealth_OK` (`api/client_test.go:41`) | `status=="ok"` | `status=="Healthy"` (PascalCase, `HealthEndpoints.cs:20-23`) |
| 2 | `TestReady_OK` (`api/client_test.go:77`) | (did not exist; `infra status` double-called `/health`) | Calls `/health/ready`; asserts `Status="Ready"` (`HealthEndpoints.cs:24-49`) |
| 3 | `TestReady_503_MapsToDBUnavailable` (`api/client_test.go:98`) | (did not exist) | 503 → `CLIError(CodeDBUnavailable, MarkRetryable=true)` |
| 4 | `TestCertStatus_TenantHeaderInjected` (`api/client_test.go:248`) | Decoded `Certificate{Subject,Issuer,ExpiresAt,Estado}` with fabricated `subject/issuer/expiresAt/estado` json tags | Decodes real `[{id, nombrePropietario, fechaExpiracion, activo, fechaCreacion}]` (`CertificateDtos.cs:3-8`); asserts `ID`, `NombrePropietario`, non-zero `FechaExpiracion` |
| 5 | `TestCertStatus_EmptyList200` (`api/client_test.go:294`) | 404 (assumed, but backend never returns 404 here) | `200 []` decodes to empty slice, no error (`CertificateEndpoints.cs:72-80`) |
| 6 | `TestBootstrap_DuplicateIs400WithProblemDetails` (`api/client_test.go:180`) | Old `TestBootstrap_DuplicateIs409` (gone) | 400 + `application/problem+json` + Spanish sentinel → `CodeTenantDuplicate` (`GlobalExceptionHandler.cs:106-112`, `TenantBootstrapService.cs:67`) |
| 7 | `TestBootstrap_Other400IsBadRequest` (`api/client_test.go:214`) | (did not exist; negative case) | 400 with different Detail ("13 dígitos") → `CodeBootstrapBadReq`, NOT duplicate |
| 8 | `TestTenantCreate_DuplicateIs400WithProblemDetails` (`core/tenant_test.go:278`) | Old test stubbed 409 | End-to-end against `httptest.NewServer`; asserts `CodeTenantDuplicate`, exit 5, alias NOT in config, apiKey NOT in keychain |
| 9 | `TestTenantCreate_Other400IsBadRequest` (`core/tenant_test.go:333`) | (did not exist) | Negative at handler level: RUC inválido → `bootstrap_bad_request`, NOT duplicate |
| 10 | `TestCertStatus_Valid` (`core/cert_test.go:75`) | Old fixtures constructed `Certificate{Subject,Issuer,ExpiresAt,Estado}` | Constructs real DTO with all 5 fields; asserts handler derives `Status="valid"` (not the fabricated `expired` from zero-time) |
| 11 | `TestCertStatus_ExpiringIsCIError` (`core/cert_test.go:108`) | Old assertions | `fechaExpiracion=now+10d` → `expiring` + exit 8 + Renderable |
| 12 | `TestCertStatus_ExpiredIsCIError` (`core/cert_test.go:146`) | Old assertions | `fechaExpiracion=now-1h` → `expired` + exit 9 + Renderable |
| 13 | `TestCertStatus_RevokedIsExpired` (`core/cert_test.go:178`) | (did not exist) | `activo=false` → expired regardless of expiry (consistent with `Activo` as revocation flag) |
| 14 | `TestCertStatus_EmptyListIsCertNotFound` (`core/cert_test.go:204`) | (did not exist; old test assumed 404) | `[]Certificate{}` from fake → `CodeCertNotFound`, exit 4, hint present |
| 15 | `TestInfra_Status_Healthy` (`core/infra_test.go:167`) | Old: called `Health` twice (fabricated readiness) | Calls `/health` once AND `/health/ready` once; asserts `healthHits=1` and `readyHits=1`; compares `Status == "Healthy"` / `"Ready"` (PascalCase) |
| 16 | `TestInfra_Status_ReadyDegraded` (`core/infra_test.go:229`) | (did not exist) | Liveness up, readiness 503 → `Degraded=true`, `Renderable()`, non-zero exit |
| 17 | `TestInfra_Upgrade_Success` (`core/infra_test.go:351`) | Old: backup pre-upgrade was missing | Backup runs BEFORE `.env` is mutated; `RunTo` is called (streaming); `pull` + `up -d` ran; `/health/ready` probed |
| 18 | `TestInfra_Upgrade_BackupFailsAbortsBeforeMutation` (`core/infra_test.go:498`) | (did not exist) | `BILLING_IMAGE_TAG` remains `v1.0.0`; `pull`/`up` NOT called; error code is `db_unavailable` (preserved) |
| 19 | `TestInfra_Backup_Success` (`core/infra_test.go:545`) | Old: `Run` with buffered string | `RunTo` is called (NOT `Run`); file written; `sizeBytes` matches stream length (binary-safe) |
| 20 | `TestInfra_Backup_MidStreamFailureRemovesPartialFile` (`core/infra_test.go:643`) | (did not exist) | `RunTo` returns error mid-stream → NO `sriya-backup-*` file lingers |
| 21 | `TestExitCode_KnownCodes` (`errs/errors_test.go:18`) | Old: 6 cases collapsed in exit 6 | 19 cases pinned: `cert_expiring→8`, `cert_expired→9`, `upgrade_health_timeout→10`, `doctor_check_failed→11`, `confirm_required→2`, `tenant_duplicate→5`, etc. |
| 22 | `TestConfirm_*` (11 tests in `cli/confirm_test.go`) | (did not exist; confirm gate was dead code) | Full TTY×bypass table; renderable sentinel; dry-run gate |

The old `TestBootstrap_DuplicateIs409` is fully removed; a `grep` of `internal/` confirms no
orphan references remain (verified in `apply-progress.md` Batch D and the verify cross-check
section).

## Warnings + Suggestions (verify-report.md) → Backlog

The verify report flagged 2 warnings and 2 suggestions that are **out of scope** for
`sriyactl-v1-fixes` and have been migrated to `openspec/BACKLOG.md`:

- `SRIYACTL-V1-DOD` — close remaining `[ ]` checkboxes in `tasks.md:134` and the
  `Definition of Done` block (cosmetic, no behavior gap)
- `SRIYACTL-SERVICETAG-NOTE` — add a "REMOVE in v2" comment to `ServiceTag` in
  `sriyactl/internal/api/client.go:38-44` (or remove the field entirely in v2)
- `SRIYACTL-409-TEST` — add a test asserting that generic HTTP 409 maps to
  `errs.CodeConflict` in `sriyactl/internal/api/errors.go` (currently unexercised)
- `SRIYACTL-AUDIT-409-DELETION` — audit git history of `sriyactl/` to confirm
  `TestBootstrap_DuplicateIs409` is fully gone and no orphan references remain
  (verify-report suggestion S-2)

The first three come from `verify-report.md` §"Issues Found" (WARNINGS + SUGGESTIONS); the
fourth is the explicit suggestion to add a `git log -S` audit reference for archeology.

## Files Touched (Apply Phase)

```
internal/errs/errors.go
internal/errs/errors_test.go
internal/api/client.go
internal/api/client_test.go
internal/api/testhelpers_test.go
internal/compose/runner.go
internal/cli/confirm.go          (NEW)
internal/cli/confirm_test.go     (NEW)
internal/cli/middleware.go
internal/cli/infra.go
internal/cli/wiring.go
internal/core/cert.go
internal/core/cert_test.go
internal/core/tenant_test.go
internal/core/infra.go
internal/core/infra_test.go
cmd/sriyactl/main.go
.goreleaser.yaml
```

## Out of Scope (Confirmed)

- Backend .NET (`billing/src/`) — no changes
- v2/v3 features, command redesign, new subcommands
- Endpoint routes, bootstrap multipart fields, readonly gate internals, config/secret
  model, handler↔render separation
- `--no-backup` opt-out flag for `infra upgrade` (deferred to a future change)

## Spec Deltas Synced

The four delta specs in `openspec/changes/sriyactl-v1-fixes/specs/` have been merged into
the source-of-truth main specs at `openspec/specs/`. The main specs now reflect the final
post-v1-fixes state:

- `openspec/specs/infra/spec.md` — infra status, infra upgrade, infra restore MODIFIED
- `openspec/specs/tenant/spec.md` — tenant create MODIFIED
- `openspec/specs/cert/spec.md` — cert status MODIFIED
- `openspec/specs/ai-contract/spec.md` — dry-run MODIFIED; payload con centinela, exit
  codes distintos y estables por clase, tests afirman el contrato real ADDED

## SDD Cycle Complete

The change has been fully planned, implemented, verified, and archived. All 4 deltas are
merged; the 4 backlog items are recorded; the change folder is moved to
`openspec/changes/archive/2026-06-06-sriyactl-v1-fixes/`.

---

**Sign-off**: sdd-archive (autonomous) — 2026-06-06
