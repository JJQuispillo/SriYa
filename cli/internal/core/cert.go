package core

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/api"
	"github.com/JJQuispillo/billing/cli/internal/config"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// CertDeps bundles the dependencies for cert handlers. Tenant resolution
// lives in the same struct: the handler picks the tenantID from the
// override (--tenant) or the active tenant in the config.
type CertDeps struct {
	API                 api.Client
	Store               config.TenantsStore
	Config              *config.Config
	ContextName         string
	TenantAliasOverride string // "" means use the active tenant
}

// CertStatusRequest is the input to CertStatusHandler.
type CertStatusRequest struct {
	TenantAlias string // empty → use active
	WarnDays    int
}

// CertStatusResult is the success payload. Expiring/Expired are surfaced
// as Errors in the envelope (not as Go errors) so the operator can see
// which cert tripped; the render layer projects the list and the CLI
// middleware then maps Expiring/Expired to a non-zero exit code via
// the errs code table.
type CertStatusResult struct {
	Tenant string            `json:"tenant"  yaml:"tenant"`
	Warn   int               `json:"warn"    yaml:"warn"`
	Certs  []CertStatusEntry `json:"certs"   yaml:"certs"`
}

// CertStatusEntry is a single certificate row.
//
// Fields map to the backend's CertificateResponse (CertificateDtos.cs:3-8):
//   - Subject carries NombrePropietario (the only descriptive field the
//     backend exposes; "subject" is the historical column name in the CLI
//     and is preserved here to keep the table-render contract stable).
//   - Issuer was removed in sriyactl-v1-fixes (design §#2): the backend
//     does not expose it; leaving an empty column would surface a dead
//     field. The column is dropped from the entry entirely.
//
// Status is DERIVED in this handler from FechaExpiracion + Activo +
// warn-days — the backend does NOT send a pre-computed status. Without
// this derivation the DTO mismatch produced a zero-time ExpiresAt that
// marked every cert as "expired" (finding #2 in the proposal).
type CertStatusEntry struct {
	ID        string    `json:"id"         yaml:"id"`
	Subject   string    `json:"subject"    yaml:"subject"`
	ExpiresAt time.Time `json:"expiresAt"  yaml:"expiresAt"`
	DaysLeft  int       `json:"daysLeft"   yaml:"daysLeft"`
	Status    string    `json:"status"     yaml:"status"` // valid | expiring | expired
}

// CertStatusHandler implements core.Handler. Resolves the alias to a
// uuid via the local config (no backend call), then calls
// api.CertStatus. The X-Tenant-Id header is set in the api client.
//
// On success, this returns a sentinel CLIError marked Renderable when
// any cert is expiring or expired, so the cli middleware prints the
// payload (cert list) to stdout and the error envelope to stderr before
// returning a non-zero exit code (design §#4 + ai-contract REQ-PAYLOAD).
func CertStatusHandler(d CertDeps) Handler[CertStatusRequest, CertStatusResult] {
	return func(ctx context.Context, in CertStatusRequest) (Output[CertStatusResult], error) {
		alias := in.TenantAlias
		if alias == "" {
			alias = d.TenantAliasOverride
		}
		if alias == "" {
			// Fall back to the active tenant.
			ref, err := d.Store.Active(d.ContextName)
			if err != nil {
				if errors.Is(err, config.ErrNoActiveTenant) {
					return Output[CertStatusResult]{}, errs.New(
						errs.CodeUsage,
						"no tenant specified and none active",
						"pass --tenant <alias> or run `sriyactl tenant use <alias>`",
					)
				}
				return Output[CertStatusResult]{}, err
			}
			alias = ref.Alias
		}
		ref, err := d.Store.Get(d.ContextName, alias)
		if err != nil {
			if errors.Is(err, config.ErrTenantNotFound) {
				return Output[CertStatusResult]{}, errs.New(
					errs.CodeTenantNotFound,
					fmt.Sprintf("alias %q not found in context %q", alias, d.ContextName),
					"run `sriyactl tenant list` to see registered aliases",
				)
			}
			return Output[CertStatusResult]{}, err
		}

		raw, err := d.API.CertStatus(ctx, ref.ID)
		if err != nil {
			return Output[CertStatusResult]{}, err
		}

		// Verified backend contract (CertificateEndpoints.cs:72-80):
		// a tenant without certificates returns 200 with an empty JSON
		// array, NOT 404. The empty-list case is a normal
		// cert_not_found (exit 4) — different from "expired" (exit 9).
		if len(raw) == 0 {
			return Output[CertStatusResult]{}, errs.New(
				errs.CodeCertNotFound,
				fmt.Sprintf("tenant %q has no certificate on file", alias),
				"upload one with `sriyactl cert upload <alias> --cert <path>` (v2)",
			)
		}

		warn := in.WarnDays
		if warn <= 0 {
			warn = 30
		}
		// Compare in UTC. Backend serializes DateTime UTC as ISO-8601
		// ("...Z"); Go's time.Time preserves the timezone. We force
		// UTC here so a server in a non-UTC zone does not skew the
		// "days left" math.
		now := time.Now().UTC()
		entries := make([]CertStatusEntry, 0, len(raw))
		anyExpiring := false
		anyExpired := false
		for _, c := range raw {
			// Normalize the cert expiry to UTC. encoding/json decodes
			// ISO-8601 with offset to time.Time with the offset; we
			// compare absolute times so the zone does not matter, but
			// the displayed ExpiresAt is kept as the server sent it.
			expiry := c.FechaExpiracion
			if expiry.Location() != time.UTC {
				expiry = expiry.UTC()
			}
			hours := expiry.Sub(now).Hours()
			days := int(hours / 24)
			st := "valid"
			switch {
			case !c.Activo:
				// A revoked/inactive cert is NOT usable regardless of
				// the expiry date. Mark expired (the closest signal)
				// so CI surfaces it.
				st = "expired"
				anyExpired = true
			case hours < 0:
				st = "expired"
				anyExpired = true
			case days <= warn:
				st = "expiring"
				anyExpiring = true
			}
			entries = append(entries, CertStatusEntry{
				ID:        c.ID,
				Subject:   c.NombrePropietario,
				ExpiresAt: expiry,
				DaysLeft:  days,
				Status:    st,
			})
		}

		// Map the result to an exit-code-bearing CLIError. We do NOT
		// fail the handler on expiring/expired — we return the data +
		// a renderable sentinel so the middleware prints both the
		// payload (stdout) and the error (stderr) before the non-zero
		// exit (design §#4).
		res := CertStatusResult{
			Tenant: alias,
			Warn:   warn,
			Certs:  entries,
		}
		if anyExpired {
			return NewOutput("CertStatus", res), errs.New(
				errs.CodeCertExpired,
				fmt.Sprintf("at least one certificate for %q is expired", alias),
				"rotate the cert (v2: `sriyactl cert upload`) or contact support",
			).MarkRenderable()
		}
		if anyExpiring {
			return NewOutput("CertStatus", res), errs.New(
				errs.CodeCertExpiring,
				fmt.Sprintf("at least one certificate for %q is expiring within %d days", alias, warn),
				"plan a rotation before the deadline",
			).MarkRenderable()
		}
		return NewOutput("CertStatus", res), nil
	}
}
