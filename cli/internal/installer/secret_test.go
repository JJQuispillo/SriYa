package installer

import (
	"regexp"
	"testing"
)

var alnum = regexp.MustCompile(`^[A-Za-z0-9]+$`)

func TestGenSecret_CharsetAndLength(t *testing.T) {
	// Run many times: a single sample could miss a stray base64 char.
	for i := 0; i < 200; i++ {
		s, err := GenSecret(DefaultSecretLen)
		if err != nil {
			t.Fatalf("GenSecret: %v", err)
		}
		if len(s) != DefaultSecretLen {
			t.Fatalf("len = %d, want %d", len(s), DefaultSecretLen)
		}
		if !alnum.MatchString(s) {
			t.Fatalf("secret %q contains non-alphanumeric chars (base64 leak?)", s)
		}
	}
}

func TestGenSecret_NoBase64Punctuation(t *testing.T) {
	// Explicit guard: the whole point of this generator is to never emit
	// +, /, or = which break Npgsql / SQL role passwords.
	for i := 0; i < 500; i++ {
		s, err := GenSecret(64)
		if err != nil {
			t.Fatal(err)
		}
		for _, c := range s {
			if c == '+' || c == '/' || c == '=' {
				t.Fatalf("forbidden base64 char %q in secret %q", c, s)
			}
		}
	}
}

func TestGenSecret_RejectsNonPositiveLength(t *testing.T) {
	for _, n := range []int{0, -1, -44} {
		if _, err := GenSecret(n); err == nil {
			t.Errorf("GenSecret(%d) = nil error, want error", n)
		}
	}
}

func TestGenSecret_Uniqueness(t *testing.T) {
	// Two consecutive secrets must differ (sanity check that we read fresh
	// randomness each call, not a cached buffer).
	a, _ := GenSecret(DefaultSecretLen)
	b, _ := GenSecret(DefaultSecretLen)
	if a == b {
		t.Fatalf("two secrets are identical: %q", a)
	}
}

func TestGenSecret_DistributionUsesFullAlphabet(t *testing.T) {
	// Across a large sample every alphabet symbol should appear at least
	// once — proves we are not biased to a subset (and not modulo-biased
	// away from the tail of the alphabet).
	seen := map[rune]bool{}
	for i := 0; i < 1000; i++ {
		s, _ := GenSecret(DefaultSecretLen)
		for _, c := range s {
			seen[c] = true
		}
	}
	for _, c := range secretAlphabet {
		if !seen[c] {
			t.Errorf("alphabet symbol %q never appeared in 1000 samples", c)
		}
	}
}
