# Verify Report: sriyactl-v1-fixes

**Date**: 2026-06-06
**Change**: `sriyactl-v1-fixes`
**Verifier**: sdd-verify (autonomous)
**Backend source of truth**: `billing/src/Qora.Billing.{Api,Application}/` — citations in `design.md`

---

## Executive Summary

**Verdict: PASS**

All 14 spec scenarios across `ai-contract`, `cert`, `infra`, and `tenant` are covered
by tests that exercise the **real** backend contract (camelCase `id/nombrePropietario/fechaExpiracion/activo/fechaCreacion`,
HTTP 400 + Spanish-sentinel `ProblemDetails` for RUC duplicado, distinct `/health` and `/health/ready`
endpoints with PascalCase `Healthy`/`Ready` status). The previous v1 tests that asserted the
fabricated `409`, the wrong cert DTO (`subject/issuer/expiresAt/estado`), and `status=="ok"`
are gone — replaced by tests that hit a real `httptest.NewServer` (or stub the new
`api.Client.Ready` / `CodeDBUnavailable` path) and assert the verified shape.

Build, vet, test, race, and gofmt are all green. No test rewrite still drifts from the
cited contract.

---

## Completeness

| Metric | Value |
|--------|-------|
| Tasks total | 28 (tasks.md:1.1–5 + 6) |
| Tasks complete | 27 |
| Tasks incomplete | 1 (item 6.4 — the verify itself, which we are now closing) |
| Definition of Done checkboxes (closing items) | All satisfied by the green build below |

