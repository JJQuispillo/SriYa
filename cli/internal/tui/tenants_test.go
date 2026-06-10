package tui

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/charmbracelet/huh"

	"github.com/JJQuispillo/billing/cli/internal/core"
	"github.com/JJQuispillo/billing/cli/internal/errs"
	"github.com/JJQuispillo/billing/cli/internal/ops"
)

// fakeTenantHandlers builds an in-memory tenant backend.
func fakeTenantHandlers(t *testing.T) (tenantHandlers, *struct {
	active  string
	created []core.TenantCreateRequest
}) {
	t.Helper()
	state := &struct {
		active  string
		created []core.TenantCreateRequest
	}{active: "acme"}

	h := tenantHandlers{
		list: func(ctx context.Context, _ struct{}) (core.Output[core.TenantListResult], error) {
			return core.NewOutput("TenantList", core.TenantListResult{
				Context: "local",
				Active:  state.active,
				Tenants: []core.TenantListEntry{
					{Alias: "acme", ID: "tid-1", RUC: "111", Env: "prod", IsActive: state.active == "acme"},
					{Alias: "beta", ID: "tid-2", RUC: "222", Env: "test", IsActive: state.active == "beta"},
				},
			}), nil
		},
		use: func(ctx context.Context, in core.TenantUseRequest) (core.Output[core.TenantUseResult], error) {
			state.active = in.Alias
			return core.NewOutput("TenantUse", core.TenantUseResult{Context: "local", Alias: in.Alias, ID: "tid-x"}), nil
		},
		create: func(ctx context.Context, in core.TenantCreateRequest) (core.Output[core.TenantCreateResult], error) {
			state.created = append(state.created, in)
			return core.NewOutput("TenantCreateResult", core.TenantCreateResult{}), nil
		},
	}
	return h, state
}

// loadList drives Init's load command through Update.
func loadList(t *testing.T, s *tenantsScreen) *tenantsScreen {
	t.Helper()
	cmd := s.Init()
	if cmd == nil {
		t.Fatal("Init must schedule the list load")
	}
	ns, _ := s.Update(cmd())
	return ns.(*tenantsScreen)
}

// TestTenants_ListRendersRows: the table shows every tenant with the
// active mark.
func TestTenants_ListRendersRows(t *testing.T) {
	h, _ := fakeTenantHandlers(t)
	s := newTenantsScreenWith(ops.Options{}, h, nil)
	s = loadList(t, s)

	view := s.View()
	for _, want := range []string{"acme", "beta", "111", "222"} {
		if !strings.Contains(view, want) {
			t.Errorf("tenants view missing %q:\n%s", want, view)
		}
	}
}

// TestTenants_UseSelected: enter on a row activates it via the use
// handler and reloads the list.
func TestTenants_UseSelected(t *testing.T) {
	h, state := fakeTenantHandlers(t)
	s := newTenantsScreenWith(ops.Options{}, h, nil)
	s = loadList(t, s)

	// Move to the second row and activate it.
	ns, _ := s.Update(keyMsg("down"))
	s = ns.(*tenantsScreen)
	ns, cmd := s.Update(keyMsg("enter"))
	s = ns.(*tenantsScreen)
	if cmd == nil {
		t.Fatal("enter must schedule the use action")
	}
	res := cmd() // run the pipeline
	if state.active != "beta" {
		t.Errorf("expected beta active, got %q", state.active)
	}
	ns, reload := s.Update(res)
	s = ns.(*tenantsScreen)
	if !strings.Contains(s.View(), "tenant activo: beta") {
		t.Errorf("expected feedback line, got:\n%s", s.View())
	}
	if reload == nil {
		t.Fatal("a successful use must reload the list")
	}
}

// TestTenants_ReadOnlyBlocksUse: under readonly the use action is
// blocked by the SAME guard as cobra — the handler never runs.
func TestTenants_ReadOnlyBlocksUse(t *testing.T) {
	h, state := fakeTenantHandlers(t)
	s := newTenantsScreenWith(ops.Options{ReadOnly: true}, h, nil)
	s = loadList(t, s)

	ns, cmd := s.Update(keyMsg("enter"))
	s = ns.(*tenantsScreen)
	if cmd == nil {
		t.Fatal("enter must schedule the use action")
	}
	res := cmd()
	rm, ok := res.(resultMsg)
	if !ok {
		t.Fatalf("expected resultMsg, got %T", res)
	}
	var ce *errs.CLIError
	if rm.err == nil || !errors.As(rm.err, &ce) || ce.Code != errs.CodeReadOnlyBlocked {
		t.Errorf("expected readonly_blocked, got %v", rm.err)
	}
	if state.active != "acme" {
		t.Errorf("active tenant must not change under readonly, got %q", state.active)
	}
	// The error surfaces in the view without crashing.
	ns, _ = s.Update(res)
	s = ns.(*tenantsScreen)
	if !strings.Contains(s.View(), "error:") {
		t.Errorf("expected the error in the view:\n%s", s.View())
	}
}

