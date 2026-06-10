// Package render projects core.Output[T] values into table | json | yaml.
//
// This is the SOLE place in sriyactl that decides presentation. Handlers
// MUST NOT call fmt.Print, log, or build strings for stdout. By enforcing
// that boundary, --output json and the future MCP server are free: they
// receive the same typed Output the table renderer uses.
package render

import (
	"encoding/json"
	"fmt"
	"io"
	"reflect"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"

	"gopkg.in/yaml.v3"
)

// Format selects the wire format. Table is for humans; JSON and YAML are
// stable, machine-readable, and identical modulo syntax.
type Format int

const (
	FormatTable Format = iota
	FormatJSON
	FormatYAML
)

// String renders the format name (used by --output validation).
func (f Format) String() string {
	switch f {
	case FormatJSON:
		return "json"
	case FormatYAML:
		return "yaml"
	case FormatTable:
		return "table"
	default:
		return "table"
	}
}

// ParseFormat parses the canonical name. Unknown values fall back to table.
func ParseFormat(s string) Format {
	switch s {
	case "json":
		return FormatJSON
	case "yaml", "yml":
		return FormatYAML
	case "table":
		return FormatTable
	default:
		return FormatTable
	}
}

// Renderable is the contract for table output. Types that implement it
// control their own columns/rows. Built-in Output[T] does NOT implement
// Renderable directly; we use a renderer-specific RowExtractor strategy
// (see renderTable) so we don't force every Data type to implement the
// interface.
type Renderable interface {
	Columns() []string
	Rows() [][]string
}

// Render projects an Output to the writer in the requested format. The
// envelope schemaVersion is preserved verbatim in JSON and YAML modes; in
// Table mode it is omitted (tables are for humans, not contracts).
func Render[T any](w io.Writer, out core.Output[T], f Format) error {
	switch f {
	case FormatJSON:
		return renderJSON(w, out)
	case FormatYAML:
		return renderYAML(w, out)
	case FormatTable:
		return renderTable(w, out)
	default:
		return renderTable(w, out)
	}
}

func renderJSON[T any](w io.Writer, out core.Output[T]) error {
	enc := json.NewEncoder(w)
	enc.SetIndent("", "  ")
	return enc.Encode(out)
}

func renderYAML[T any](w io.Writer, out core.Output[T]) error {
	return yaml.NewEncoder(w).Encode(out)
}

// renderTable is a best-effort reflection-based table printer. If the Data
// type implements Renderable, it gets full control. Otherwise the renderer
// uses reflection to walk struct fields and produce a single row.
//
// This avoids the v1 trap of "every command writes its own table format".
// Commands SHOULD implement Renderable for prettier output, but the default
// always works.
func renderTable[T any](w io.Writer, out core.Output[T]) error {
	if r, ok := any(out.Data).(Renderable); ok {
		return writeTable(w, []string{out.Kind}, [][]string{{out.SchemaVersion}}, r.Columns(), r.Rows())
	}
	cols, rows := reflectTable(out.Data)
	return writeTable(w, []string{"kind", "schemaVersion"}, [][]string{{out.Kind, out.SchemaVersion}}, cols, rows)
}

func writeTable(w io.Writer, headers []string, summary [][]string, cols []string, rows [][]string) error {
	if len(headers) > 0 {
		if _, err := fmt.Fprintln(w, joinTabs(headers)); err != nil {
			return err
		}
		for _, row := range summary {
			if _, err := fmt.Fprintln(w, joinTabs(row)); err != nil {
				return err
			}
		}
	}
	if len(cols) == 0 {
		return nil
	}
	if _, err := fmt.Fprintln(w, ""); err != nil {
		return err
	}
	if _, err := fmt.Fprintln(w, joinTabs(cols)); err != nil {
		return err
	}
	for _, row := range rows {
		if _, err := fmt.Fprintln(w, joinTabs(row)); err != nil {
			return err
		}
	}
	return nil
}

func joinTabs(parts []string) string {
	out := ""
	for i, p := range parts {
		if i > 0 {
			out += "\t"
		}
		out += p
	}
	return out
}

func reflectTable(data any) ([]string, [][]string) {
	v := reflect.ValueOf(data)
	for v.Kind() == reflect.Ptr {
		v = v.Elem()
	}
	if v.Kind() != reflect.Struct {
		return nil, nil
	}
	t := v.Type()
	cols := make([]string, 0, t.NumField())
	vals := make([]string, 0, t.NumField())
	for i := 0; i < t.NumField(); i++ {
		f := t.Field(i)
		cols = append(cols, f.Name)
		vals = append(vals, fmt.Sprintf("%v", v.Field(i).Interface()))
	}
	return cols, [][]string{vals}
}

// RenderError projects an error to the writer in the requested format.
// JSON/YAML modes emit {code,message,hint,retryable} as a top-level object
// — never as a free string. This is the ai-contract REQ-ERR-001.
func RenderError(w io.Writer, err error, f Format) error {
	if err == nil {
		return nil
	}
	var ce *errs.CLIError
	if !asCLIError(err, &ce) {
		ce = &errs.CLIError{
			Code:    errs.CodeGeneric,
			Message: err.Error(),
		}
	}
	envelope := struct {
		Error struct {
			Code      errs.Code `json:"code"`
			Message   string    `json:"message"`
			Hint      string    `json:"hint,omitempty"`
			Retryable bool      `json:"retryable"`
		} `json:"error"`
	}{}
	envelope.Error.Code = ce.Code
	envelope.Error.Message = ce.Message
	envelope.Error.Hint = ce.Hint
	envelope.Error.Retryable = ce.Retryable

	switch f {
	case FormatJSON:
		return json.NewEncoder(w).Encode(envelope)
	case FormatYAML:
		return yaml.NewEncoder(w).Encode(envelope)
	default:
		// Table mode: human-friendly one-liner, NOT a JSON object.
		_, werr := fmt.Fprintf(w, "error: %s: %s\n", ce.Code, ce.Message)
		return werr
	}
}

func asCLIError(err error, target **errs.CLIError) bool {
	for err != nil {
		if ce, ok := err.(*errs.CLIError); ok {
			*target = ce
			return true
		}
		type unwrapper interface{ Unwrap() error }
		u, ok := err.(unwrapper)
		if !ok {
			return false
		}
		err = u.Unwrap()
	}
	return false
}
