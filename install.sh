#!/usr/bin/env sh
# Installs the latest axiom-cli release for Linux or macOS.
# Usage: curl -fsSL https://raw.githubusercontent.com/YoMosa2009/Axiom-CLI/main/install.sh | sh
set -eu

REPO="YoMosa2009/Axiom-CLI"
INSTALL_DIR="${AXIOM_INSTALL_DIR:-$HOME/.local/bin}"

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
  Linux) platform="linux" ;;
  Darwin) platform="osx" ;;
  *) echo "Unsupported OS: $os" >&2; exit 1 ;;
esac

case "$arch" in
  x86_64|amd64) rid_arch="x64" ;;
  arm64|aarch64) rid_arch="arm64" ;;
  *) echo "Unsupported architecture: $arch" >&2; exit 1 ;;
esac

asset="axiom-cli-${platform}-${rid_arch}.tar.gz"
api_url="https://api.github.com/repos/$REPO/releases/latest"

echo "Detecting latest release..."
download_url="$(curl -fsSL "$api_url" | grep "browser_download_url.*$asset" | sed -E 's/.*"(https[^"]+)".*/\1/')"

if [ -z "$download_url" ]; then
  echo "Could not find a release asset named '$asset'. See https://github.com/$REPO/releases" >&2
  exit 1
fi

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

echo "Downloading $asset..."
curl -fsSL "$download_url" -o "$tmp_dir/$asset"

echo "Extracting..."
tar -xzf "$tmp_dir/$asset" -C "$tmp_dir"

# Locate the binary whether the archive is flat or nested one level.
axiom_bin="$(find "$tmp_dir" -type f -name axiom | head -n 1)"
if [ -z "$axiom_bin" ]; then
  echo "Archive did not contain an 'axiom' binary." >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR"
cp "$axiom_bin" "$INSTALL_DIR/axiom"
chmod +x "$INSTALL_DIR/axiom"

# Copy any native libraries that ship beside the binary (SQLite, etc.).
bin_dir="$(dirname "$axiom_bin")"
find "$bin_dir" -maxdepth 1 \( -name '*.dylib' -o -name '*.so' -o -name '*.so.*' \) 2>/dev/null | while IFS= read -r lib; do
  [ -n "$lib" ] && cp "$lib" "$INSTALL_DIR/" || true
done

echo "Installed axiom to $INSTALL_DIR/axiom"

case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *)
    echo ""
    echo "$INSTALL_DIR is not on your PATH. Add this to your shell profile:"
    echo "  export PATH=\"$INSTALL_DIR:\$PATH\""
    ;;
esac

echo ""
echo "Run 'axiom config' to set your OpenRouter API key, then 'axiom' or 'axiom code \"<task>\"'."
