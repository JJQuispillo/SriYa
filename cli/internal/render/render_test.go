package render

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

type sampleData struct {
	Name  string `json:"name"`
	Count int    `json:"count"`
}

func TestRender_JSONEnvelopeHasSchemaVersion(t *testing.T) {
	var buf bytes.Buffer
	out := core.NewOutput("Sample", sampleData{Name: "acme", Count: 3})
	if err := Render(&buf, out, FormatJSON); err != nil {
		t.Fatalf("render: %v", err)
	}
	var got map[string]any
	if err := json.Unmarshal(buf.Bytes(), &got); err != nil {
		t.Fatalf("output is not valid JSON: %v\nraw=%s", err, buf.String())
	}
	if got["schemaVersion"] != "1.0" {
		t.Errorf("expected schemaVersion=1.0, got %v", got["schemaVersion"])
	}
	if got["kind"] != "Sample" {
		t.Errorf("expected kind=Sample, got %v", got["kind"])
	}
}

func TestRender_YAMLEnvelopeHasSchemaVersion(t *testing.T) {
	var buf bytes.Buffer
	out := core.NewOutput("Sample", sampleData{Name: "acme", Count: 3})
	if err := Render(&buf, out, FormatYAML); err != nil {
		t.Fatalf("render: %v", err)
	}
	if !strings.Contains(buf.String(), "schemaVersion:") {
		t.Errorf("expected yaml to contain schemaVersion, got: %s", buf.String())
	}
}

func TestRenderError_JSONHasCode(t *testing.T) {
	var buf bytes.Buffer
	e := errs.New(errs.CodeTenantDuplicate, "ruc already exists", "use a different ruc")
	if err := RenderError(&buf, e, FormatJSON); err != nil {
		t.Fatalf("render: %v", err)
	}
	var got struct {
		Error struct {
			Code      errs.Code `json:"code"`
			Message   string    `json:"message"`
			Hint      string    `json:"hint"`
			Retryable bool      `json:"retryable"`
		} `json:"error"`
	}
	if err := json.Unmarshal(buf.Bytes(), &got); err != nil {
		t.Fatalf("output is not valid JSON: %v", err)
	}
	if got.Error.Code != errs.CodeTenantDuplicate {
		t.Errorf("expected code=tenant_duplicate, got %v", got.Error.Code)
	}
	if got.Error.Hint == "" {
		t.Errorf("expected hint to be present")
	}
}

func TestRenderError_PlainErrorBecomesGenericCode(t *testing.T) {
	var buf bytes.Buffer
	plain := errString("kaboom")
	if err := RenderError(&buf, plain, FormatJSON); err != nil {
		t.Fatalf("render: %v", err)
	}
	if !strings.Contains(buf.String(), `"code":"generic"`) {
		t.Errorf("expected generic code, got: %s", buf.String())
	}
}

type errString string

func (e errString) Error() string { return string(e) }
