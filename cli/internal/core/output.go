// Package core defines the foundational types for the sriyactl handler/render
// separation. Every command is modeled as a pure Handler[In, Out] that returns
// an Output[T] envelope. The render layer is the SOLE place that decides
// formatting (table, json, yaml) and the SOLE place that maps errors to
// machine-readable JSON.
package core

import "context"

// Output is the envelope every successful command emits. It carries an
// explicit schemaVersion so JSON/YAML consumers can evolve safely across
// versions of sriyactl.
//
// Convention: Kind is a stable PascalCase identifier for the payload type
// (e.g. "TenantList", "InfraStatus", "CertStatus"). It is the contract key
// for the v2 MCP server and for `sriyactl spec --json`.
type Output[T any] struct {
	SchemaVersion string `json:"schemaVersion" yaml:"schemaVersion"`
	Kind          string `json:"kind"          yaml:"kind"`
	Data          T      `json:"data"          yaml:"data"`
}

// Handler is the cornerstone contract: every command is a typed function
// that receives a request and returns an Output. Handlers MUST NOT perform
// presentation I/O (no fmt.Print, no ANSI). They MUST NOT mutate the global
// state of the CLI; any side effects are tracked in the returned Data and
// gated by guardMutation before execution.
type Handler[In any, Out any] func(ctx context.Context, in In) (Output[Out], error)

// NewOutput is a small constructor that fills SchemaVersion with the
// current contract version and Kind with the caller-supplied name. It exists
// so the constant `schemaVersion` lives in exactly one place.
func NewOutput[T any](kind string, data T) Output[T] {
	return Output[T]{
		SchemaVersion: SchemaVersion,
		Kind:          kind,
		Data:          data,
	}
}

// SchemaVersion is the current version of the Output envelope. Bump this
// when a breaking change is made to the wire shape (Kind values may be added
// at any time without a bump; changes to existing fields require a bump).
const SchemaVersion = "1.0"
