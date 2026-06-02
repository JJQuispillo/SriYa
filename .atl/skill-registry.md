# Skill Registry - billing

**Orchestrator use only.** Read this registry once per session to resolve skill paths, then pass pre-resolved paths directly to each sub-agent's launch prompt. Sub-agents receive the path and load the skill directly — they do NOT read this registry.

> Refreshed by sdd-init on 2026-06-02 (post SRI-only open-source pivot).

## User Skills

| Trigger | Skill | Path | Relevance |
|---------|-------|------|-----------|
| Generate API reference docs from .NET endpoints, DTOs, validators | docs-api-reference | `~/.claude/skills/docs-api-reference/SKILL.md` | HIGH — .NET 9 Minimal API + Swagger/OpenAPI |
| Generate Diataxis technical docs from source (.NET supported) | docs-technical | `~/.claude/skills/docs-technical/SKILL.md` | HIGH — .NET project technical docs |
| Generate internal docs (runbooks, onboarding, SLAs, incident templates) | docs-internal | `~/.claude/skills/docs-internal/SKILL.md` | MEDIUM — ops/onboarding for SRI integration |
| Manage Docusaurus site (scaffold, migrate, build, version) | docs-site-manage | `~/.claude/skills/docs-site-manage/SKILL.md` | LOW — only if a docs site is stood up |
| Build distinctive frontend UIs / components | frontend-design | `~/.claude/skills/frontend-design/SKILL.md` | NONE — backend API, no frontend here |
| Any Supabase task | supabase | `~/.claude/skills/supabase/SKILL.md` | NONE — uses PostgreSQL via EF Core + Npgsql, not Supabase |
| AWS Amplify Gen 2 full-stack workflows | amplify-workflow | `~/.claude/skills/amplify-workflow/SKILL.md` | NONE — not used |
| Create native MS Access .accdb databases | ms-access | `~/.claude/skills/ms-access/SKILL.md` | NONE — not applicable |
| Build a CV/resume | cv-builder | `~/.claude/skills/cv-builder/SKILL.md` | NONE — not applicable |
| Flutter UI/animations/state/setup | flutter-* (expert, ui-ux, animations, adaptive-ui) | `~/.claude/skills/flutter-*/SKILL.md` | NONE — Flutter, irrelevant to .NET backend |

### Plugin / built-in skills (most relevant)

| Trigger | Skill | Relevance |
|---------|-------|-----------|
| Verify a change works by running the app | verify | MEDIUM — manual change verification |
| Review the current diff for bugs / cleanups | code-review | MEDIUM — diff review |
| Simplify changed code | simplify | MEDIUM — refactor cleanup |
| Security review of pending changes | security-review | MEDIUM — SRI signing / crypto / multi-tenant auth |
| Conventional-commit message generation | commit-commands:commit / caveman-commit | MEDIUM — repo uses Conventional Commits (Spanish) |
| Configure settings.json (permissions, hooks, env) | update-config | LOW |
| Persistent memory (always active) | engram:memory | ALWAYS ACTIVE |

## Project-Level Skills

No project-level skills found in `.claude/skills/`, `.gemini/skills/`, `.agent/skills/`, or `skills/`.

## Project Conventions

| File | Path | Notes |
|------|------|-------|
| CLAUDE.md (monorepo root) | `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/CLAUDE.md` | Project Planner Agent conventions. Spanish-first: generated docs in Spanish (technical terms in English). |
| .editorconfig | `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/billing/.editorconfig` | 4-space indent, LF, file-scoped namespaces (warning), `using` outside namespace, `var` preferred, private fields `_camelCase`, System directives sorted first. |
| Directory.Build.props | `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/billing/Directory.Build.props` | `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `NoWarn=NU1605`. |
| CI workflow | `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/billing/.github/workflows/ci.yml` | Build (Release) → Test (UnitTests, coverage) → Lint (`dotnet format --verify-no-changes`). Triggers on `billing/**` paths. |

No `AGENTS.md`/`agents.md`/`.cursorrules`/`GEMINI.md`/`copilot-instructions.md` at project root.

## SDD Skills (managed separately)

Available at `~/.claude/skills/sdd-*/SKILL.md`: sdd-init, sdd-explore, sdd-propose, sdd-spec, sdd-design, sdd-tasks, sdd-apply, sdd-verify, sdd-archive.

## Notes

- .NET 9 / ASP.NET Core Minimal API microservice, Clean Architecture (Api → Application → Domain ← Infrastructure).
- Single domain now: **SRI electronic invoicing (Ecuador)**. The SaaS/payments layer (Stripe/PlacetoPay, Plan, Subscription, UsageRecord, quotas, manual payments) was fully REMOVED in the open-source self-hosted pivot — no references remain in `src/`.
- Multi-tenant retained only for isolation/auth: `Tenant` + `ApiKey` (API-key auth) + service-to-service token auth.
- CQRS via **MediatR 12.4.1** (downgraded from 14.x to stay on the last MIT/free version) + FluentValidation pipeline behaviors (LoggingBehavior, ValidationBehavior).
- Testing: **xUnit + FluentAssertions + Moq + coverlet**. UnitTests runs in CI; IntegrationTests project also present.
- Most relevant non-SDD skills: **docs-api-reference** and **docs-technical**.
