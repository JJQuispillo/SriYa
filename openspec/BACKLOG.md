# Project Backlog — Qora Billing / sriya

Post-archive follow-ups that fall **outside the scope of a completed change** are
migrated here so they are not lost. Each item should be promoted to a new
SDD change (`/sdd-new <name>`) when the team picks it up.

---

## SRIYACTL-CI — Enable `-race` and coverage in CI for `sriyactl`

- **Origin change**: `sriyactl-cli` (archived 2026-06-06)
- **Severity**: info
- **Source**: `openspec/changes/archive/2026-06-06-sriyactl-cli/verify-report.md` (S-2)
- **What**: `go test -race ./...` and `go test -coverprofile=cover.out ./...` are
  not run in CI today. They were validated locally during verify (race clean,
  68/68 pass) but should be enforced on every push.
- **Why**: Catches data races and surfaces coverage deltas over time. The CLI
  has no other CI gating besides `go build` + `go test`.
- **Where to implement**: GitHub Actions workflow file
  `sriyactl/.github/workflows/ci.yml` (or equivalent) — add `-race -coverprofile`
  to the test step and upload the coverage artifact.
- **Acceptance**: every PR to `sriyactl/` runs `go test -race -coverprofile=cover.out ./...`;
  coverage delta is posted (or the artifact is uploaded). Build still green.
- **Estimated effort**: ~30 minutes (one workflow file, optional badge).

---

## BACKEND-ERROR-CODE — Add stable `code` field to backend error envelope

- **Origin change**: `sriyactl-cli` (archived 2026-06-06) — recorded there as task 8.6
- **Severity**: info
- **Inherited from**: `sri-emision-atomicidad-pr1b` (recorded as `F-EMI-EF-CLI-BACKEND-CODE`)
- **Source**: `openspec/changes/archive/2026-06-06-sriyactl-cli/design.md` §"Known Limitations (v1)"
  and `tasks.md` §"Backend follow-up"
- **What**: Add a stable, machine-readable `code: string` property to the backend
  error response envelope (e.g. `tenant_duplicate`, `cert_invalid`,
  `password_mismatch`, `cert_not_found`, `install_dir_invalid`).
- **Why**: The CLI v1 heuristically maps backend 400s to `tenant_duplicate`
  by string-matching the Spanish response body. That is accurate-but-fragile and
  will break if the backend copy changes. A stable `code` lets the CLI drop the
  heuristic and route on a known set of error classes, also enabling cleaner
  exit-code mapping and better AI-agent behavior.
- **Where to implement**:
  - `src/Qora.Billing.Api/Middleware/GlobalExceptionHandler.cs` — add `code`
    to the envelope DTO.
  - `src/Qora.Billing.Domain/Exceptions/BillingDomainException*.cs` — declare
    the `Code` on each exception subclass.
  - Concrete endpoints that surface business errors: `BootstrapEndpoints.cs`
    (400 → `tenant_duplicate`), `CertificateEndpoints.cs` (400 →
    `cert_invalid`/`cert_expired`).
- **Acceptance**: backend error response is `{ "error": { "code": "...", "message": "...", "details": [...] } }`;
  each `BillingDomainException` subclass sets a stable `code`. After deploy, the
  CLI can drop `internal/api/errors.go:mapHTTPError` string heuristics and route
  on the `code` field directly.
- **Estimated effort**: 0.5–1 day (touches middleware + each exception type +
  one CLI refactor in a follow-up change).
- **Follow-up CLI change (separate)**: `sriyactl-v2-error-codes` — drops the
  heuristic, consumes the new `code` field, adds coverage for the new paths.

---

## SRIYACTL-V1-DOD — Close remaining cosmetic checkboxes in `tasks.md`

- **Origin change**: `sriyactl-v1-fixes` (archived 2026-06-06)
- **Severity**: cosmetic (no behavior gap)
- **Source**: `openspec/changes/archive/2026-06-06-sriyactl-v1-fixes/verify-report.md`
  §"Issues Found" → WARNINGS (item 1) and `state.yaml:109-112` (FU-1)
- **What**: Mark the remaining `[ ]` checkboxes in `sriyactl/openspec/.../tasks.md:134`
  ("Re-correr un SDD verify completo") and the `## Definition of Done` block at the
  bottom of the file as `[x]`. The verify itself (`verify-report.md`) has already been
  recorded in `state.yaml` (`verify: done`), so these are stale.
- **Why**: The verify-report closes the substance; the cosmetic edit keeps `tasks.md`
  consistent with `state.yaml` and makes the archive self-explanatory.
