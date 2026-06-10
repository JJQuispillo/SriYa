# sriyactl — makefile
# Day-2 ops CLI for the SriYa/Qora self-hosted stack.
# See ../billing/openspec/changes/sriyactl-cli/ for the design artifacts.

GO ?= go
BIN ?= bin/sriyactl

.PHONY: build
build:
	$(GO) build -o $(BIN) ./cmd/sriyactl

.PHONY: test
test:
	$(GO) test ./...

.PHONY: vet
vet:
	$(GO) vet ./...

.PHONY: tidy
tidy:
	$(GO) mod tidy

.PHONY: check
check: vet test build

.PHONY: clean
clean:
	rm -rf bin/

.PHONY: snapshot
snapshot:
	goreleaser release --snapshot --clean

.PHONY: install
install: build
	install -m 0755 $(BIN) /usr/local/bin/sriyactl

.PHONY: help
help:
	@echo "Targets:"
	@echo "  build   - compile the sriyactl binary into bin/"
	@echo "  test    - run go test ./..."
	@echo "  vet     - run go vet ./..."
	@echo "  tidy    - go mod tidy"
	@echo "  check   - vet + test + build"
	@echo "  clean   - remove bin/"
	@echo "  snapshot - goreleaser snapshot build"
	@echo "  install - install the binary to /usr/local/bin"
