# sriyactl

CLI de operaciones day-2 para el stack auto-gestionado [SriYa/Qora](https://github.com/JJQuispillo/billing).
Envuelve `docker compose` y la API HTTP de SriYa en una interfaz única, auditable y amigable para IA.

> Estado: **v1** — cubre `infra`, `tenant` y `cert status`. v2 agregará `sriyactl mcp`,
> documentación de comandos y gestión de apikeys.

## Instalación

### Homebrew (macOS / Linux)

```bash
brew install JJQuispillo/tap/sriyactl
```

### Binario pre-compilado

Descarga el tarball más reciente desde la página de [Releases](https://github.com/JJQuispillo/sriyactl/releases).
macOS / Linux:

```bash
curl -L https://github.com/JJQuispillo/sriyactl/releases/latest/download/sriyactl_<version>_<os>_<arch>.tar.gz \
  | tar -xz -C /usr/local/bin sriyactl
chmod +x /usr/local/bin/sriyactl
```

### Desde fuente

```bash
go install github.com/JJQuispillo/sriyactl/cmd/sriyactl@latest
```

## Configuración

`sriyactl` lee su configuración de `~/.config/sriyactl/config.toml`. En una instalación
nueva el archivo no existe; el CLI lo crea automáticamente en la primera mutación.

Configuración mínima:

```toml
current_context = "prod"
current_tenant  = "acme"

[contexts.prod]
url = "https://sri.example.com"
service_token_ref = "keychain"   # siempre "keychain" en v1

[tenants.prod.acme]
id  = "00000000-0000-0000-0000-000000000001"
ruc = "1790000000001"
env = "prod"
```

**Los secretos nunca viven en este archivo.** El token de servicio se almacena en el
keychain del SO bajo `sriyactl/<context>`; las API keys por tenant bajo
`sriyactl/<context>/<tenant-alias>`.

Para entornos CI / headless, usa las variables de entorno:

```bash
export SRIYACTL_SERVICE_TOKEN="..."
export SRIYACTL_API_KEY="..."        # por tenant, opcional
```

## TUI interactiva

`sriyactl` sin argumentos lanza una terminal interactiva cuando stdout es una TTY.
Usa `sriyactl ui` para forzar la TUI (incluso en pipes) o `SRIYACTL_NO_TUI=1` para
deshabilitarla en entornos de scripting.

### Navegación del menú

| Tecla  | Acción                |
|--------|-----------------------|
| ↑ / k  | Mover cursor arriba   |
| ↓ / j  | Mover cursor abajo    |
| Enter  | Abrir pantalla        |
| Esc / q| Volver / salir        |
| r      | Refrescar pantalla    |

### Pantallas

| Pantalla  | Descripción                                                   |
|-----------|---------------------------------------------------------------|
| Dashboard | Estado de infra, cert y tenant de un vistazo (auto-refresh 10 s) |
| Install   | Wizard de aprovisionamiento day-1                             |
| Tenants   | Listar, activar o crear tenants                               |
| Logs      | Visor de logs en tiempo real (modo follow)                    |

### Insignias de modo

La barra de estado muestra `READONLY` (`SRIYACTL_READONLY=1`) o `DRY-RUN` (`--dry-run`)
cuando esos modos están activos. Las mutaciones se bloquean o son plan-only respectivamente.

### Atajos por pantalla

| Pantalla              | Teclas                                          |
|-----------------------|-------------------------------------------------|
| Dashboard             | `r` refrescar, `esc` menú                       |
| Tenants (lista)       | `enter` / `u` activar, `c` crear, `r` refrescar, `esc` menú |
| Tenants (crear)       | `enter` siguiente campo, `esc` cancelar         |
| Install wizard        | `tab` / flechas navegar, `enter` confirmar, `ctrl+c` abortar |
| Logs                  | `esc` / `q` detener y volver, `r` reconectar (tras fin del stream) |

## Comandos (v1)

| Comando | Descripción |
|---------|-------------|
| `sriyactl infra status`   | Estado agregado del stack: compose ps + /health + tag de imagen |
| `sriyactl infra logs [-f] [service]` | Stream de logs de compose (Ctrl-C para detener) |
| `sriyactl infra upgrade --to vX.Y.Z [--timeout 5m]` | Actualización con detección de migraciones: bump tag → pull → up → wait /health |
| `sriyactl infra backup`   | `pg_dump` vía compose exec; reporta ruta + tamaño |
| `sriyactl infra restore <file>` | Restaurar un dump (destructivo, requiere `--yes`) |
| `sriyactl infra doctor`   | Preflight: docker, daemon, claves .env, longitud de ENCRYPTION_KEY |
| `sriyactl tenant create --alias <a> --ruc <r> --razon-social <rs> --owner-name <o> --password <p> --cert <ruta>` | Onboarding atómico; captura automáticamente la apiKey en el keychain |
| `sriyactl tenant list`    | Listar tenants en el contexto actual |
| `sriyactl tenant use <alias>` | Persistir el tenant activo |
| `sriyactl tenant current` | Mostrar el tenant activo |
| `sriyactl cert status [--tenant <alias>] [--warn-days N]` | Vigilancia de expiración de certificados (señal para CI) |

## Modelo de salida y errores

Todo comando soporta `--output json|yaml|table`. El valor por defecto se auto-detecta:
**TTY → table, pipe → json**. El envelope de salida es:

```json
{
  "schemaVersion": "1.0",
  "kind": "TenantList",
  "data": { /* payload tipado */ }
}
```

Los errores se renderizan como:

```json
{ "error": { "code": "tenant_duplicate", "message": "...", "hint": "...", "retryable": false } }
```

### Códigos de salida (estables, ai-contract)

| Código | Significado                                    |
|--------|-----------------------------------------------|
| 0      | éxito                                         |
| 1      | error genérico                                |
| 2      | error de uso / flags                          |
| 3      | auth (credenciales inválidas o ausentes)      |
| 4      | no encontrado                                 |
| 5      | conflicto (ej. tenant ya existe)              |
| 6      | transitorio / red / certificado por expirar   |
| 7      | comando mutante bloqueado por solo-lectura    |

## Solo-lectura y dry-run

Dos funcionalidades de seguridad para IA son ciudadanos de primera clase:

- `SRIYACTL_READONLY=1` (o `--readonly`): todo comando mutante falla rápido con
  `code: readonly_blocked` (exit 7) **antes** de cualquier efecto. Los comandos de
  solo lectura siguen funcionando.
- `--dry-run`: todo comando mutante imprime un objeto `Plan` describiendo lo que haría,
  sin ejecutarlo. Úsalo en CI antes de operaciones destructivas.

## Ejemplos

```bash
# Dar de alta un tenant
sriyactl tenant create \
  --alias acme \
  --ruc 1790000000001 \
  --razon-social "ACME S.A." \
  --owner-name "Jane Doe" \
  --password "$BOOTSTRAP_PASSWORD" \
  --cert ./acme.p12

# Pasar lista de tenants a jq
sriyactl tenant list --output json | jq '.data.tenants[].alias'

# Vigilar expiración de certificados en CI (exit 6 si alguno expira en 30 días)
sriyactl cert status acme --warn-days 30

# Verificar que el stack está saludable
sriyactl infra status

# Actualización con detección de migraciones y rollback automático en timeout
sriyactl infra upgrade --to v1.4.0 --timeout 10m

# Ejecutar un comando destructivo en CI
SRIYACTL_READONLY=1 sriyactl infra upgrade --to v1.4.0  # exit 7

# Ver el plan de restauración sin ejecutar
sriyactl infra restore ./backup-20260605.sql --dry-run
```

## Relacionados

- [SriYa billing backend](https://github.com/JJQuispillo/billing) — el servicio .NET 9
  con el que este CLI se comunica.
- [AGENTS.md](./AGENTS.md) — contrato para agentes de IA: salida estructurada, códigos
  de salida, modo solo-lectura.

## Licencia

MIT — ver [LICENSE](./LICENSE).
