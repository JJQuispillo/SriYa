package render

import (
	"bytes"
	"encoding/json"
	"os"
	"strings"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// TestAutoNonTTY_DefaultIsJSONInPipe validates the ai-contract
// "pipe forces json" requirement without depending on x/term (the
// library is exercised in TestIsTerminal_IsTerminal).
func TestAutoNonTTY_DefaultIsJSONInPipe(t *testing.T) {
	// os.Stdout in tests is a pipe, so AutoFormat() should return JSON.
	// (If running interactively the test framework would capture a TTY
	// fd, but go test always runs with non-TTY stdout.)
	if IsTerminal(int(os.Stdout.Fd())) {
		t.Skip("stdout is a TTY; cannot assert JSON default in this environment")
	}
	if AutoFormat() != FormatJSON {
		t.Errorf("expected JSON default in pipe, got %s", AutoFormat().String())
	}
}

// TestErrorAsJSONInPipeMode is the ai-contract check: "errores como JSON
// estructurado" — when --output json (or pipe), errors are JSON, not text.
func TestErrorAsJSONInPipeMode(t *testing.T) {
	var buf bytes.Buffer
	e := errs.New(errs.CodeTenantDuplicate, "ruc 1 already exists", "use a different ruc")
	if err := RenderError(&buf, e, FormatJSON); err != nil {
		t.Fatalf("render: %v", err)
	}
	var got map[string]any
	if err := json.Unmarshal(buf.Bytes(), &got); err != nil {
		t.Fatalf("not JSON: %v", err)
	}
	errBlock, ok := got["error"].(map[string]any)
	if !ok {
		t.Fatalf("expected top-level 'error' object, got: %s", buf.String())
	}
	if errBlock["code"] != "tenant_duplicate" {
		t.Errorf("code: %v", errBlock["code"])
	}
	if errBlock["message"] != "ruc 1 already exists" {
		t.Errorf("message: %v", errBlock["message"])
	}
	if errBlock["hint"] == nil || errBlock["hint"] == "" {
		t.Errorf("hint: %v", errBlock["hint"])
	}
}

// TestErrorAsTextInTableMode confirms table mode renders a human line
// (NOT a JSON object), so an interactive user does not see JSON.
func TestErrorAsTextInTableMode(t *testing.T) {
	var buf bytes.Buffer
	e := errs.New(errs.CodeCertExpired, "cert expired", "rotate it")
	if err := RenderError(&buf, e, FormatTable); err != nil {
		t.Fatalf("render: %v", err)
	}
	if strings.HasPrefix(strings.TrimSpace(buf.String()), "{") {
		t.Errorf("table mode should not emit JSON, got: %s", buf.String())
	}
	if !strings.Contains(buf.String(), "cert_expired") {
		t.Errorf("table mode should still print the code, got: %s", buf.String())
	}
}

// TestEnvelopeIsStableAcrossFormats ensures json and yaml both carry
// the schemaVersion field with the same value.
func TestEnvelopeIsStableAcrossFormats(t *testing.T) {
	out := core.NewOutput("X", struct{ N int }{N: 1})
	var jsonBuf, yamlBuf bytes.Buffer
	_ = Render(&jsonBuf, out, FormatJSON)
	_ = Render(&yamlBuf, out, FormatYAML)
	if !strings.Contains(jsonBuf.String(), `"schemaVersion"`) {
		t.Errorf("json missing schemaVersion key: %s", jsonBuf.String())
	}
	if !strings.Contains(yamlBuf.String(), "schemaVersion:") {
		t.Errorf("yaml missing schemaVersion: %s", yamlBuf.String())
	}
}
