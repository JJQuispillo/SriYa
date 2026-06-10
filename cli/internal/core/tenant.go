package core

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/config"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/secret"
)

// TenantDeps is the bundle of dependencies tenant handlers need. We
// inject this rather than reaching into package-level state so tests can
// pass fakes.
type TenantDeps struct {
	API         api.Client
	Store       config.TenantsStore
	Secret      secret.Store
	Config      *config.Config
	ContextName string
}

// TenantCreateRequest is the input to TenantCreateHandler.
type TenantCreateRequest struct {
	Alias           string
	RUC             string
	RazonSocial     string
	OwnerName       string
	Password        string
	CertificatePath string
	NombreComercial string
	CorreoContacto  string
	APIKeyName      string
	ShowAPIKey      bool // print apiKey in output (default: hide)
}

// TenantCreateResult is the success payload. APIKey is omitted unless
// ShowAPIKey was set; the CLI render layer respects that.
type TenantCreateResult struct {
	TenantID            string    `json:"tenantId"             yaml:"tenantId"`
	Alias               string    `json:"alias"                yaml:"alias"`
	RUC                 string    `json:"ruc"                  yaml:"ruc"`
	RazonSocial         string    `json:"razonSocial"          yaml:"razonSocial"`
	CertificadoID       string    `json:"certificadoId"        yaml:"certificadoId"`
	CertificadoExpiraEn time.Time `json:"certificadoExpiraEn"  yaml:"certificadoExpiraEn"`
	APIKey              string    `json:"apiKey,omitempty"     yaml:"apiKey,omitempty"`
	APIKeyStored        bool      `json:"apiKeyStored"         yaml:"apiKeyStored"`
}

// TenantCreateHandler implements core.Handler. It calls BootstrapTenant,
// auto-captures the apiKey in the keychain, registers the tenant alias
// in config, and returns the typed result. The render layer (not this
// function) decides whether to print the apiKey based on --show.
func TenantCreateHandler(d TenantDeps) Handler[TenantCreateRequest, TenantCreateResult] {
	return func(ctx context.Context, in TenantCreateRequest) (Output[TenantCreateResult], error) {
		if err := GuardMutation(ctx); err != nil {
			return Output[TenantCreateResult]{}, err
		}
		if in.Alias == "" {
			return Output[TenantCreateResult]{}, errs.New(errs.CodeUsage, "tenant alias is required", "pass --alias <name>")
		}

		// Idempotency: if the alias already exists in this context, fail
		// fast with tenant_duplicate so we don't burn a backend bootstrap
		// for a known-collision case.
		if _, err := d.Store.Get(d.ContextName, in.Alias); err == nil {
			return Output[TenantCreateResult]{}, errs.New(
				errs.CodeTenantDuplicate,
				fmt.Sprintf("alias %q already exists in context %q", in.Alias, d.ContextName),
				"pick a different --alias or drop the existing one",
			)
		} else if !errors.Is(err, config.ErrTenantNotFound) {
			return Output[TenantCreateResult]{}, err
		}

		// Translate the request into the api layer's struct.
		apiReq := api.BootstrapRequest{
			RUC:             in.RUC,
			RazonSocial:     in.RazonSocial,
			OwnerName:       in.OwnerName,
			Password:        in.Password,
			CertificatePath: in.CertificatePath,
			NombreComercial: in.NombreComercial,
			CorreoContacto:  in.CorreoContacto,
			APIKeyName:      in.APIKeyName,
		}
		// If --dry-run, short-circuit with a Plan.
		if IsDryRun(ctx) {
			_ = Plan{
				Action: "tenant.bootstrap",
				Target: in.Alias,
				Details: map[string]any{
					"ruc":           in.RUC,
					"razonSocial":   in.RazonSocial,
					"ownerName":     in.OwnerName,
					"certificate":   in.CertificatePath,
					"captureApiKey": true,
				},
			}
			return NewOutput("TenantCreatePlan", TenantCreateResult{
				Alias:        in.Alias,
				RUC:          in.RUC,
				RazonSocial:  in.RazonSocial,
				APIKeyStored: false,
			}), nil
		}

		resp, err := d.API.BootstrapTenant(ctx, apiReq)
		if err != nil {
			return Output[TenantCreateResult]{}, err
		}

		// Auto-capture the apiKey to the keychain. The handler NEVER
		// returns the apiKey in the result unless ShowAPIKey is set;
		// the secret is captured in the keychain either way.
		stored := false
		if resp.APIKey != "" {
			k := secret.TenantAPIKey(d.ContextName, in.Alias)
			if err := d.Secret.Set(k, resp.APIKey); err != nil {
				// Surface the error but DO NOT roll back the tenant
				// (the tenant was created on the backend). The operator
				// can re-store manually. Tenant alias IS persisted.
				return Output[TenantCreateResult]{}, errs.Wrap(
					errs.CodeGeneric, err,
					"tenant created but apiKey could not be stored in keychain",
					"re-run `sriyactl tenant create` with the same alias to retry keychain storage, or rotate the key via the backend",
				)
			}
			stored = true
		}

		// Persist the tenant alias → id mapping.
		if err := d.Store.Upsert(d.ContextName, config.TenantRef{
			Alias: in.Alias,
			ID:    resp.TenantID,
			RUC:   resp.RUC,
		}); err != nil {
			return Output[TenantCreateResult]{}, errs.Wrap(
				errs.CodeGeneric, err,
				"tenant created but alias could not be saved to config",
				"the keychain entry exists; the alias will need to be re-added manually",
			)
		}

		res := TenantCreateResult{
			TenantID:            resp.TenantID,
			Alias:               in.Alias,
			RUC:                 resp.RUC,
			RazonSocial:         resp.RazonSocial,
			CertificadoID:       resp.CertificadoID,
			CertificadoExpiraEn: resp.CertificadoExpiraEn,
			APIKeyStored:        stored,
		}
		if in.ShowAPIKey {
			res.APIKey = resp.APIKey
		}
		_ = planFormat("tenant create completed", in.Alias)
		return NewOutput("TenantCreateResult", res), nil
	}
}

