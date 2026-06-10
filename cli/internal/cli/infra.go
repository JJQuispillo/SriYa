package cli

import (
	"fmt"
	"time"

	"github.com/spf13/cobra"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/installer"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// newInfraCmd wires the infra subcommand.
func newInfraCmd(flags *SharedFlags) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "infra",
		Short: "Operate the SriYa stack (status, logs, upgrade, backup, restore, doctor)",
		Long:  "Wrapper around docker compose + /health endpoints. All commands operate from the install dir (--dir or SRIYACTL_HOME).",
	}
	cmd.AddCommand(
		newInfraInstallCmd(flags),
		newInfraStatusCmd(flags),
		newInfraLogsCmd(flags),
		newInfraUpgradeCmd(flags),
		newInfraBackupCmd(flags),
		newInfraRestoreCmd(flags),
		newInfraDoctorCmd(flags),
	)
	return cmd
}

// resolveInstallTargetDir resolves where `infra install` provisions the
// stack. The logic lives in ops.ResolveInstallDir (shared with the TUI
// wizard); this thin wrapper keeps the historical name in this package.
func resolveInstallTargetDir(flags *SharedFlags) (string, error) {
	return ops.ResolveInstallDir(flags)
}

func newInfraInstallCmd(flags *SharedFlags) *cobra.Command {
	var (
		version     string
		port        string
		corsOrigin  string
		dbUser      string
		autoInstall bool
		noBootstrap bool
		timeout     time.Duration
		// Bootstrap (Fase 5) flags. On a TTY the missing ones are prompted;
		// headless they must all be supplied (else bootstrap_input_required).
		contextName     string
		ruc             string
		razonSocial     string
		ownerName       string
		password        string
		certPath        string
		nombreComercial string
		correoContacto  string
		apiKeyName      string
	)
	cmd := &cobra.Command{
		Use:   "install",
		Short: "Provision the SriYa stack: preflight → .env → compose → up -d → wait /health/ready",
		Long: "Day-1 all-in-one installer. Runs a pre-install docker check, renders a .env with " +
			"charset-safe secrets (no-clobber), downloads the pinned compose, brings the stack up, " +
			"and waits for readiness. Idempotent: re-running never rotates secrets or clobbers an edited compose.",
		RunE: func(cmd *cobra.Command, _ []string) error {
			stdout := cmd.OutOrStdout()
			stderr := cmd.ErrOrStderr()

			dir, err := resolveInstallTargetDir(flags)
			if err != nil {
				return err
			}

			// Shared install wiring (ops.BuildInstallDeps): the same
			// builder serves the TUI wizard. Auto-install step lines go
			// to stderr (keeps stdout structured); missing bootstrap
			// fields are prompted on a TTY.
			localURL := ops.LocalAPIURL(port)
			deps := ops.BuildInstallDeps(flags, dir, port, stderr, installer.IsInteractive, ops.NewBootstrapPrompter(stderr))
			deps.HealthTimeout = timeout
			handler := core.InfraInstallHandler(deps)

			// `infra install` provisions infra AND (by default) creates the
			// first tenant, so it is a mutating command — honor
			// SRIYACTL_READONLY via the standard guard.
			mutFlags := *flags
			mutFlags.Mutating = true

			req := core.InfraInstallRequest{
				Version:     version,
				Port:        port,
				CorsOrigin:  corsOrigin,
				DBUser:      dbUser,
				Dir:         dir,
				AutoInstall: autoInstall,
				NoBootstrap: noBootstrap,
				ContextName: contextName,
				LocalURL:    localURL,
				Boot: api.BootstrapRequest{
					RUC:             ruc,
					RazonSocial:     razonSocial,
					OwnerName:       ownerName,
					Password:        password,
					CertificatePath: certPath,
					NombreComercial: nombreComercial,
					CorreoContacto:  correoContacto,
					APIKeyName:      apiKeyName,
				},
			}
			exit := runInstallHandler(mutFlags, stdout, stderr, handler, req)
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
	f := cmd.Flags()
	f.StringVar(&version, "version", "", "pinned image tag / compose ref (default: 1.0.0; \"latest\" tracks main)")
	f.StringVar(&port, "port", "", "host API port (default 8080)")
	f.StringVar(&corsOrigin, "cors-origin", "", "allowed frontend origin (default http://localhost:3000)")
	f.StringVar(&dbUser, "db-user", "", "owner/bootstrap DB role (default billing_user)")
	f.BoolVar(&autoInstall, "auto-install", false, "attempt to install docker automatically (macOS only, requires Homebrew: `brew install colima docker docker-compose` + `colima start`; Linux is guide-only)")
	f.BoolVar(&noBootstrap, "no-bootstrap", false, "skip first-tenant bootstrap after the stack is healthy")
	f.DurationVar(&timeout, "timeout", 90*time.Second, "max wait for /health/ready")
	// Bootstrap (Fase 5) flags. Prompted on a TTY; required headless.
	f.StringVar(&contextName, "context", "local", "local context alias to seed in config.toml")
	f.StringVar(&ruc, "ruc", "", "first-tenant RUC (prompted on TTY)")
	f.StringVar(&razonSocial, "razon-social", "", "first-tenant razón social (prompted on TTY)")
	f.StringVar(&ownerName, "owner-name", "", "first-tenant owner full name (prompted on TTY)")
	f.StringVar(&password, "password", "", "first-tenant owner password (prompted on TTY)")
	f.StringVar(&certPath, "cert", "", "path to the first-tenant .p12/.pfx certificate (prompted on TTY)")
	f.StringVar(&nombreComercial, "nombre-comercial", "", "first-tenant commercial name (optional)")
	f.StringVar(&correoContacto, "correo-contacto", "", "first-tenant contact email (optional)")
	f.StringVar(&apiKeyName, "api-key-name", "bootstrap", "first-tenant api key label (optional)")
	return cmd
}

func newInfraStatusCmd(flags *SharedFlags) *cobra.Command {
	return &cobra.Command{
		Use:   "status",
		Short: "Aggregate stack state: compose ps + /health + /health/ready + image tag",
		RunE: func(cmd *cobra.Command, _ []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			deps := core.InfraDeps{API: cc.API, Compose: cc.Compose}
			handler := core.InfraStatusHandler(deps)
			exit := RunHandler(*flags, cc.Stdout, cc.Stderr, handler, struct{}{})
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
}

func newInfraLogsCmd(flags *SharedFlags) *cobra.Command {
	var (
		follow  bool
		service string
	)
	cmd := &cobra.Command{
		Use:   "logs",
		Short: "Stream compose logs (use -f to follow)",
		RunE: func(cmd *cobra.Command, _ []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			deps := core.InfraDeps{API: cc.API, Compose: cc.Compose}
			handler := core.InfraLogsHandler(deps)
			ctx := flags.BuildContext()
			return handler(ctx, core.InfraLogsRequest{Follow: follow, Service: service}, cc.Stdout)
		},
	}
	f := cmd.Flags()
	f.BoolVarP(&follow, "follow", "f", false, "follow log output")
	f.StringVar(&service, "service", "", "limit to a single service (default: all)")
	return cmd
}

func newInfraUpgradeCmd(flags *SharedFlags) *cobra.Command {
	var (
		target  string
		timeout time.Duration
	)
	cmd := &cobra.Command{
		Use:   "upgrade",
		Short: "Migration-aware upgrade: backup → bump tag → pull → up -d → wait /health/ready",
		RunE: func(cmd *cobra.Command, _ []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			mutFlags := *flags
			mutFlags.Mutating = true
			// `infra upgrade` is destructive (it mutates .env, pulls a
			// new image, restarts the stack). Marking RequiresConfirm
			// is what activates the gate in RunHandler; the description
			// is interpolated with the target tag for the operator.
			mutFlags.RequiresConfirm = true
			mutFlags.ConfirmDescription = fmt.Sprintf("upgrade the SriYa stack to image tag %q (will backup, bump BILLING_IMAGE_TAG, pull, and recreate containers)", target)
			deps := core.InfraDeps{API: cc.API, Compose: cc.Compose}
			handler := core.InfraUpgradeHandler(deps)
			exit := RunHandler(mutFlags, cc.Stdout, cc.Stderr, handler, core.InfraUpgradeRequest{
				TargetTag: target,
				Timeout:   timeout,
			})
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
	f := cmd.Flags()
	f.StringVar(&target, "to", "", "target image tag (e.g. v1.4.0)")
	f.DurationVar(&timeout, "timeout", 5*time.Minute, "max wait for /health/ready before rolling back")
	_ = cmd.MarkFlagRequired("to")
	return cmd
}

func newInfraBackupCmd(flags *SharedFlags) *cobra.Command {
	return &cobra.Command{
		Use:   "backup",
		Short: "Dump the Postgres database via `pg_dump` over compose exec",
		RunE: func(cmd *cobra.Command, _ []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			deps := core.InfraDeps{API: cc.API, Compose: cc.Compose}
			handler := core.InfraBackupHandler(deps)
			exit := RunHandler(*flags, cc.Stdout, cc.Stderr, handler, struct{}{})
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
}

func newInfraRestoreCmd(flags *SharedFlags) *cobra.Command {
	return &cobra.Command{
		Use:   "restore <file>",
		Short: "Restore a Postgres dump (DESTRUCTIVE: requires --yes unless non-TTY)",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			mutFlags := *flags
			mutFlags.Mutating = true
			// `infra restore` is destructive: it overwrites the
			// current postgres database. The Confirm gate is wired
			// here so every caller of this command gets the same
			// table-of-decision behavior (TTY × bypass).
			mutFlags.RequiresConfirm = true
			mutFlags.ConfirmDescription = fmt.Sprintf("restore dump %q into the postgres container (overwrites the current database)", args[0])
			deps := core.InfraDeps{API: cc.API, Compose: cc.Compose}
			handler := core.InfraRestoreHandler(deps)
			exit := RunHandler(mutFlags, cc.Stdout, cc.Stderr, handler, core.InfraRestoreRequest{
				Path: args[0],
			})
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
}

func newInfraDoctorCmd(flags *SharedFlags) *cobra.Command {
	return &cobra.Command{
		Use:   "doctor",
		Short: "Preflight checks (docker, daemon, .env keys, image, ENCRYPTION_KEY length)",
		RunE: func(cmd *cobra.Command, _ []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			deps := core.InfraDeps{API: cc.API, Compose: cc.Compose}
			handler := core.InfraDoctorHandler(deps)
			exit := RunHandler(*flags, cc.Stdout, cc.Stderr, handler, core.InfraDoctorRequest{})
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
}
