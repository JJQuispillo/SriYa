#!/usr/bin/env bash
# install.sh — sriyactl installer shim
#
# Lives in billing/cli/ (monorepo). Binary downloads still come from the
# sriyactl GitHub Releases page (goreleaser publishes there).
# Detects OS + arch, prefers Homebrew (macOS/Linux), falls back to
# downloading the pinned binary + sha256 checksum from GitHub Releases.
# On success it exec's `sriyactl infra install` so a one-liner works:
#
#   curl -fsSL https://raw.githubusercontent.com/JJQuispillo/billing/main/cli/install.sh | bash
#
# Unsupported OS/arch → non-zero exit with a clear message.

set -euo pipefail

SRIYACTL_REPO="JJQuispillo/sriyactl"
SRIYACTL_VERSION="${SRIYACTL_VERSION:-}"

warn()  { echo >&2 "==> $*"; }
abort() { echo >&2 "ERROR: $*"; exit 1; }

# ---- OS / arch detection ----
detect_os() {
  local uname
  uname="$(uname -s | tr '[:upper:]' '[:lower:]')"
  case "$uname" in
    darwin)  echo "darwin"  ;;
    linux)   echo "linux"   ;;
    *)       abort "unsupported OS: $uname (only darwin/linux)" ;;
  esac
}

detect_arch() {
  local uname
  uname="$(uname -m)"
  case "$uname" in
    x86_64|amd64) echo "amd64" ;;
    arm64|aarch64) echo "arm64" ;;
    *) abort "unsupported architecture: $uname (only amd64/arm64)" ;;
  esac
}

# ---- Homebrew path ----
try_brew() {
  if command -v brew >/dev/null 2>&1; then
    warn "Installing via Homebrew…"
    brew install "$SRIYACTL_REPO/tap/sriyactl"
    return 0
  fi
  return 1
}

# ---- Binary download ----
resolve_version() {
  if [ -n "$SRIYACTL_VERSION" ]; then
    echo "v${SRIYACTL_VERSION#v}"
    return
  fi
  # Fetch latest release tag from GitHub API (no auth required for public repos).
  local tag
  tag="$(curl -fsSL "https://api.github.com/repos/$SRIYACTL_REPO/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p')"
  if [ -z "$tag" ]; then
    abort "could not determine latest version from GitHub API; set SRIYACTL_VERSION=vX.Y.Z"
  fi
  echo "$tag"
}

download_binary() {
  local os="$1" arch="$2" version="$3"
  local archive="sriyactl_${version#v}_${os}_${arch}.tar.gz"
  local url="https://github.com/$SRIYACTL_REPO/releases/download/$version/$archive"
  local checksum_url="https://github.com/$SRIYACTL_REPO/releases/download/$version/checksums.txt"

  local tmpdir
  tmpdir="$(mktemp -d)"
  trap "rm -rf '$tmpdir'" EXIT

  warn "Downloading $archive…"
  curl -fsSL "$url" -o "$tmpdir/$archive"

  warn "Verifying checksum…"
  curl -fsSL "$checksum_url" -o "$tmpdir/checksums.txt"
  (cd "$tmpdir" && shasum -a 256 -c --ignore-missing < checksums.txt) || abort "checksum mismatch — download may be corrupted"

  warn "Extracting…"
  tar -xzf "$tmpdir/$archive" -C "$tmpdir"

  local bin="$tmpdir/sriyactl"
  if [ ! -f "$bin" ]; then
    abort "binary not found inside the archive"
  fi
  chmod +x "$bin"
  mv "$bin" /usr/local/bin/sriyactl

  rm -rf "$tmpdir"
  trap - EXIT
  warn "Installed sriyactl to /usr/local/bin/sriyactl"
}

# ---- Main ----
main() {
  local os arch
  os="$(detect_os)"
  arch="$(detect_arch)"

  echo "==> sriyactl installer — $os/$arch"

  if command -v sriyactl >/dev/null 2>&1; then
    warn "sriyactl is already installed at $(command -v sriyactl)"
    warn "Run \`sriyactl infra install\` to provision the stack,"
    warn "or \`brew upgrade JJQuispillo/tap/sriyactl\` / download a new binary to update."
  fi

  # Prefer Homebrew on macOS or Linux with brew.
  if [ "$os" = "darwin" ] || command -v brew >/dev/null 2>&1; then
    try_brew && exec sriyactl infra install "$@"
  fi

  # Fallback: download binary.
  local version
  version="$(resolve_version)"
  download_binary "$os" "$arch" "$version"

  exec sriyactl infra install "$@"
}

main "$@"