// TestTenants_CreateFormMasksPasswordAndSubmits: 'c' opens the huh form;
// the password field is masked; completing the form invokes the create
// handler with the captured fields.
func TestTenants_CreateFormMasksPasswordAndSubmits(t *testing.T) {
	h, state := fakeTenantHandlers(t)
	s := newTenantsScreenWith(ops.Options{}, h, nil)
	s = loadList(t, s)

	ns, _ := s.Update(keyMsg("c"))
	s = ns.(*tenantsScreen)
	if s.mode != tenantsCreate || s.form == nil {
		t.Fatal("c must open the create form")
	}

	// Fill the bound data directly (huh keystroke simulation is covered
	// by huh's own tests) and force completion through the screen's
	// submit path.
	*s.formData = tenantCreateForm{
		alias:       "gama",
		ruc:         "333",
		razonSocial: "Gama SA",
		ownerName:   "Ana",
		password:    "s3cr3t",
		certPath:    "/tmp/cert.p12",
	}
	// The masked echo is a property of the field construction: rendering
	// the form after typing must never echo the password value.
	form, data := s.newCreateForm()
	data.password = "supersecreto"
	_ = form.Init()
	if v := form.View(); strings.Contains(v, "supersecreto") {
		t.Errorf("password must be masked in the form view:\n%s", v)
	}

	s.form.State = huh.StateCompleted
	ns, cmd := s.Update(struct{}{}) // any msg routes through updateCreate
	s = ns.(*tenantsScreen)
	if cmd == nil {
		t.Fatal("completing the form must schedule the create action")
	}
	res := cmd()
	if len(state.created) != 1 {
		t.Fatalf("expected 1 create call, got %d", len(state.created))
	}
	got := state.created[0]
	if got.Alias != "gama" || got.Password != "s3cr3t" || got.CertificatePath != "/tmp/cert.p12" {
		t.Errorf("create request mismatch: %+v", got)
	}
	if got.ShowAPIKey {
		t.Error("the TUI must never request the apiKey in tenant create output")
	}
	ns, _ = s.Update(res)
	s = ns.(*tenantsScreen)
	if s.mode != tenantsList {
		t.Error("after submit the screen returns to the list")
	}
}

// TestTenants_EscCancelsCreate: esc inside the form aborts without
// executing anything and returns to the list.
func TestTenants_EscCancelsCreate(t *testing.T) {
	h, state := fakeTenantHandlers(t)
	s := newTenantsScreenWith(ops.Options{}, h, nil)
	s = loadList(t, s)

	ns, _ := s.Update(keyMsg("c"))
	s = ns.(*tenantsScreen)
	ns, _ = s.Update(keyMsg("esc"))
	s = ns.(*tenantsScreen)
	if s.mode != tenantsList {
		t.Error("esc must return to the list")
	}
	if len(state.created) != 0 {
		t.Error("cancelling must not create anything")
	}
}

// TestTenants_WireErrShowsUnavailable: when deps cannot be built (no
// config yet) the screen reports it and stays navigable.
func TestTenants_WireErrShowsUnavailable(t *testing.T) {
	wireErr := errs.New(errs.CodeConfigInvalid, "no current_context in config", "run context use")
	s := newTenantsScreenWith(ops.Options{}, tenantHandlers{}, wireErr)
	if cmd := s.Init(); cmd != nil {
		t.Fatal("Init must not schedule a load without wiring")
	}
	if !strings.Contains(s.View(), "no disponible") {
		t.Errorf("expected 'no disponible', got:\n%s", s.View())
	}
	_, cmd := s.Update(keyMsg("esc"))
	if _, ok := cmd().(navPopMsg); !ok {
		t.Error("esc must pop back to the menu")
	}
}
