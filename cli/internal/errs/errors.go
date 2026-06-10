// Package errs defines the CLIError type and the canonical exit-code map
// for sriyactl. Every error returned by a handler or the CLI middleware
// flows through this package, so the JSON error envelope and the exit code
// are deterministic by construction.
//
// Exit codes (stable, ai-contract REQ-ERR-002):
//
//	0  OK
//	1  generic error
//	2  usage / flag error (incl. confirmation_required / confirmation_aborted)
//	3  auth error (invalid or missing credentials)
//	4  resource not found
//	5  conflict (duplicate, already-exists, etc.)
//	6  transient / network — retryable
//	7  mutating command blocked by read-only
//	8  cert expiring (within --warn-days, see ai-contract REQ-ERR-002)
//	9  cert expired
//	10 upgrade /health/ready never recovered (rollback already done)
//	11 one or more preflight checks failed
package errs

import (
	"errors"
	"fmt"
)

// Code is a stable, machine-readable identifier for a class of error. New
// codes may be added; renaming or removing a code is a breaking change.
type Code string

const (
	CodeGeneric           Code = "generic"
	CodeUsage             Code = "usage"
	CodeAuth              Code = "auth_invalid"
	CodeNotFound          Code = "not_found"
	CodeConflict          Code = "conflict"
	CodeNetwork           Code = "network"
	CodeReadOnlyBlocked   Code = "readonly_blocked"
	CodeInstallDirInvalid Code = "install_dir_invalid"
	CodeTenantNotFound    Code = "tenant_not_found"
	CodeTenantDuplicate   Code = "tenant_duplicate"
	CodeCertNotFound      Code = "cert_not_found"
	CodeCertExpiring      Code = "cert_expiring"
	CodeCertExpired       Code = "cert_expired"
	CodeCertInvalidFormat Code = "cert_invalid_format"
	CodeCertInvalidPass   Code = "cert_invalid_password"
	CodeBootstrapBadReq   Code = "bootstrap_bad_request"
	// CodeBootstrapInputRequired is returned by the install→bootstrap chain
	// when running headless (non-TTY) and a required bootstrap input (RUC,
	// razón social, owner name, password, certificate) is missing. It is a
	// usage error (exit 2): supply the flags or run on a TTY where the values
	// are prompted.
	CodeBootstrapInputRequired Code = "bootstrap_input_required"
	CodeDBUnavailable          Code = "db_unavailable"
	// CodeUpgradeTimeout's wire value is "upgrade_health_timeout" (per
	// ai-contract). The Go name is kept for readability at call sites.
	CodeUpgradeTimeout    Code = "upgrade_health_timeout"
	CodeDockerUnavailable Code = "docker_unavailable"
	// CodeInstallHealthTimeout is returned by `infra install` when the
	// freshly provisioned stack never reaches /health/ready within the
	// health-wait budget. The stack is left running (no rollback — there
	// is no previous state to restore on a fresh install) so the operator
	// can inspect logs. Shares the "health never recovered" exit class
	// (10) with CodeUpgradeTimeout.
	CodeInstallHealthTimeout Code = "install_health_timeout"
	CodeDoctorCheckFailed    Code = "doctor_check_failed"
	CodeConfigInvalid        Code = "config_invalid"
	// Added in sriyactl-v1-fixes (design §#1, §#9).
	CodeConfirmRequired Code = "confirmation_required"
	CodeConfirmAborted  Code = "confirmation_aborted"
)

// CLIError is the canonical error type. It carries a stable code, a
// human-readable message, an actionable hint, and a retryable flag. The
// render layer projects it as a JSON object {code, message, hint, retryable}
// in JSON/YAML mode (see render package).
//
// RenderPayload is a "render-and-signal" flag (design §#4): when true, the
// CLI middleware prints the handler's data payload to stdout before the
// error envelope to stderr. The flag does NOT change the exit code; it
// only changes whether the payload is preserved alongside the signal.
type CLIError struct {
	Code          Code
	Message       string
	Hint          string
	Retryable     bool
	RenderPayload bool
	Cause         error
}

// Error implements the error interface. The cause is unwrapped so that
// errors.Is / errors.As work transparently.
func (e *CLIError) Error() string {
	if e == nil {
		return ""
	}
	if e.Cause != nil {
		return fmt.Sprintf("%s: %s: %v", e.Code, e.Message, e.Cause)
	}
	return fmt.Sprintf("%s: %s", e.Code, e.Message)
}

// Unwrap exposes the underlying cause for errors.Is / errors.As.
func (e *CLIError) Unwrap() error { return e.Cause }

// Renderable is the interface implemented by errors that want the
// middleware to render the handler's data payload before the error.
// It is implemented by *CLIError via the RenderPayload field; we expose
// it as an interface so middleware checks via errors.As (design §#4).
type Renderable interface {
	Renderable() bool
}

// Renderable reports whether this error is asking the middleware to
// render the handler's data payload before emitting the error envelope.
// Implements the Renderable interface.
func (e *CLIError) Renderable() bool {
	return e != nil && e.RenderPayload
}

// MarkRenderable flags the error as render-and-signal. Returns the
// receiver for chaining at the call site (design §#4).
func (e *CLIError) MarkRenderable() *CLIError {
	e.RenderPayload = true
	return e
}

// New constructs a CLIError with the given code, message, and hint. Retry
// defaults to false.
func New(code Code, message, hint string) *CLIError {
	return &CLIError{Code: code, Message: message, Hint: hint}
}

// Wrap attaches a cause to a CLIError. Useful when translating errors
// returned by lower layers (http, viper, go-keyring) into a stable code.
func Wrap(code Code, cause error, message, hint string) *CLIError {
	return &CLIError{Code: code, Message: message, Hint: hint, Cause: cause}
}

// MarkRetryable marks the error as retryable. Returns the receiver for
// chaining. The name avoids the field name `Retryable`.
func (e *CLIError) MarkRetryable() *CLIError {
	e.Retryable = true
	return e
}

// ExitCode returns the process exit code for the given error. If the error
// is not a CLIError, exit 1 is returned. Stable mapping per ai-contract.
func ExitCode(err error) int {
	if err == nil {
		return 0
	}
	var ce *CLIError
	if !errors.As(err, &ce) {
		return 1
	}
	return codeToExit(ce.Code)
}

func codeToExit(c Code) int {
	switch c {
	case CodeGeneric:
		return 1
	case CodeUsage, CodeConfigInvalid, CodeConfirmRequired, CodeConfirmAborted, CodeBootstrapInputRequired:
		return 2
	case CodeAuth:
		return 3
	case CodeNotFound, CodeTenantNotFound, CodeCertNotFound:
		return 4
	case CodeConflict, CodeTenantDuplicate:
		return 5
	case CodeNetwork, CodeDBUnavailable, CodeDockerUnavailable:
		return 6
	case CodeReadOnlyBlocked:
		return 7
	case CodeCertExpiring:
		return 8
	case CodeCertExpired:
		return 9
	case CodeUpgradeTimeout, CodeInstallHealthTimeout:
		return 10
	case CodeDoctorCheckFailed:
		return 11
	default:
		return 1
	}
}
