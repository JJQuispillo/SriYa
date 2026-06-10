# billing — root Makefile
# Delegates Go targets to cli/, .NET targets use dotnet CLI directly.

GO ?= go
DOTNET ?= dotnet

.PHONY: go-build
go-build:
	$(MAKE) -C cli build

.PHONY: go-test
go-test:
	$(MAKE) -C cli test

.PHONY: go-vet
go-vet:
	$(MAKE) -C cli vet

.PHONY: go-check
go-check:
	$(MAKE) -C cli check

.PHONY: dotnet-build
dotnet-build:
	$(DOTNET) build -c Release

.PHONY: dotnet-test
dotnet-test:
	$(DOTNET) test --configuration Release

.PHONY: dotnet-format
dotnet-format:
	$(DOTNET) format --verify-no-changes --verbosity diagnostic

.PHONY: help
help:
	@echo "Targets:"
	@echo "  go-build     - build the CLI binary"
	@echo "  go-test      - run CLI tests"
	@echo "  go-vet       - run go vet on CLI"
	@echo "  go-check     - vet + test + build for CLI"
	@echo "  dotnet-build - build the .NET API"
	@echo "  dotnet-test  - run .NET tests"
	@echo "  dotnet-format- check .NET formatting"
