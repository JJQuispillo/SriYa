package api

import "os"

// writeFile is a small helper used across the api test suite. We use
// this consistently (instead of os.WriteFile) so the tests can be
// rebuilt in a v2 with a different backing store (memory FS) without
// rewriting every call site.
func writeFile(path, content string) error {
	return os.WriteFile(path, []byte(content), 0o600)
}
