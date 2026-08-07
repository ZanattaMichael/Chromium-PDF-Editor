#!/usr/bin/env bash
# Builds a Debian package (.deb) for the PDF Editor native messaging host (linux-x64).
#
# The package installs the self-contained host under /opt/pdf-editor-host and registers it
# system-wide with Chrome/Chromium by shipping the native-messaging manifest as a package-owned
# file — so `apt remove` cleans it up automatically. The manifest's allowed_origins is pinned to
# the extension ID (from $CHROME_EXTENSION_ID, else scripts/extension-id.txt).
#
# Usage: ./scripts/package-deb.sh [output-dir]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${1:-$REPO_ROOT/dist}"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"

VERSION=$(python3 -c "import json; print(json.load(open('$REPO_ROOT/extension/manifest.json'))['version'])")

EXTENSION_ID="${CHROME_EXTENSION_ID:-}"
if [[ -z "$EXTENSION_ID" && -f "$REPO_ROOT/scripts/extension-id.txt" ]]; then
  EXTENSION_ID="$(tr -d '[:space:]' < "$REPO_ROOT/scripts/extension-id.txt")"
fi
if [[ -z "$EXTENSION_ID" ]]; then
  echo "error: no extension ID (set CHROME_EXTENSION_ID or scripts/extension-id.txt)" >&2
  exit 1
fi

HOST_NAME="com.pdfeditor.host"
INSTALL_DIR="/opt/pdf-editor-host"
EXE="PdfEditor.NativeHost"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
DEBROOT="$STAGE/deb"

echo "Publishing native host (linux-x64, self-contained)..."
dotnet publish "$REPO_ROOT/src/PdfEditor.NativeHost" \
  --configuration Release --runtime linux-x64 --self-contained true \
  -p:PublishSingleFile=false --output "$DEBROOT$INSTALL_DIR" --nologo -v q

chmod 0755 "$DEBROOT$INSTALL_DIR/$EXE"

# Native-messaging manifest, pinned to the extension ID and pointing at the installed binary.
# Chrome reads /etc/opt/chrome/... ; Chromium reads /etc/chromium/... — register both.
render_manifest() {
  sed -e "s|__HOST_PATH__|$INSTALL_DIR/$EXE|" -e "s|__EXTENSION_ID__|$EXTENSION_ID|" \
    "$REPO_ROOT/scripts/com.pdfeditor.host.json.template"
}
for dir in "etc/opt/chrome/native-messaging-hosts" "etc/chromium/native-messaging-hosts"; do
  mkdir -p "$DEBROOT/$dir"
  render_manifest > "$DEBROOT/$dir/$HOST_NAME.json"
done

# Debian control metadata.
INSTALLED_KB=$(du -sk "$DEBROOT$INSTALL_DIR" | cut -f1)
mkdir -p "$DEBROOT/DEBIAN"
cat > "$DEBROOT/DEBIAN/control" <<EOF
Package: pdf-editor-host
Version: $VERSION
Section: utils
Priority: optional
Architecture: amd64
Maintainer: PDF Editor <noreply@users.noreply.github.com>
Installed-Size: $INSTALLED_KB
Description: PDF Editor native messaging host
 Local backend for the PDF Editor browser extension. Performs PDF processing
 (edit, redact, merge, sign, OCR) on your machine and is registered as a Chrome/
 Chromium native messaging host so the extension can talk to it.
EOF

DEB_PATH="$OUTPUT_DIR/pdf-editor-host_${VERSION}_amd64.deb"
echo "Building $DEB_PATH ..."
# --root-owner-group: files owned by root:root without needing fakeroot.
dpkg-deb --root-owner-group --build "$DEBROOT" "$DEB_PATH" >/dev/null

echo
echo "Built: $DEB_PATH ($(du -h "$DEB_PATH" | cut -f1))"
echo "Extension ID pinned: $EXTENSION_ID"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "deb_path=$DEB_PATH" >> "$GITHUB_OUTPUT"
fi
