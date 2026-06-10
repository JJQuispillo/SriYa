// Package installer holds the net-new day-1 provisioning plumbing that
// `sriyactl infra install` orchestrates: charset-safe secret generation,
// `.env` rendering, pinned compose download, TTY prompts, and docker /
// OS detection. It is deliberately decoupled from cobra (the cli layer)
// and from the core handlers — the handlers call into this package; this
// package never reaches back up. All network / filesystem / external-binary
// I/O is funneled through small interfaces so it can be faked in tests.
package installer

import (
	"crypto/rand"
	"math/big"

	"github.com/JJQuispillo/billing/cli/internal/errs"
)

// secretAlphabet is the charset every generated secret is drawn from. It is
// intentionally limited to `[A-Za-z0-9]`.
//
// Why not base64? base64 emits `+`, `/`, and `=` padding. Those characters
// break two things in the SriYa stack:
//
//   - Npgsql connection-string parsing (a `;` or `=` in a password value
//     needs escaping/quoting that several layers get wrong), and
//   - SQL role passwords created from the rendered .env.
//
// install.sh's gen_secret uses exactly this alphabet via
// `tr -dc 'A-Za-z0-9'`; we keep byte-for-byte parity.
const secretAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"

// DefaultSecretLen matches install.sh: 44 chars from a 62-symbol alphabet
// is ~262 bits of entropy. Kept as a named constant so RenderEnv and the
// tests share one source of truth.
const DefaultSecretLen = 44

// GenSecret returns a cryptographically-random string of length n drawn
// uniformly from `[A-Za-z0-9]`.
//
// It uses rejection-free uniform sampling via crypto/rand + big.Int over
// the alphabet length, so there is NO modulo bias (every symbol is equally
// likely). The result is guaranteed to match `^[A-Za-z0-9]+$` — never any
// base64 punctuation.
func GenSecret(n int) (string, error) {
	if n <= 0 {
		return "", errs.New(errs.CodeUsage, "secret length must be positive", "pass n >= 1")
	}
	max := big.NewInt(int64(len(secretAlphabet)))
	out := make([]byte, n)
	for i := 0; i < n; i++ {
		idx, err := rand.Int(rand.Reader, max)
		if err != nil {
			return "", errs.Wrap(errs.CodeGeneric, err, "read from crypto/rand", "the system CSPRNG is unavailable")
		}
		out[i] = secretAlphabet[idx.Int64()]
	}
	return string(out), nil
}
