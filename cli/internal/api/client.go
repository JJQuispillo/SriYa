// Package api is the HTTP client for the SriYa/Qora billing backend. The
// Client interface is the boundary handlers depend on; tests substitute
// fakes (httptest-based) or hand-rolled stubs.
package api

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"mime/multipart"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// Client is the interface handlers depend on. It is HTTP-only — the
// tenant list comes from internal/config's tenants.Store (see design.md
// correction A, OQ 3.3).
type Client interface {
	Health(ctx context.Context) (Health, error)
	// Ready probes /health/ready (DB readiness). The backend returns
	// 200 {status:"Ready"} on success and **503** (no useful body) when
	// the DB is not reachable. The 503 path is translated into a
	// CLIError(CodeDBUnavailable) at the api layer; handlers can treat
	// the absence of a Ready result as "degraded".
	Ready(ctx context.Context) (Health, error)
	BootstrapTenant(ctx context.Context, req BootstrapRequest) (BootstrapResponse, error)
	CertStatus(ctx context.Context, tenantID string) ([]Certificate, error)
}

// Health is the response from GET /health (liveness) and GET /health/ready
// (readiness). The backend serializes the status as PascalCase strings
// ("Healthy" / "Ready"), NOT lowercase ("ok"). ServiceTag does NOT exist
// in the backend payload and is only kept as a deprecated optional field
// for backward compatibility.
type Health struct {
	Status     string `json:"status"     yaml:"status"`
	ServiceTag string `json:"serviceTag,omitempty" yaml:"serviceTag,omitempty"`
}

// BootstrapRequest is the input to POST /api/v1/bootstrap. The Certificate
// is a multipart file part; all other fields are text parts.
type BootstrapRequest struct {
	RUC             string
	RazonSocial     string
	OwnerName       string
	Password        string
	CertificatePath string
	NombreComercial string
	CorreoContacto  string
	APIKeyName      string
	TenantAlias     string
}

// Validate performs cheap client-side checks before the round trip. Backend
// validation is authoritative.
func (b BootstrapRequest) Validate() error {
	if b.RUC == "" || b.RazonSocial == "" || b.OwnerName == "" || b.Password == "" {
		return errs.New(errs.CodeUsage, "ruc, razonSocial, ownerName and password are required", "use --ruc, --razon-social, --owner-name, --password")
	}
	if b.CertificatePath == "" {
		return errs.New(errs.CodeUsage, "certificate is required", "pass --cert <path-to-.p12-or-.pfx>")
	}
	return nil
}

// BootstrapResponse is the success payload. APIKey is plaintext exactly
// once — the CLI must auto-capture it to the keychain.
type BootstrapResponse struct {
	TenantID            string    `json:"tenantId"`
	RUC                 string    `json:"ruc"`
	RazonSocial         string    `json:"razonSocial"`
	CertificadoID       string    `json:"certificadoId"`
	CertificadoExpiraEn time.Time `json:"certificadoExpiraEn"`
	APIKeyID            string    `json:"apiKeyId"`
	APIKey              string    `json:"apiKey"`
	FechaCreacion       time.Time `json:"fechaCreacion"`
}

// Certificate mirrors the backend's CertificateResponse (CertificateDtos.cs).
// The backend serializes via System.Text.Json with camelCase defaults.
// The previously assumed `subject/issuer/expiresAt/estado` fields do NOT
// exist; decoding against the wrong shape produces a zero-time
// `FechaExpiracion`, which would mark every cert as expired.
type Certificate struct {
	ID                string    `json:"id"`
	NombrePropietario string    `json:"nombrePropietario"`
	FechaExpiracion   time.Time `json:"fechaExpiracion"`
	Activo            bool      `json:"activo"`
	FechaCreacion     time.Time `json:"fechaCreacion"`
}

// problemDetails is the shape RFC 7807 ProblemDetails documents use. The
// .NET backend's GlobalExceptionHandler emits a subset of these fields
// in camelCase. We only need a few keys for the duplicate heuristic.
type problemDetails struct {
	Type   string `json:"type"`
	Title  string `json:"title"`
	Status int    `json:"status"`
	Detail string `json:"detail"`
	// Instance is set by the backend but unused by the duplicate
	// heuristic; kept for forward-compat.
	Instance string `json:"instance"`
}

