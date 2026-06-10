package compose

import "os"

// writeFileImpl is split out so it can be reused without colliding with
// the test helper's writeFile.
func writeFileImpl(path, content string) error {
	return os.WriteFile(path, []byte(content), 0o600)
}
