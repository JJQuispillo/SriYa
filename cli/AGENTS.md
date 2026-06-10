# AGENTS.md — sriyactl AI contract

This file is the canonical entry point for AI agents (LLMs, MCP clients, automation
scripts) consuming `sriyactl` (now living at `billing/cli/` in the monorepo).
It mirrors the ai-contract spec at `openspec/changes/sriyactl/specs/ai-contract/spec.md`
and is kept short so it can be inlined into a system prompt or fetched on demand.

All Go toolchain commands (go build, go test, go vet) should be run from the `cli/`
directory, e.g. `cd cli && go build ./...`.

## TL;DR for agents

```bash
# Always non-interactive, always parseable.
sriyactl <subcommand> --output json
```

The default output format is auto-detected from the TTY: when stdout is a pipe (the
common case in agent pipelines), the CLI emits JSON automatically. You can always
force it with `--output json` or `--output yaml`.

## Output envelope

Every successful command emits:

```json
{
  "schemaVersion": "1.0",
  "kind": "<CommandName>",
  "data": { /* typed payload, see --help */ }
}
```

`schemaVersion` is a stability contract. The CLI will refuse to silently change
existing field shapes; breaking changes bump the version. New `kind` values may be
added at any time.

## Errors

When something fails, the CLI emits a single JSON object on stderr (or stdout when
`--output json` is set):

```json
{
  "error": {
    "code": "<stable_code>",
    "message": "human-readable explanation",
    "hint": "actionable next step (when available)",
    "retryable": false
  }
}
```

Stable error codes (extend, do not rename):

| Code | Meaning | Exit |
|------|---------|------|
| `generic` | unspecified error | 1 |
| `usage` | bad flags / args | 2 |
| `auth_invalid` | bad or missing credentials | 3 |
| `not_found` | resource does not exist | 4 |
| `tenant_not_found` | alias not registered in context | 4 |
| `cert_not_found` | tenant has no cert | 4 |
| `conflict` | duplicate / already exists | 5 |
| `tenant_duplicate` | RUC or alias already used | 5 |
| `network` | transient backend / network | 6 |
| `db_unavailable` | Postgres not running | 6 |
| `cert_expiring` | cert expires within `--warn-days` | 6 |
| `cert_expired` | cert already expired | 6 |
| `upgrade_health_timeout` | upgrade's /health never recovered | 6 |
| `doctor_check_failed` | one or more preflight checks failed | 6 |
| `install_dir_invalid` | install dir missing .env / docker-compose.yml | 4 |
| `readonly_blocked` | mutating cmd blocked by SRIYACTL_READONLY=1 | 7 |

`retryable: true` indicates that retrying the same command may succeed (e.g. transient
network). Agents SHOULD respect this and either retry with backoff or surface the
retryability to a human.

## Read-only mode

Agents operating in CI / sandbox / preview environments should set
`SRIYACTL_READONLY=1`. Every mutating command (anything that calls a POST/PUT/DELETE
endpoint, shells into the install dir, or mutates the keychain / config) will fail
with `code: readonly_blocked` and exit 7 **before any side effect**.

Read-only commands (`infra status`, `tenant list`, `tenant current`, `cert status`)
keep working.

## Dry-run

Mutating commands accept `--dry-run`. The CLI returns the same envelope shape with
a `Kind` of `*Plan` and the data describing the action. Nothing is executed.

```bash
sriyactl tenant create --alias acme --ruc ... --cert ./a.p12 --dry-run --output json
```

```json
{ "schemaVersion": "1.0", "kind": "TenantCreatePlan", "data": { "alias": "acme", ... } }
```

## Recommended agent loop

1. **Probe**: `sriyactl infra status --output json` — confirm the stack is reachable.
2. **Scope**: read `data.kind` and walk `data.*` instead of parsing free text.
3. **Mutate cautiously**: prefer `--dry-run` first, then re-run without it. For
   destructive ops, set `SRIYACTL_READONLY=0` only when the user has approved the action.
4. **Handle errors**: parse `error.code`; if `retryable: true`, retry with backoff.
5. **Never trust stdout in pipe mode**: use `--output json` explicitly even when
   piped, so a TTY-detection regression does not silently change your parser's input.

## Versioning

The CLI follows [SemVer](https://semver.org/). The `schemaVersion` of the output
envelope is bumped on breaking wire-format changes. See [README.md](./README.md) for
the full command reference and install instructions.
