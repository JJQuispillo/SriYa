package cli

import (
	"fmt"

	"github.com/spf13/cobra"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// newTenantCmd wires the tenant subcommand.
func newTenantCmd(flags *SharedFlags) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "tenant",
		Short: "Manage tenants (onboarding, active selection, list)",
		Long:  "Subcommands for tenant lifecycle: create (atomic bootstrap), list, use, current. All require a current context with a service token.",
	}
	cmd.AddCommand(
		newTenantCreateCmd(flags),
		newTenantListCmd(flags),
		newTenantUseCmd(flags),
		newTenantCurrentCmd(flags),
	)
	return cmd
}

// newTenantCreateCmd implements `sriyactl tenant create`.
func newTenantCreateCmd(flags *SharedFlags) *cobra.Command {
	var (
		alias           string
		ruc             string
		razonSocial     string
		ownerName       string
		password        string
		certPath        string
		nombreComercial string
		correoContacto  string
		apiKeyName      string
		showAPIKey      bool
	)
	cmd := &cobra.Command{
		Use:   "create",
		Short: "Onboard a new tenant atomically (POST /api/v1/bootstrap)",
		Long:  "Performs atomic tenant onboarding via the backend bootstrap endpoint and auto-captures the one-time apiKey to the OS keychain. The apiKey is NOT printed unless --show is passed.",
		RunE: func(cmd *cobra.Command, _ []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			// Mark the invocation as a mutator so the read-only guard
			// in core.TenantCreateHandler fires when SRIYACTL_READONLY=1.
			mutFlags := *flags
			mutFlags.Mutating = true
			deps := core.TenantDeps{
				API:         cc.API,
				Store:       toCfgTenantsStore(cc.TenantStore),
				Secret:      cc.Secret,
				Config:      loadRawConfigFor(cc),
				ContextName: cc.ContextName,
			}
			req := core.TenantCreateRequest{
				Alias:           alias,
				RUC:             ruc,
				RazonSocial:     razonSocial,
				OwnerName:       ownerName,
				Password:        password,
				CertificatePath: certPath,
				NombreComercial: nombreComercial,
				CorreoContacto:  correoContacto,
				APIKeyName:      apiKeyName,
				ShowAPIKey:      showAPIKey,
			}
			handler := core.TenantCreateHandler(deps)
			exit := RunHandler(mutFlags, cc.Stdout, cc.Stderr, handler, req)
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
	f := cmd.Flags()
	f.StringVar(&alias, "alias", "", "tenant alias (required)")
	f.StringVar(&ruc, "ruc", "", "RUC (required)")
	f.StringVar(&razonSocial, "razon-social", "", "razón social (required)")
	f.StringVar(&ownerName, "owner-name", "", "owner full name (required)")
	f.StringVar(&password, "password", "", "owner password (required)")
	f.StringVar(&certPath, "cert", "", "path to .p12/.pfx certificate (required)")
	f.StringVar(&nombreComercial, "nombre-comercial", "", "commercial name (optional)")
	f.StringVar(&correoContacto, "correo-contacto", "", "contact email (optional)")
	f.StringVar(&apiKeyName, "api-key-name", "bootstrap", "api key label (optional, default 'bootstrap')")
	f.BoolVar(&showAPIKey, "show", false, "print the apiKey in the output (default: hide)")
	_ = cmd.MarkFlagRequired("alias")
	_ = cmd.MarkFlagRequired("ruc")
	_ = cmd.MarkFlagRequired("razon-social")
	_ = cmd.MarkFlagRequired("owner-name")
	_ = cmd.MarkFlagRequired("password")
	_ = cmd.MarkFlagRequired("cert")
	return cmd
}

// newTenantListCmd implements `sriyactl tenant list`.
func newTenantListCmd(flags *SharedFlags) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "list",
		Short: "List tenants known to the current context",
		RunE: func(cmd *cobra.Command, _ []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			deps := core.TenantDeps{
				API:         cc.API,
				Store:       toCfgTenantsStore(cc.TenantStore),
				Secret:      cc.Secret,
				Config:      loadRawConfigFor(cc),
				ContextName: cc.ContextName,
			}
			handler := core.TenantListHandler(deps)
			exit := RunHandler(*flags, cc.Stdout, cc.Stderr, handler, struct{}{})
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
	return cmd
}

// newTenantUseCmd implements `sriyactl tenant use <alias>`.
func newTenantUseCmd(flags *SharedFlags) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "use <alias>",
		Short: "Persist the active tenant alias in the current context",
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
			deps := core.TenantDeps{
				API:         cc.API,
				Store:       toCfgTenantsStore(cc.TenantStore),
				Secret:      cc.Secret,
				Config:      loadRawConfigFor(cc),
				ContextName: cc.ContextName,
			}
			handler := core.TenantUseHandler(deps)
			exit := RunHandler(mutFlags, cc.Stdout, cc.Stderr, handler, core.TenantUseRequest{Alias: args[0]})
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
	return cmd
}

// newTenantCurrentCmd implements `sriyactl tenant current`.
func newTenantCurrentCmd(flags *SharedFlags) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "current",
		Short: "Show the active tenant alias in the current context",
		RunE: func(cmd *cobra.Command, _ []string) error {
			cc, err := buildCmdContext(flags)
			if err != nil {
				return err
			}
			cc.Stdout = cmd.OutOrStdout()
			cc.Stderr = cmd.ErrOrStderr()
			deps := core.TenantDeps{
				API:         cc.API,
				Store:       toCfgTenantsStore(cc.TenantStore),
				Secret:      cc.Secret,
				Config:      loadRawConfigFor(cc),
				ContextName: cc.ContextName,
			}
			handler := core.TenantCurrentHandler(deps)
			exit := RunHandler(*flags, cc.Stdout, cc.Stderr, handler, struct{}{})
			if exit != 0 {
				return cliErrorFromExit(exit)
			}
			return nil
		},
	}
	return cmd
}

// cliErrorFromExit is a small helper to map a non-zero exit code back to
// an error. Currently used to keep cobra's RunE signature; v2 can simplify.
func cliErrorFromExit(exit int) error {
	code := errs.CodeGeneric
	switch exit {
	case 1:
		code = errs.CodeGeneric
	case 2:
		code = errs.CodeUsage
	case 3:
		code = errs.CodeAuth
	case 4:
		code = errs.CodeNotFound
	case 5:
		code = errs.CodeConflict
	case 6:
		code = errs.CodeNetwork
	case 7:
		code = errs.CodeReadOnlyBlocked
	}
	return errs.New(
		code,
		fmt.Sprintf("command exited with code %d", exit),
		"check the rendered output above",
	)
}