// TenantListResult is the shape returned by TenantListHandler.
type TenantListResult struct {
	Context string            `json:"context" yaml:"context"`
	Active  string            `json:"active"  yaml:"active"`
	Tenants []TenantListEntry `json:"tenants" yaml:"tenants"`
}

// TenantListEntry is one row of `tenant list`.
type TenantListEntry struct {
	Alias    string `json:"alias"   yaml:"alias"`
	ID       string `json:"id"      yaml:"id"`
	RUC      string `json:"ruc"     yaml:"ruc"`
	Env      string `json:"env"     yaml:"env"`
	IsActive bool   `json:"active"  yaml:"active"`
}

// TenantListHandler implements core.Handler.
func TenantListHandler(d TenantDeps) Handler[struct{}, TenantListResult] {
	return func(ctx context.Context, _ struct{}) (Output[TenantListResult], error) {
		tenants, err := d.Store.ListKnown(d.ContextName)
		if err != nil {
			return Output[TenantListResult]{}, err
		}
		active := d.Config.CurrentTenant
		entries := make([]TenantListEntry, 0, len(tenants))
		for _, t := range tenants {
			entries = append(entries, TenantListEntry{
				Alias:    t.Alias,
				ID:       t.ID,
				RUC:      t.RUC,
				Env:      t.Env,
				IsActive: t.Alias == active && d.Config.CurrentContext == d.ContextName,
			})
		}
		return NewOutput("TenantList", TenantListResult{
			Context: d.ContextName,
			Active:  active,
			Tenants: entries,
		}), nil
	}
}

// TenantUseRequest is the input to TenantUseHandler.
type TenantUseRequest struct {
	Alias string
}

// TenantUseResult is the success payload.
type TenantUseResult struct {
	Context string `json:"context" yaml:"context"`
	Alias   string `json:"alias"   yaml:"alias"`
	ID      string `json:"id"      yaml:"id"`
}

// TenantUseHandler implements core.Handler. Persists the active tenant
// in config; idempotent.
func TenantUseHandler(d TenantDeps) Handler[TenantUseRequest, TenantUseResult] {
	return func(ctx context.Context, in TenantUseRequest) (Output[TenantUseResult], error) {
		ref, err := d.Store.Get(d.ContextName, in.Alias)
		if err != nil {
			if errors.Is(err, config.ErrTenantNotFound) {
				return Output[TenantUseResult]{}, errs.New(
					errs.CodeTenantNotFound,
					fmt.Sprintf("alias %q not found in context %q", in.Alias, d.ContextName),
					"run `sriyactl tenant list` to see registered aliases",
				)
			}
			return Output[TenantUseResult]{}, err
		}
		if err := d.Store.SetCurrent(d.ContextName, in.Alias); err != nil {
			return Output[TenantUseResult]{}, err
		}
		return NewOutput("TenantUse", TenantUseResult{
			Context: d.ContextName,
			Alias:   ref.Alias,
			ID:      ref.ID,
		}), nil
	}
}

// TenantCurrentResult is the shape returned by TenantCurrentHandler.
type TenantCurrentResult struct {
	Context string `json:"context" yaml:"context"`
	Alias   string `json:"alias"   yaml:"alias"`
	ID      string `json:"id"      yaml:"id"`
}

// TenantCurrentHandler implements core.Handler. Returns a hint-style
// error when no tenant is active.
func TenantCurrentHandler(d TenantDeps) Handler[struct{}, TenantCurrentResult] {
	return func(ctx context.Context, _ struct{}) (Output[TenantCurrentResult], error) {
		ref, err := d.Store.Active(d.ContextName)
		if err != nil {
			if errors.Is(err, config.ErrNoActiveTenant) {
				return Output[TenantCurrentResult]{}, errs.New(
					errs.CodeTenantNotFound,
					"no active tenant in the current context",
					"run `sriyactl tenant use <alias>` to set one",
				)
			}
			return Output[TenantCurrentResult]{}, err
		}
		return NewOutput("TenantCurrent", TenantCurrentResult{
			Context: d.ContextName,
			Alias:   ref.Alias,
			ID:      ref.ID,
		}), nil
	}
}

// planFormat is a tiny helper used by --dry-run paths to render a
// uniform one-liner. Kept here so it lives near the handlers that use it.
func planFormat(action, target string) string { return action + ": " + target }
