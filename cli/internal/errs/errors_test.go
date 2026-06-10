package errs

import "testing"

// TestExitCode_KnownCodes pins the stable mapping per ai-contract
// REQ-ERR-002. The mapping is updated in sriyactl-v1-fixes (design §#9)
// to disambiguate classes that were previously collapsed in the
// network/retryable bucket (exit 6):
//
//   - cert_expiring  → 8
//   - cert_expired   → 9
//   - upgrade_health_timeout → 10
//   - doctor_check_failed    → 11
//
// The previous v1 test asserted the old 6 for those codes; this test
// is REPLACED — the old assertions would silently fail against the
// new mapping and the failure would go unnoticed by CI.
func TestExitCode_KnownCodes(t *testing.T) {
	cases := []struct {
		code Code
		want int
	}{
		{CodeGeneric, 1},
		{CodeUsage, 2},
		{CodeConfigInvalid, 2},
		{CodeConfirmRequired, 2},
		{CodeConfirmAborted, 2},
		{CodeAuth, 3},
		{CodeNotFound, 4},
		{CodeTenantNotFound, 4},
		{CodeCertNotFound, 4},
		{CodeConflict, 5},
		{CodeTenantDuplicate, 5},
		{CodeNetwork, 6},
		{CodeDBUnavailable, 6},
		{CodeDockerUnavailable, 6},
		{CodeReadOnlyBlocked, 7},
		{CodeCertExpiring, 8},
		{CodeCertExpired, 9},
		{CodeUpgradeTimeout, 10},
		{CodeDoctorCheckFailed, 11},
	}
	for _, c := range cases {
		if got := ExitCode(New(c.code, "x", "")); got != c.want {
			t.Errorf("code %s: want exit %d, got %d", c.code, c.want, got)
		}
	}
}

func TestExitCode_UnknownError(t *testing.T) {
	if got := ExitCode(nil); got != 0 {
		t.Errorf("nil error should be exit 0, got %d", got)
	}
	plain := errorString("boom")
	if got := ExitCode(plain); got != 1 {
		t.Errorf("plain error should be exit 1, got %d", got)
	}
}

type errorString string

func (e errorString) Error() string { return string(e) }
