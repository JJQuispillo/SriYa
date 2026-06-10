package secret

import "sync"

// InMemoryStore is a process-local Store used by tests. It is safe for
// concurrent use. The production code uses KeyringStore.
type InMemoryStore struct {
	mu   sync.RWMutex
	vals map[string]string
}

// NewInMemoryStore returns an empty InMemoryStore.
func NewInMemoryStore() *InMemoryStore { return &InMemoryStore{vals: map[string]string{}} }

// Get implements Store.
func (m *InMemoryStore) Get(key string) (string, error) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	v, ok := m.vals[key]
	if !ok {
		return "", errNotFound(key)
	}
	return v, nil
}

// Set implements Store.
func (m *InMemoryStore) Set(key, val string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.vals[key] = val
	return nil
}

// Delete implements Store.
func (m *InMemoryStore) Delete(key string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.vals, key)
	return nil
}

type notFoundErr struct{ key string }

func (e notFoundErr) Error() string { return "secret not found: " + e.key }
func errNotFound(key string) error  { return notFoundErr{key: key} }
