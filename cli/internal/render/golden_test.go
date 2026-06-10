package render

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"github.com/JJQuispillo/billing/cli/internal/core"
)

// goldenPayloads is the canonical set of golden inputs and expected
// outputs for each Kind. Adding a new Kind requires adding a row here.
var goldenPayloads = []struct {
	kind   string
	data   any
	golden string
}{
	{
		kind: "TenantList",
		data: struct {
			Context string `json:"context"`
			Tenants []struct {
				Alias string `json:"alias"`
				ID    string `json:"id"`
			} `json:"tenants"`
		}{
			Context: "prod",
			Tenants: []struct {
				Alias string `json:"alias"`
				ID    string `json:"id"`
			}{{Alias: "acme", ID: "00000000-0000-0000-0000-000000000001"}},
		},
		golden: `{
  "schemaVersion": "1.0",
  "kind": "TenantList",
  "data": {
    "context": "prod",
    "tenants": [
      {
        "alias": "acme",
        "id": "00000000-0000-0000-0000-000000000001"
      }
    ]
  }
}`,
	},
}

func TestGolden_JSON(t *testing.T) {
	for _, g := range goldenPayloads {
		t.Run(g.kind, func(t *testing.T) {
			var buf bytes.Buffer
			if err := Render(&buf, core.NewOutput(g.kind, g.data), FormatJSON); err != nil {
				t.Fatalf("render: %v", err)
			}
			// Decode-recode to avoid formatting-equivalence issues; we
			// only check semantic equality of the JSON, not byte-for-byte.
			var got, want any
			if err := json.Unmarshal(buf.Bytes(), &got); err != nil {
				t.Fatalf("output not valid JSON: %v", err)
			}
			if err := json.Unmarshal([]byte(g.golden), &want); err != nil {
				t.Fatalf("golden not valid JSON: %v", err)
			}
			gb, _ := json.Marshal(got)
			wb, _ := json.Marshal(want)
			if string(gb) != string(wb) {
				t.Errorf("golden mismatch\n got: %s\nwant: %s", gb, wb)
			}
		})
	}
}

func TestGolden_YAML_HasEnvelope(t *testing.T) {
	for _, g := range goldenPayloads {
		t.Run(g.kind, func(t *testing.T) {
			var buf bytes.Buffer
			if err := Render(&buf, core.NewOutput(g.kind, g.data), FormatYAML); err != nil {
				t.Fatalf("render: %v", err)
			}
			s := buf.String()
			if !strings.Contains(s, "schemaVersion:") {
				t.Errorf("expected yaml to contain schemaVersion, got: %s", s)
			}
			if !strings.Contains(s, "kind: "+g.kind) {
				t.Errorf("expected yaml to contain kind: %s, got: %s", g.kind, s)
			}
		})
	}
}