// HTTPClient is the net/http-based implementation of Client.
type HTTPClient struct {
	BaseURL string
	HTTP    *http.Client
	// Auth is consulted on every request. It is responsible for setting
	// X-Service-Token, X-Api-Key, and X-Tenant-Id as appropriate. The
	// client never sets these headers directly.
	Auth AuthDispatch
}

// NewHTTPClient builds a client. baseURL is the context URL (e.g.
// https://sri.example.com), auth is the dispatcher.
func NewHTTPClient(baseURL string, auth AuthDispatch) *HTTPClient {
	return &HTTPClient{
		BaseURL: strings.TrimRight(baseURL, "/"),
		HTTP:    &http.Client{Timeout: 30 * time.Second},
		Auth:    auth,
	}
}

// Health implements Client. Uses GET <baseURL>/health (anonymous).
func (c *HTTPClient) Health(ctx context.Context) (Health, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.BaseURL+"/health", nil)
	if err != nil {
		return Health{}, errs.Wrap(errs.CodeGeneric, err, "build health request", "")
	}
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return Health{}, errs.Wrap(errs.CodeNetwork, err, "health request failed", "check connectivity to the billing host")
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return Health{}, mapHTTPError(resp.StatusCode, body, "health")
	}
	var h Health
	if err := json.Unmarshal(body, &h); err != nil {
		return Health{}, errs.Wrap(errs.CodeGeneric, err, "decode health response", "")
	}
	return h, nil
}

// Ready implements Client. Probes GET /health/ready (readiness). The
// backend returns 200 {status:"Ready"} when the DB is reachable and 503
// (no useful body) when it is not; we translate the 503 into a
// CLIError(CodeDBUnavailable) so handlers can treat the absence of a
// Ready result as "degraded" without re-checking the status code.
func (c *HTTPClient) Ready(ctx context.Context) (Health, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.BaseURL+"/health/ready", nil)
	if err != nil {
		return Health{}, errs.Wrap(errs.CodeGeneric, err, "build ready request", "")
	}
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return Health{}, errs.Wrap(errs.CodeNetwork, err, "readiness request failed", "check connectivity to the billing host")
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusServiceUnavailable {
		// 503 is the documented degraded signal (DB not reachable).
		// We return a CLIError with code db_unavailable and MarkRetryable
		// so callers / agents know it might recover.
		return Health{}, errs.New(
			errs.CodeDBUnavailable,
			"readiness: backend returned 503",
			"the database is not reachable; check `sriyactl infra logs` and `sriyactl infra doctor`",
		).MarkRetryable()
	}
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK {
		return Health{}, mapHTTPError(resp.StatusCode, body, "ready")
	}
	var h Health
	if err := json.Unmarshal(body, &h); err != nil {
		return Health{}, errs.Wrap(errs.CodeGeneric, err, "decode ready response", "")
	}
	return h, nil
}