- **Where to implement**: `sriyactl/openspec/changes/sriyactl-v1-fixes/tasks.md` (or
  the equivalent path in the archived copy). NOTE: since the change is now archived,
  the practical target is the archived copy at
  `openspec/changes/archive/2026-06-06-sriyactl-v1-fixes/tasks.md` — the cosmetic
  fix has no functional effect.
- **Acceptance**: All checkboxes in `Definition of Done` and item 6.4 read `[x]` in
  the archived `tasks.md`.
- **Estimated effort**: < 5 minutes.

---

## SRIYACTL-SERVICETAG-NOTE — Strengthen `ServiceTag` deprecation comment

- **Origin change**: `sriyactl-v1-fixes` (archived 2026-06-06)
- **Severity**: info / hygiene
- **Source**: `openspec/changes/archive/2026-06-06-sriyactl-v1-fixes/verify-report.md`
  §"Issues Found" → WARNINGS (item 2)
- **What**: `sriyactl/internal/api/client.go:38-44` keeps `ServiceTag string` with
  `omitempty` in the `Health` struct, even though the backend does not return it. The
  current comment documents this as a deliberate backward-compat holdover. Either
  add a louder "REMOVE in v2" comment, or schedule removal in the next v2 change.
- **Why**: Avoids misleading future readers who only see the struct field; clarifies
  intent that this is dead, not a contract gap.
- **Where to implement**: `sriyactl/internal/api/client.go:38-44`. If removal is
  preferred, also remove the comment-only references in tests (none currently
  assert on the field).
- **Acceptance**: Either (a) the comment clearly says "REMOVE in v2 — field not
  present in backend payload" with a TODO tag, or (b) the field is removed
  entirely in a `sriyactl-v2-*-cleanup` change.
- **Estimated effort**: < 15 minutes.

---

## SRIYACTL-409-TEST — Add test for generic HTTP 409 → `CodeConflict` mapping

- **Origin change**: `sriyactl-v1-fixes` (archived 2026-06-06)
- **Severity**: test coverage gap (no behavior bug)
- **Source**: `openspec/changes/archive/2026-06-06-sriyactl-v1-fixes/verify-report.md`
  §"Issues Found" → WARNINGS (item 3) and `state.yaml:114-117` (FU-2)
- **What**: `sriyactl/internal/api/errors.go:368-372` (`mapHTTPError`) still maps
  generic HTTP 409 to `CodeConflict`. The mapping is correct (the only 409 in the
  backend is `SecuencialExhaustedException`, unrelated to tenant bootstrap), but
  nothing in the test suite exercises this path today.
- **Why**: Pin the generic 409 path on both sides of the boundary. Future
  refactors of `mapHTTPError` should not silently regress it.
- **Where to implement**: `sriyactl/internal/api/client_test.go` — add a test
  similar to `TestBootstrap_Other400IsBadRequest` that stubs a generic 409 (no
  ProblemDetails, no Spanish sentinel) and asserts `errs.CodeConflict` is
  returned with a non-zero exit code.
- **Acceptance**: New test exists, passes, asserts `CodeConflict` for a generic
  409; full test suite still green.
- **Estimated effort**: ~30 minutes.

---

## SRIYACTL-AUDIT-409-DELETION — Audit git history of `TestBootstrap_DuplicateIs409` removal

- **Origin change**: `sriyactl-v1-fixes` (archived 2026-06-06)
- **Severity**: archeology / audit (no behavior gap)
- **Source**: `openspec/changes/archive/2026-06-06-sriyactl-v1-fixes/verify-report.md`
  §"Issues Found" → SUGGESTIONS (S-2) and `state.yaml:118-120` (FU-3)
- **What**: `apply-progress.md` notes that `TestBootstrap_DuplicateIs409` (the test
  that asserted the fabricated 409 mapping) was removed. A `grep` confirms it is
  gone. For auditability, a `git log -S "TestBootstrap_DuplicateIs409"` in
  `sriyactl/` should yield a single commit that removed it (and any orphan
  references).
- **Why**: Future archeology. If a regression resurfaced the 409 mapping, the
  blame would be unambiguous.
- **Where to implement**: A one-liner in the archived `verify-report.md` (or a
  new section in `sriyactl/CHANGELOG.md` once the repo is released) citing the
  commit SHA and the fact that the test is fully removed with no orphan
  references.
- **Acceptance**: An audit log entry exists pointing at the commit that removed
  `TestBootstrap_DuplicateIs409`; a `grep` of `sriyactl/internal/` for
  `DuplicateIs409` returns zero results.
- **Estimated effort**: < 10 minutes.

---

_This backlog is the single source of truth for post-archive work that does not
belong to any active change folder. Promote items to a new SDD change before
implementing them — do not start work directly from here._
