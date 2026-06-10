# sriyactl

Day-2 ops CLI for the [SriYa/Qora](https://github.com/JJQuispillo/billing) self-hosted stack.
Wraps `docker compose` and the SriYa HTTP API behind a single, auditable, AI-friendly interface.

> Status: **v1** — covers `infra`, `tenant`, and `cert status`. v2 will add `sriyactl mcp`,
> document commands, and apikey management.

## Install

### Homebrew (macOS / Linux)

```bash
brew install JJQuispillo/tap/sriyactl
```

### Binary release

Download the latest tarball from the [Releases](https://github.com/JJQuispillo/sriyactl/releases)
page. macOS / Linux:

```bash
curl -L https://github.com/JJQuispillo/sriyactl/releases/latest/download/sriyactl_<version>_<os>_<arch>.tar.gz \
  | tar -xz -C /usr/local/bin sriyactl
chmod +x /usr/local/bin/sriyactl
```

### From source

```bash
go install github.com/JJQuispillo/sriyactl/cmd/sriyactl@latest
```

## Configure

`sriyactl` reads its config from `~/.config/sriyactl/config.toml`. On a fresh install
the file does not exist; the CLI auto-creates it on first mutation.

Minimal config:

```toml
current_context = "prod"
current_tenant  = "acme"

[contexts.prod]
url = "https://sri.example.com"
service_token_ref = "keychain"   # always "keychain" in v1

[tenants.prod.acme]
id  = "00000000-0000-0000-0000-000000000001"
ruc = "1790000000001"
env = "prod"
```

**Secrets never live in this file.** The service token is stored in the OS keychain
under `sriyactl/<context>`; per-tenant API keys under `sriyactl/<context>/<tenant-alias>`.

For CI / headless contexts, set the env var instead:

```bash
export SRIYACTL_SERVICE_TOKEN="..."
export SRIYACTL_API_KEY="..."        # per-tenant, optional
```

## Interactive TUI

`sriyactl` with no arguments launches an interactive terminal UI when stdout is a TTY.
Use `sriyactl ui` to force the TUI (even in pipes) or set `SRIYACTL_NO_TUI=1` to
disable it in scripting environments.

### Menu navigation

| Key | Action |
|-----|--------|
| ↑ / k | Move cursor up |
| ↓ / j | Move cursor down |
| Enter | Open selected screen |
| Esc / q | Go back / quit |
| r | Refresh current screen |

### Screens

| Screen | Description |
|--------|-------------|
| Dashboard | Infra, cert and tenant status at a glance (auto-refresh every 10 s) |
| Install | Day-1 stack provisioning wizard |
| Tenants | List, activate, or create tenants |
| Logs | Real-time compose log viewer (follow mode) |

### Mode badges

The status bar shows `READONLY` (`SRIYACTL_READONLY=1`) or `DRY-RUN` (`--dry-run`)
when those modes are active. Mutations are blocked or plan-only respectively.

### Keybindings per screen

| Screen | Keys |
|--------|------|
| Dashboard | `r` refresh, `esc` menu |
| Tenants (list) | `enter` / `u` activate, `c` create, `r` refresh, `esc` menu |
| Tenants (create) | `enter` next field, `esc` cancel |
| Install wizard | `tab` / arrows navigate, `enter` confirm, `ctrl+c` abort |
| Logs | `esc` / `q` stop and go back, `r` reconnect (after stream ends) |

## Commands (v1)

| Command | Description |
|---------|-------------|
| `sriyactl infra status`   | Aggregated stack state: compose ps + /health + image tag |
| `sriyactl infra logs [-f] [service]` | Stream compose logs (Ctrl-C to stop) |
| `sriyactl infra upgrade --to vX.Y.Z [--timeout 5m]` | Migration-aware: bump tag → pull → up → wait /health |
| `sriyactl infra backup`   | `pg_dump` via compose exec; reports path + size |
| `sriyactl infra restore <file>` | Restore a dump (destructive, requires `--yes`) |
| `sriyactl infra doctor`   | Preflight: docker, daemon, .env keys, ENCRYPTION_KEY length |
| `sriyactl tenant create --alias <a> --ruc <r> --razon-social <rs> --owner-name <o> --password <p> --cert <path>` | Atomic onboarding; auto-captures the apiKey to the keychain |
| `sriyactl tenant list`    | List tenants in the current context |
| `sriyactl tenant use <alias>` | Persist the active tenant |
| `sriyactl tenant current` | Show the active tenant |
| `sriyactl cert status [--tenant <alias>] [--warn-days N]` | Cert expiry watch (CI-signal) |

## Output & error model

Every command supports `--output json|yaml|table`. The default is auto-detected:
**TTY → table, pipe → json**. The output envelope is:

```json
{
  "schemaVersion": "1.0",
  "kind": "TenantList",
  "data": { /* typed payload */ }
}
```

Errors render as:

```json
{ "error": { "code": "tenant_duplicate", "message": "...", "hint": "...", "retryable": false } }
```

### Exit codes (stable, ai-contract)

| Code | Meaning |
|------|---------|
| 0 | success |
| 1 | generic error |
| 2 | usage / flag |
| 3 | auth (invalid or missing credentials) |
| 4 | not found |
| 5 | conflict (e.g. tenant already exists) |
| 6 | transient / network / expiring cert |
| 7 | mutating command blocked by read-only |

## Read-only & dry-run

Two AI-safety features are first-class:

- `SRIYACTL_READONLY=1` (or `--readonly`): all mutating commands fail fast with
  `code: readonly_blocked` (exit 7) **before** any effect. Read-only commands keep working.
- `--dry-run`: every mutating command prints a `Plan` object describing what it would
  do, without executing it. Use this in CI before destructive operations.

## Examples

```bash
# Onboard a tenant
sriyactl tenant create \
  --alias acme \
  --ruc 1790000000001 \
  --razon-social "ACME S.A." \
  --owner-name "Jane Doe" \
  --password "$BOOTSTRAP_PASSWORD" \
  --cert ./acme.p12

# Pipe tenant list to jq
sriyactl tenant list --output json | jq '.data.tenants[].alias'

# Watch cert expiry in CI (exits 6 when any cert expires within 30 days)
sriyactl cert status acme --warn-days 30

# Check the stack is healthy
sriyactl infra status

# Migration-aware upgrade with auto-rollback on health timeout
sriyactl infra upgrade --to v1.4.0 --timeout 10m

# Run a destructive command in CI
SRIYACTL_READONLY=1 sriyactl infra upgrade --to v1.4.0  # exits 7

# Print a restore plan without executing
sriyactl infra restore ./backup-20260605.sql --dry-run
```

## Related

- [SriYa billing backend](https://github.com/JJQuispillo/billing) — the .NET 9 service
  this CLI talks to.
- [AGENTS.md](./AGENTS.md) — AI-agent contract: structured output, exit codes,
  read-only mode.

## License

MIT — see [LICENSE](./LICENSE).