// BootstrapTenant implements Client. Sends POST <baseURL>/api/v1/bootstrap
// as multipart/form-data per the backend contract (OQ 3.4).
//
// Per the verified backend contract (GlobalExceptionHandler.cs:106-112 +
// TenantBootstrapService.cs:67), a RUC duplicate is mapped to **HTTP 400
// BadRequest** with a ProblemDetails body whose Detail contains the
// Spanish sentinel "Ya existe un tenant con el RUC '...'". The CLI's
// 409-detection branch was a fabricated contract; the backend never
// returns 409 for tenant creation (the only 409 in the API is
// SecuencialExhaustedException, which is unrelated).
func (c *HTTPClient) BootstrapTenant(ctx context.Context, in BootstrapRequest) (BootstrapResponse, error) {
	if err := in.Validate(); err != nil {
		return BootstrapResponse{}, err
	}
	// Build multipart body.
	body := &bytes.Buffer{}
	mw := multipart.NewWriter(body)
	textFields := map[string]string{
		"ruc":             in.RUC,
		"razonSocial":     in.RazonSocial,
		"ownerName":       in.OwnerName,
		"password":        in.Password,
		"nombreComercial": in.NombreComercial,
		"correoContacto":  in.CorreoContacto,
		"apiKeyName":      in.APIKeyName,
	}
	for k, v := range textFields {
		if v == "" {
			continue
		}
		if err := mw.WriteField(k, v); err != nil {
			return BootstrapResponse{}, errs.Wrap(errs.CodeGeneric, err, "build multipart form", "")
		}
	}
	// File part: open the cert, stream it in.
	f, err := os.Open(in.CertificatePath)
	if err != nil {
		return BootstrapResponse{}, errs.Wrap(errs.CodeUsage, err, "open certificate file", "check --cert path and permissions")
	}
	defer f.Close()
	fw, err := mw.CreateFormFile("certificate", filepath.Base(in.CertificatePath))
	if err != nil {
		return BootstrapResponse{}, errs.Wrap(errs.CodeGeneric, err, "create file part", "")
	}
	if _, err := io.Copy(fw, f); err != nil {
		return BootstrapResponse{}, errs.Wrap(errs.CodeGeneric, err, "stream certificate", "")
	}
	if err := mw.Close(); err != nil {
		return BootstrapResponse{}, errs.Wrap(errs.CodeGeneric, err, "finalize multipart", "")
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.BaseURL+"/api/v1/bootstrap", body)
	if err != nil {
		return BootstrapResponse{}, errs.Wrap(errs.CodeGeneric, err, "build request", "")
	}
	req.Header.Set("Content-Type", mw.FormDataContentType())
	// Per OQ 3.4: only X-Service-Token; X-Tenant-Id is omitted.
	c.Auth.SetRequestAuth(req, AuthCallOptions{Auth: AuthServiceToken})

	resp, err := c.HTTP.Do(req)
	if err != nil {
		return BootstrapResponse{}, errs.Wrap(errs.CodeNetwork, err, "bootstrap request failed", "check connectivity and the service token")
	}
	defer resp.Body.Close()
	rb, _ := io.ReadAll(resp.Body)
	// Note: we do NOT special-case 409 here. The verified contract says
	// 409 is reserved for SecuencialExhaustedException (not a tenant
	// bootstrap concern) and the RUC duplicate path is 400 +
	// ProblemDetails. The 400+duplicate heuristic is applied by the
	// caller (TenantCreateHandler) via classifyBootstrapError.
	if resp.StatusCode >= 400 {
		return BootstrapResponse{}, classifyBootstrapError(resp.StatusCode, rb)
	}
	var out BootstrapResponse
	if err := json.Unmarshal(rb, &out); err != nil {
		return BootstrapResponse{}, errs.Wrap(errs.CodeGeneric, err, "decode bootstrap response", "")
	}
	return out, nil
}

// CertStatus implements Client. Requires X-Tenant-Id (resolved from local
// config by the caller before invoking). The backend returns 200 with a
// JSON array of CertificateResponse records (CertificateEndpoints.cs:72-80).
// A tenant without certificates is **200 []** — NOT 404.
func (c *HTTPClient) CertStatus(ctx context.Context, tenantID string) ([]Certificate, error) {
	if tenantID == "" {
		return nil, errs.New(errs.CodeUsage, "tenant id required for cert status", "pass --tenant <alias> or set current tenant")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.BaseURL+"/api/v1/certificates", nil)
	if err != nil {
		return nil, errs.Wrap(errs.CodeGeneric, err, "build cert request", "")
	}
	c.Auth.SetRequestAuth(req, AuthCallOptions{
		Auth:     AuthServiceToken,
		TenantID: tenantID,
	})
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return nil, errs.Wrap(errs.CodeNetwork, err, "cert request failed", "check connectivity and the service token")
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	// The verified backend contract says 200 [] for "no cert". The
	// previous 404-detection branch was a fabricated contract; we no
	// longer special-case 404 here. The empty-list → cert_not_found
	// mapping is performed by the handler (CertStatusHandler).
	if resp.StatusCode >= 400 {
		return nil, mapHTTPError(resp.StatusCode, body, "cert")
	}
	// The backend may return either an array or a paginated envelope;
	// v1 accepts the array shape (see CertificateEndpoints.cs).
	var arr []Certificate
	if err := json.Unmarshal(body, &arr); err != nil {
		return nil, errs.Wrap(errs.CodeGeneric, err, "decode cert response", "")
	}
	return arr, nil
}