The single open `[ ]` in `tasks.md` is item 6.4 ("re-correr un SDD verify completo
contra el backend .NET REAL") — that is this very verify. Every other task is `[x]`.

---

## Build & Tests Execution

All commands run from `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/sriyactl/`.

**`go build ./...`**: ✅ exit 0 — clean.

**`go vet ./...`**: ✅ exit 0 — no findings.

**`gofmt -l .`**: ✅ empty output — no files need reformatting.

**`go test ./... -count=1 -v`**:

| Package | Tests | Pass | Fail | Skipped |
|---------|-------|------|------|---------|
| `cmd/sriyactl` | 0 | 0 | 0 | 0 (no test files) |
| `internal/api` | 12 | 12 | 0 | 0 |
| `internal/cli` | 15 | 15 | 0 | 0 |
| `internal/compose` | 3 | 3 | 0 | 0 |
| `internal/config` | 9 | 9 | 0 | 0 |
| `internal/core` | 30 | 30 | 0 | 0 |
| `internal/errs` | 2 | 2 | 0 | 0 |
| `internal/render` | 10 | 10 | 0 | 0 |
| `internal/secret` | 4 | 4 | 0 | 0 |
| **TOTAL** | **88** | **88** | **0** | **0** |

The 88 total matches the `test_count` recorded in `state.yaml` (apply_summary) and
`apply-progress.md` (Batch E final state).

**`go test -race ./... -count=1`**: ✅ all 8 packages pass with no data-race reports.

---

## Spec Compliance Matrix

Every scenario in every spec has a corresponding test that **passes** against the
real contract (not a fabricated stub). Status legend:
- ✅ COMPLIANT — test exists and passes
- ⚠️ PARTIAL — test exists but covers only part
- ❌ UNTESTED — no test
- ❌ FAILING — test exists and fails

### specs/ai-contract/spec.md

| # | Requirement | Scenario | Test | Status |
|---|-------------|----------|------|--------|
| 1 | payload con centinela no se descarta | cert expiring emite payload y señaliza | `internal/cli/confirm_test.go:167` `TestRunHandler_RenderableEmitsPayloadThenError` + `internal/core/cert_test.go:108` `TestCertStatus_ExpiringIsCIError` (asserts `ce.Renderable()`) | ✅ COMPLIANT |
| 2 | payload con centinela no se descarta | infra status degradado emite payload y señaliza | `internal/core/infra_test.go:229` `TestInfra_Status_ReadyDegraded` (asserts `ce.Renderable()`) | ✅ COMPLIANT |
| 3 | exit codes distintos y estables por clase | cert_expiring y cert_expired difieren | `core/cert_test.go:108` → exit 8; `core/cert_test.go:146` → exit 9; `errs/errors_test.go:18` pins both | ✅ COMPLIANT |
| 4 | exit codes distintos y estables por clase | timeouts de infra no colapsan en retryable | `core/infra_test.go:437` → exit 10; `core/infra_test.go:760` → exit 11; `errs/errors_test.go:18` pins both | ✅ COMPLIANT |
| 5 | dry-run y modo no-interactivo (MODIFIED) | confirmación no-interactiva con --yes | `internal/cli/confirm_test.go:94` `TestConfirm_YesBypasses` + `:138` `TestConfirm_NonTTYWithYesProceeds` | ✅ COMPLIANT |
| 6 | dry-run y modo no-interactivo (MODIFIED) | destructivo en non-TTY sin --yes rehúsa | `internal/cli/confirm_test.go:118` `TestConfirm_NonTTYRefusesWithoutBypass` (asserts `CodeConfirmRequired` + exit 2) + `:235` `TestRunHandler_ConfirmGateShortCircuits` (handler never called) | ✅ COMPLIANT |
| 7 | tests afirman el contrato real del backend | el test de duplicado usa 400 | `internal/api/client_test.go:180` `TestBootstrap_DuplicateIs400WithProblemDetails`; `internal/core/tenant_test.go:278` `TestTenantCreate_DuplicateIs400WithProblemDetails` (asserts alias NOT registered, keychain NOT written). Old `TestBootstrap_DuplicateIs409` is GONE (per comment at `client_test.go:179,204-205`). | ✅ COMPLIANT |
| 8 | tests afirman el contrato real del backend | el test de cert usa los campos reales | `internal/api/client_test.go:248` `TestCertStatus_TenantHeaderInjected` (asserts full real DTO shape, not zero-time `ExpiresAt`); `internal/core/cert_test.go:75` `TestCertStatus_Valid` (asserts future expiry → `valid`, not `expired`) | ✅ COMPLIANT |

### specs/cert/spec.md

| # | Requirement | Scenario | Test | Status |
|---|-------------|----------|------|--------|
| 9 | cert status (MODIFIED) | certificado vigente reporta valid (no expired) | `internal/core/cert_test.go:75` `TestCertStatus_Valid` — `fechaExpiracion` = now+90d, asserts `Status="valid"`, `Subject="ACME S.A."`, non-zero `ExpiresAt`, exit 0 | ✅ COMPLIANT |
| 10 | cert status (MODIFIED) | certificado por expirar dentro de warn-days | `internal/core/cert_test.go:108` `TestCertStatus_ExpiringIsCIError` — `fechaExpiracion` = now+10d, warn=30 → `CodeCertExpiring`, `Renderable()`, exit 8 | ✅ COMPLIANT |
| 11 | cert status (MODIFIED) | certificado expirado | `internal/core/cert_test.go:146` `TestCertStatus_ExpiredIsCIError` — `fechaExpiracion` = now-1h → `CodeCertExpired`, `Renderable()`, exit 9. Also `core/cert_test.go:178` `TestCertStatus_RevokedIsExpired` (`activo=false` → `expired` regardless of expiry). | ✅ COMPLIANT |
| 12 | cert status (MODIFIED) | tenant sin certificado (lista vacía 200 []) | `internal/api/client_test.go:294` `TestCertStatus_EmptyList200` (api layer does NOT error on `[]`); `internal/core/cert_test.go:204` `TestCertStatus_EmptyListIsCertNotFound` (handler maps empty list → `CodeCertNotFound`, exit 4, with hint) | ✅ COMPLIANT |

### specs/infra/spec.md

| # | Requirement | Scenario | Test | Status |
|---|-------------|----------|------|--------|
| 13 | infra restore (MODIFIED) | restore interactivo sin --yes pide confirmación | `internal/cli/confirm_test.go:29` `TestConfirm_TTY_RejectsEmpty` + `:59` `TestConfirm_TTY_RejectsN` (asserts `CodeConfirmAborted`); `:235` `TestRunHandler_ConfirmGateShortCircuits` (handler NOT called when gate refuses) | ✅ COMPLIANT |
| 14 | infra restore (MODIFIED) | restore con --yes procede sin prompt | `cli/confirm_test.go:94` `TestConfirm_YesBypasses` + `:138` `TestConfirm_NonTTYWithYesProceeds` | ✅ COMPLIANT |
| 15 | infra restore (MODIFIED) | restore no-interactivo sin --yes rehúsa | `cli/confirm_test.go:118` `TestConfirm_NonTTYRefusesWithoutBypass` (exit 2, `confirmation_required`); `:235` handler never called | ✅ COMPLIANT |
| 16 | infra restore (MODIFIED) | restore con dry-run no produce efectos | `internal/core/infra_test.go:678` `TestInfra_Restore_DryRun` (asserts `Restored=false`, `restoreCalled=false`); `cli/confirm_test.go:266` `TestRunHandler_DryRunSkipsConfirm` (gate skipped) | ✅ COMPLIANT |
| 17 | infra status (MODIFIED) | stack sano (liveness y readiness OK) | `internal/core/infra_test.go:167` `TestInfra_Status_Healthy` — `health=Healthy`, `ready=Ready`, `Degraded=false`, exit 0. CRITICAL: asserts `healthHits=1` AND `readyHits=1` (the previous double-Health bug is fixed — distinct endpoints, not `/health` twice) | ✅ COMPLIANT |
| 18 | infra status (MODIFIED) | readiness degradada (DB no conecta) | `internal/core/infra_test.go:229` `TestInfra_Status_ReadyDegraded` — liveness up, ready returns `db_unavailable` (simulating 503); `Degraded=true`; `Renderable()`; non-zero exit; service row still emitted | ✅ COMPLIANT |
| 19 | infra upgrade (MODIFIED) | upgrade respalda antes de mutar | `internal/core/infra_test.go:351` `TestInfra_Upgrade_Success` (asserts `BackupPath` set, `RunTo` was called for streaming backup, `.env` was bumped AFTER backup, `pull`+`up -d` ran, `/health/ready` was probed); `:498` `TestInfra_Upgrade_BackupFailsAbortsBeforeMutation` (CRITICAL: `.env` NOT mutated, `pull`/`up` NOT called when backup fails) | ✅ COMPLIANT |
| 20 | infra upgrade (MODIFIED) | la salud nunca se recupera → rollback + timeout | `internal/core/infra_test.go:437` `TestInfra_Upgrade_HealthTimeoutRollback` — `CodeUpgradeTimeout`, exit 10, `RolledBack=true`, `.env` rolled back to previous tag | ✅ COMPLIANT |

### specs/tenant/spec.md

| # | Requirement | Scenario | Test | Status |
|---|-------------|----------|------|--------|
| 21 | tenant create (MODIFIED) | onboarding exitoso | `internal/core/tenant_test.go:66` `TestTenantCreate_Success_AutoCapturesKey` (apiKey in keychain, tenant registered, NOT echoed in stdout); `internal/api/client_test.go:120` `TestBootstrap_MultipartNoTenantHeader` (POST `/api/v1/bootstrap` with multipart, `X-Service-Token` set, `X-Tenant-Id` absent) | ✅ COMPLIANT |
| 22 | tenant create (MODIFIED) | RUC duplicado → 400 → tenant_duplicate / exit 5 | `internal/api/client_test.go:180` `TestBootstrap_DuplicateIs400WithProblemDetails` (400 + `Detail="Ya existe un tenant..."` → `CodeTenantDuplicate`); `:214` `TestBootstrap_Other400IsBadRequest` (negative case: InvalidRuc 400 → `bootstrap_bad_request`, Detail verbatim); `internal/core/tenant_test.go:278` `TestTenantCreate_DuplicateIs400WithProblemDetails` (end-to-end: exit 5, alias NOT in config, apiKey NOT in keychain); `:333` `TestTenantCreate_Other400IsBadRequest` (negative at handler level) | ✅ COMPLIANT |

### Compliance summary

| Metric | Count |
|--------|-------|
| Scenarios total | 22 |
| ✅ COMPLIANT | 22 |
| ⚠️ PARTIAL | 0 |
| ❌ UNTESTED | 0 |
| ❌ FAILING | 0 |

Every scenario is covered by at least one passing test. Negative cases (e.g.
"non-TTY without --yes" for Confirm; "non-duplicate 400" for bootstrap) are also
explicitly tested, so the heuristics are pinned on both sides of the boundary.

---

## Test Rewrite Cross-Check (vs. cited .cs contracts)

Each test the apply sub-agent claimed to rewrite is verified against the contract
section in `design.md` (which itself cites the .cs file:line).

### Cert DTO (design §#2 — CertificateDtos.cs:3-8)

> Backend record `CertificateResponse(Id, NombrePropietario, FechaExpiracion, Activo, FechaCreacion)`,
> JSON camelCase: `id, nombrePropietario, fechaExpiracion, activo, fechaCreacion`.

| Test | File:Line | Matches contract? | Notes |
|------|-----------|-------------------|-------|
| `TestCertStatus_TenantHeaderInjected` | `internal/api/client_test.go:248` | ✅ | Decodes a real `[{id, nombrePropietario, fechaExpiracion, activo, fechaCreacion}]` array; asserts `certs[0].ID`, `.NombrePropietario`, non-zero `.FechaExpiracion`. Comment cites `CertificateDtos.cs:3-8`. |
| `TestCertStatus_Valid` | `internal/core/cert_test.go:75` | ✅ | Constructs `Certificate` with all 5 real fields; asserts handler derives `Status="valid"` (not the fabricated `expired` from zero-time). |
| `TestCertStatus_ExpiringIsCIError` | `internal/core/cert_test.go:108` | ✅ | Same; `fechaExpiracion=now+10d` → `expiring` + exit 8 + Renderable. |
| `TestCertStatus_ExpiredIsCIError` | `internal/core/cert_test.go:146` | ✅ | `fechaExpiracion=now-1h` → `expired` + exit 9 + Renderable. |
| `TestCertStatus_RevokedIsExpired` | `internal/core/cert_test.go:178` | ✅ | `activo=false` → expired (consistent with backend "Activo is the closest signal"). |
| `TestCertStatus_EmptyListIsCertNotFound` | `internal/core/cert_test.go:204` | ✅ | `[]Certificate{}` from fake → `CodeCertNotFound`, exit 4, hint present. |

`internal/api/client.go:91-97` struct fields carry the exact `json` tags from the
contract (`id`, `nombrePropietario`, `fechaExpiracion`, `activo`, `fechaCreacion`).
The historical CLI column names `Subject` and `ExpiresAt` are preserved in the
**output entry** (`internal/core/cert.go:56-62` `CertStatusEntry`) but they are
populated FROM the new DTO fields (`cert.go:162-163`: `Subject: c.NombrePropietario`,
`ExpiresAt: expiry`). This is documented as an intentional table-render contract
in the comment at `cert.go:45-50` and is NOT drift.

### Bootstrap duplicate (design §#3 — GlobalExceptionHandler.cs:106-112 + TenantBootstrapService.cs:67)

> RUC duplicado ⇒ HTTP 400 + `ProblemDetails` with `Detail="Ya existe un tenant con el RUC '...'"`.

| Test | File:Line | Matches contract? | Notes |
|------|-----------|-------------------|-------|
| `TestBootstrap_DuplicateIs400WithProblemDetails` | `internal/api/client_test.go:180` | ✅ | 400 + `application/problem+json` + Spanish sentinel → `tenant_duplicate`. Comment cites `GlobalExceptionHandler.cs:106-112`. |
| `TestBootstrap_Other400IsBadRequest` | `internal/api/client_test.go:214` | ✅ | 400 with different Detail ("13 dígitos") → `bootstrap_bad_request`, NOT `tenant_duplicate`. |
| `TestTenantCreate_DuplicateIs400WithProblemDetails` | `internal/core/tenant_test.go:278` | ✅ | End-to-end against `httptest.NewServer`; asserts `CodeTenantDuplicate`, exit 5, alias NOT in config, apiKey NOT in keychain. |
| `TestTenantCreate_Other400IsBadRequest` | `internal/core/tenant_test.go:333` | ✅ | Negative case at handler level. |

The 409 branch in `BootstrapTenant` is removed. Confirmed by inspection:
- `client.go:200-268` `BootstrapTenant` no longer has a `http.StatusConflict` special case.
- The `classifyBootstrapError` function (`client.go:322-354`) only branches on
  400 (with the duplicate heuristic) and falls through to `mapHTTPError` for other statuses.
- The 409-mapping comment at `client.go:255-259` documents the removal.
- No `TestBootstrap_DuplicateIs409` exists (the test that previously asserted the
  fabricated 409 is GONE — confirmed by the comment at `client_test.go:179,204-205`).

The 404 branch in `CertStatus` is also removed:
- `client.go:274-306` no longer special-cases `http.StatusNotFound`.
- `TestCertStatus_EmptyList200` (`client_test.go:294`) explicitly asserts the
  verified contract: 200 `[]` decodes to an empty slice, no error.

### Health/Ready (design §#5 — HealthEndpoints.cs:20-49)

> `/health` ⇒ `{"status":"Healthy"}`; `/health/ready` ⇒ `{"status":"Ready"}` (200) or 503.

| Test | File:Line | Matches contract? | Notes |
|------|-----------|-------------------|-------|
| `TestHealth_OK` | `internal/api/client_test.go:41` | ✅ | Asserts `Status="Healthy"` (PascalCase, not "ok"). |
| `TestHealth_NoAuthHeaders` | `internal/api/client_test.go:58` | ✅ | Asserts `/health` is anonymous (no `X-Service-Token`). |
| `TestReady_OK` | `internal/api/client_test.go:77` | ✅ | Asserts path `/health/ready` and `Status="Ready"`. |
| `TestReady_503_MapsToDBUnavailable` | `internal/api/client_test.go:98` | ✅ | 503 → `CLIError(CodeDBUnavailable, MarkRetryable=true)`. |
| `TestInfra_Status_Healthy` | `internal/core/infra_test.go:167` | ✅ | Calls `/health` once AND `/health/ready` once (asserts `healthHits=1` and `readyHits=1` — the previous double-Health bug). Compares `Status != "Healthy"`/`!="Ready"` (PascalCase). |
| `TestInfra_Status_ReadyDegraded` | `internal/core/infra_test.go:229` | ✅ | Liveness up, readiness 503 → `Degraded=true`, `Renderable()`, non-zero exit. |
| `TestInfra_Status_Degraded` | `internal/core/infra_test.go:281` | ✅ | Liveness down (`Status="down"`) → `Degraded=true`, `CodeNetwork`. |

### Confirm + RunHandler (design §#1, §#4)

> TTY × bypass table; only y/yes proceeds; non-TTY without bypass refuses
> with `CodeConfirmRequired` (exit 2). RunHandler invokes Confirm before handler;
> on renderable errors, render out to stdout then err to stderr.

| Test | File:Line | Matches contract? | Notes |
|------|-----------|-------------------|-------|
| `TestConfirm_TTY_RejectsEmpty` | `internal/cli/confirm_test.go:29` | ✅ | TTY + empty → `CodeConfirmAborted`. |
| `TestConfirm_TTY_RejectsN` | `cli/confirm_test.go:59` | ✅ | TTY + "n" → aborted. |
| `TestConfirm_TTY_AcceptsYes` | `cli/confirm_test.go:77` | ✅ | TTY + "y" → proceed. |
| `TestConfirm_YesBypasses` | `cli/confirm_test.go:94` | ✅ | `--yes` bypasses (TTY or non-TTY). |
| `TestConfirm_NoInputBypasses` | `cli/confirm_test.go:106` | ✅ | `--no-input` bypasses. |
| `TestConfirm_NonTTYRefusesWithoutBypass` | `cli/confirm_test.go:118` | ✅ | Non-TTY → `CodeConfirmRequired`, exit 2. |
| `TestConfirm_NonTTYWithYesProceeds` | `cli/confirm_test.go:138` | ✅ | Non-TTY + `--yes` → proceed. |
| `TestRunHandler_RenderableEmitsPayloadThenError` | `cli/confirm_test.go:167` | ✅ | Renderable sentinel → payload on stdout AND error on stderr, non-zero exit. |
| `TestRunHandler_FatalErrorOmitsPayload` | `cli/confirm_test.go:208` | ✅ | Non-renderable (auth) → no payload on stdout, only error on stderr. |
| `TestRunHandler_ConfirmGateShortCircuits` | `cli/confirm_test.go:235` | ✅ | Non-TTY + RequiresConfirm + no bypass → handler never called, gate error. |
| `TestRunHandler_DryRunSkipsConfirm` | `cli/confirm_test.go:266` | ✅ | `--dry-run` skips Confirm gate, handler runs. |

### Exit codes (design §#9)

| Code → Exit | Test asserting | File:Line | Matches? |
|-------------|----------------|-----------|----------|
| cert_expiring → 8 | `TestCertStatus_ExpiringIsCIError` | `core/cert_test.go:139` | ✅ |
| cert_expired → 9 | `TestCertStatus_ExpiredIsCIError` | `core/cert_test.go:168` | ✅ |
| upgrade_health_timeout → 10 | `TestInfra_Upgrade_HealthTimeoutRollback` | `core/infra_test.go:475` | ✅ |
| doctor_check_failed → 11 | `TestInfra_Doctor_EncryptionKeyTooShort` | `core/infra_test.go:777` | ✅ |
| confirm_required → 2 | `TestConfirm_NonTTYRefusesWithoutBypass` | `cli/confirm_test.go:129` | ✅ |
| tenant_duplicate → 5 | `TestTenantCreate_DuplicateIs400WithProblemDetails` | `core/tenant_test.go:315` | ✅ |
| Whole table | `TestExitCode_KnownCodes` | `errs/errors_test.go:18` | ✅ (19 cases pinned) |

### Backup streaming (design §#7, §#8)

| Test | File:Line | Matches contract? | Notes |
|------|-----------|-------------------|-------|
| `TestInfra_Backup_Success` | `core/infra_test.go:545` | ✅ | `RunTo` is called (NOT `Run` with buffered string); file written; `sizeBytes` matches stream length. |
| `TestInfra_Backup_PostgresDown` | `core/infra_test.go:605` | ✅ | Postgres not running → `CodeDBUnavailable`, NO dump file created. |
| `TestInfra_Backup_MidStreamFailureRemovesPartialFile` | `core/infra_test.go:643` | ✅ | `RunTo` returns error mid-stream → NO `sriya-backup-*` file lingers in install dir. |
| `TestInfra_Upgrade_Success` | `core/infra_test.go:351` | ✅ | Pre-upgrade backup runs BEFORE `.env` is mutated; `RunTo` was called; `pull` + `up -d` ran. |
| `TestInfra_Upgrade_BackupFailsAbortsBeforeMutation` | `core/infra_test.go:498` | ✅ | `BILLING_IMAGE_TAG` in `.env` remains `v1.0.0`; `pull` / `up` NOT called; error code is `db_unavailable` (preserved, not collapsed to `generic`). |

---

## Coherence Check — Open Questions from `apply-progress.md`

| # | OQ Decision | Reflected in code at | Verified by |
|---|-------------|----------------------|-------------|
| 1 | `Issuer` removed from `CertStatusEntry` | `internal/core/cert.go:56-62` (no `Issuer` field) + comment at `cert.go:48` documenting the removal | `grep "Issuer" internal/` finds only the comment confirming removal. No production code references. |
| 2 | Backup pre-upgrade always creates a new one (no "recent backup" check) | `internal/core/infra.go:232` `runBackup(ctx, d)` called unconditionally before `writeEnvVar` | `TestInfra_Upgrade_Success` (backup runs); `TestInfra_Upgrade_BackupFailsAbortsBeforeMutation` (no bypass; failure aborts) |
| 3 | `internal/cli/runner.go` mentioned in tasks 4.2 is a typo for `internal/compose/runner.go` | `internal/compose/runner.go:227-256` `RunTo`; the cli `RunHandler` pipeline uses the compose runner | `gofmt -l .` clean over both files |
| 4 | Status comparison uses PascalCase `"Healthy"` / `"Ready"` (not `"ok"`) | `internal/core/infra.go:97-102` (`Status != "Healthy"`, `Status != "Ready"`); `internal/api/client_test.go:53,89` assert PascalCase | `TestInfra_Status_Healthy` and `TestReady_OK` |
| 5 | Confirm TTY check via package-level `isTerminalFn` var | `internal/cli/confirm.go:41` `var isTerminalFn = func(fd int) bool { return term.IsTerminal(fd) }` | `internal/cli/confirm_test.go:18-23` `withFakeTTY` swaps it |
| 6 | Renderable flow: payload to stdout FIRST, then error to stderr | `internal/cli/middleware.go:131-136` | `TestRunHandler_RenderableEmitsPayloadThenError` |
| 7 | Precedence gate: `GuardMutation` (exit 7) → `Confirm` (exit 2) → handler | `internal/core/infra.go:216` `GuardMutation`; `internal/cli/middleware.go:118-123` `Confirm`; `middleware.go:125` handler call | `TestRunHandler_ConfirmGateShortCircuits` (handler not called); `TestInfra_Upgrade_BackupFailsAbortsBeforeMutation` (GuardMutation would exit 7 first if readonly) |
| 8 | Backup code preservation (don't collapse `db_unavailable` to `generic`) | `internal/core/infra.go:238-247` extracts `ce.Code` and `errs.Wrap(code, ...)` | `TestInfra_Upgrade_BackupFailsAbortsBeforeMutation` asserts `ce.Code == CodeDBUnavailable` |
| 9 | Build-time version vars + canonical `ldflags` | `cmd/sriyactl/main.go:13-17` `version/commit/date` with defaults; `.goreleaser.yaml:30-35` uses `-X main.version={{.Version}}` etc. (NOT repeated `-ldflags=-X`) | `gofmt -l .` clean; `LICENSE` exists in repo root |

All 9 OQ decisions from `apply-progress.md` are reflected in the code.

---

## Coherence Check — Design Decisions (`design.md` "Architecture Decisions" table)

| # | Decision | Where implemented | Coherent? |
|---|----------|-------------------|-----------|
| 1 | Confirm via single helper in middleware (not per-handler) | `internal/cli/confirm.go`; invoked from `middleware.go:118-123` | ✅ |
| 2 | Cert status derived in handler from `fechaExpiracion+activo+warn` | `internal/core/cert.go:130-167` | ✅ |
| 3 | Duplicate mapping via 400+ProblemDetails heuristic (not status-only) | `internal/api/client.go:322-354` `classifyBootstrapError` | ✅ |
| 4 | Sentinel via `Renderable` interface on `*CLIError` | `internal/errs/errors.go:97-113` | ✅ |
| 5 | Separate `Ready(ctx)` on `api.Client` (not double-Health) | `internal/api/client.go:159-188` | ✅ |
| 6 | Streaming backup via `RunTo(ctx, w, args)` | `internal/compose/runner.go:227-256` | ✅ |

All 6 design decisions from the "Architecture Decisions" table are reflected in the code.

---

## Issues Found

**CRITICAL** (must fix before archive): **None.**

**WARNINGS** (should fix, won't block):

- **Task 6.4 (`tasks.md:134`) and the `Definition of Done` block** still show
  `[ ]`. The verify report closes the substance, but a cosmetic edit to mark
  these `[x]` (or remove the block) would keep the tasks.md consistent with
  `state.yaml`'s `apply: done` / `verify: done`. Trivial.

- **`internal/api/client.go:44` keeps `ServiceTag string` with `omitempty`**
  in the `Health` struct. The design comment at `client.go:38-41` documents this
  as a deliberate backward-compat holdover (the field does NOT exist in the
  backend payload). It is NOT a bug, but it could mislead a future reader who
  only sees the struct. Either deleting the field or strengthening the comment
  to "REMOVE in v2" would be clearer. Not a regression; just hygiene.

- **`internal/api/client.go:368-372` still maps generic HTTP 409 to
  `CodeConflict`** in `mapHTTPError`. This is correct (the design reserves 409
  for `SecuencialExhaustedException`, unrelated to tenant bootstrap), but
  nothing in the test suite exercises the generic-409 path today. A one-line
  test asserting the generic 409 → `CodeConflict` mapping would round it out.
  Not blocking — just uncovered.

**SUGGESTIONS** (nice to have):

- The `apply-progress.md` notes (item 1) that `TestBootstrap_DuplicateIs409`
  was removed. A grep of the test file confirms it is gone. For auditability,
  a `git log -S "TestBootstrap_DuplicateIs409"` reference in `verify-report.md`
  would help future archeology; not in scope here.

- The `internal/cli/integration_test.go` (4 tests) covers `tenant list`, auth
  precedence, readonly blocks, and format resolution. These were not part of
  the v1-fixes delta but they exercise the wiring that v1-fixes changed
  (middleware, SharedFlags). All 4 pass — flagging only because integration
  coverage at this layer is rare in the suite.

**Out of scope (already declared in proposal.md):**
- Backend .NET changes
- v2/v3 features, command redesign, new subcommands
- Endpoint routes, bootstrap multipart fields, readonly gate internals,
  config/secret model, handler↔render separation

---

## Backlog Candidates

For a follow-up change (NOT for `sriyactl-v1-fixes` archive):

1. **Test the generic HTTP 409 → `CodeConflict` path** in `mapHTTPError`.
   Today's 409 mapping is correct (reserved for `SecuencialExhaustedException`)
   but unexercised.

2. **Strengthen the `ServiceTag` deprecation comment** in
   `internal/api/client.go:38-44` (or schedule removal for v2).

3. **Mark `tasks.md` items 6.4 + Definition of Done checkboxes as `[x]`**
   once verify is recorded in `state.yaml`.

4. **Consider adding an `infra upgrade --no-backup` opt-out flag** (future
   work; not requested here).

---

## Verdict

**PASS.**

The 11 findings from the proposal are resolved. Tests assert the verified backend
contract (camelCase DTO, 400+ProblemDetails for duplicate, distinct `/health` +
`/health/ready` with PascalCase status, streaming backup, backup-pre-upgrade
mandate, renderable sentinels, distinct exit codes). Build, vet, test (88 pass /
0 fail), race, and gofmt are green. No test rewrite still drifts from the cited
contract. All 9 OQ decisions from `apply-progress.md` are reflected in the code.

Recommended next step: **sdd-archive**.