// classifyBootstrapError translates a non-2xx bootstrap response into a
// stable CLIError. We keep the heuristic narrow (design §#3):
//
//   - 400 + ProblemDetails with Detail matching "ya existe un tenant" ⇒
//     tenant_duplicate (exit 5). The Spanish marker is set by
//     TenantBootstrapService.cs:67 — it is a stable backend string.
//   - 400 + ProblemDetails with a different Detail ⇒ bootstrap_bad_request,
//     with the Detail verbatim so the operator sees exactly what failed
//     (RUC inválido, cert format, password, etc.).
//   - 401/403 ⇒ auth_invalid.
//   - 404 ⇒ not_found (sanity — bootstrap should not 404, but report it
//     cleanly if the backend changes).
//   - 5xx ⇒ network (retryable).
//   - Anything else ⇒ generic with the body verbatim.
func classifyBootstrapError(status int, body []byte) error {
	raw := strings.TrimSpace(string(body))
	if raw == "" {
		raw = "empty response body from bootstrap"
	}
	if status == http.StatusBadRequest {
		var pd problemDetails
		if err := json.Unmarshal(body, &pd); err == nil {
			detail := strings.ToLower(pd.Detail)
			// Spanish sentinel from TenantBootstrapService.cs:67. We
			// match on the full phrase to avoid false positives with
			// unrelated "ruc" mentions (e.g. InvalidRucException).
			if strings.Contains(detail, "ya existe un tenant") {
				return errs.New(
					errs.CodeTenantDuplicate,
					"tenant already exists for this ruc",
					"pick a different ruc or use `sriyactl tenant list` to see existing tenants",
				)
			}
			// Some other validation problem. Surface the Detail verbatim
			// so the operator can act on it.
			msg := pd.Detail
			if msg == "" {
				msg = raw
			}
			return errs.New(errs.CodeBootstrapBadReq, msg, "fix the inputs and retry")
		}
		// Body is not a ProblemDetails (rare). Fall through to generic
		// with the body verbatim so nothing is hidden.
		return errs.New(errs.CodeBootstrapBadReq, raw, "fix the inputs and retry")
	}
	return mapHTTPError(status, body, "bootstrap")
}

// mapHTTPError translates a non-2xx response into a stable CLIError for
// the generic, non-bootstrap endpoints (health, ready, cert).
func mapHTTPError(status int, body []byte, op string) error {
	raw := strings.TrimSpace(string(body))
	if raw == "" {
		raw = fmt.Sprintf("%s: empty response body", op)
	}
	switch {
	case status == http.StatusUnauthorized || status == http.StatusForbidden:
		return errs.New(errs.CodeAuth, raw, "check SRIYACTL_SERVICE_TOKEN or the context's service token in the keychain")
	case status == http.StatusNotFound:
		return errs.New(errs.CodeNotFound, raw, "verify the resource exists (run `sriyactl tenant list` or `sriyactl infra status`)")
	case status == http.StatusConflict:
		// 409 still mapped to a generic conflict for endpoints other
		// than bootstrap. The bootstrap path does its own classification
		// and the cert/tenant list endpoints should not surface 409.
		return errs.New(errs.CodeConflict, raw, "the resource already exists")
	case status >= 500:
		return errs.New(errs.CodeNetwork, raw, "backend error — retry, or check `sriyactl infra status`").MarkRetryable()
	}
	// 400-499 (excluding 401/403/404/409): treat as a usage error with
	// the backend's message verbatim so the operator can see exactly
	// what failed validation.
	return errs.New(errs.CodeGeneric, raw, "fix the inputs and retry")
}
