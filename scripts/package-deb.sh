#!/usr/bin/env bash
# Builds a Debian package (.deb) for the PDF Editor native messaging host (linux-x64).
#
# The package installs the self-contained host under /opt/pdf-editor-host, exposes it on PATH as
# /usr/bin/pdf-editor-host, and registers it system-wide with Chrome/Chromium by shipping the
# native-messaging manifest as a package-owned file — so `apt remove` cleans it up automatically.
# The manifest's allowed_origins is pinned to the Chrome and Edge extension IDs (from
# $CHROME_EXTENSION_ID/$EDGE_EXTENSION_ID, else scripts/extension-id.txt/edge-extension-id.txt).
#
# Three things decide whether the browser actually finds the host, and all three are handled here:
#   1. the manifest has to sit in the directory that browser build reads (see linux-manifest-dirs.sh);
#   2. the "path" in it has to be executable by the browser — /usr/bin is far more likely to be
#      permitted by a sandboxed browser than /opt, so the manifest points at the symlink;
#   3. the host has to actually start. A self-contained .NET build still needs ICU/OpenSSL from
#      the system; when they are missing it exits immediately and the browser reports a host that
#      "has exited", which is indistinguishable from one that was never installed. The postinst
#      runs the host once and says so plainly instead of leaving it to be discovered in a browser.
#
# Usage: ./scripts/package-deb.sh [output-dir]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=scripts/linux-manifest-dirs.sh
source "$REPO_ROOT/scripts/linux-manifest-dirs.sh"
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

EDGE_ID="${EDGE_EXTENSION_ID:-}"
if [[ -z "$EDGE_ID" && -f "$REPO_ROOT/scripts/edge-extension-id.txt" ]]; then
  EDGE_ID="$(tr -d '[:space:]' < "$REPO_ROOT/scripts/edge-extension-id.txt")"
fi
if [[ -z "$EDGE_ID" ]]; then
  echo "error: no Edge extension ID (set EDGE_EXTENSION_ID or scripts/edge-extension-id.txt)" >&2
  exit 1
fi

HOST_NAME="com.pdfeditor.host"
INSTALL_DIR="/opt/pdf-editor-host"
EXE="PdfEditor.NativeHost"
# The path browsers are told to launch. A symlink onto $INSTALL_DIR/$EXE: the .NET apphost resolves
# its own directory through /proc/self/exe, which follows the link, so it still finds its runtime.
LAUNCH_PATH="/usr/bin/pdf-editor-host"
REGISTER_PATH="/usr/bin/pdf-editor-host-register"
SHARE_DIR="/usr/share/pdf-editor-host"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
DEBROOT="$STAGE/deb"

echo "Publishing native host (linux-x64, self-contained)..."
dotnet publish "$REPO_ROOT/src/PdfEditor.NativeHost" \
  --configuration Release --runtime linux-x64 --self-contained true \
  -p:PublishSingleFile=false --output "$DEBROOT$INSTALL_DIR" --nologo -v q

# dotnet publish leaves the payload with whatever the build umask produced; a file the browser's
# user cannot read is a host that cannot start. Normalise the whole tree.
find "$DEBROOT$INSTALL_DIR" -type d -exec chmod 0755 {} +
find "$DEBROOT$INSTALL_DIR" -type f -exec chmod 0644 {} +
chmod 0755 "$DEBROOT$INSTALL_DIR/$EXE"
# The apphost is not the only executable in a self-contained publish: single-file extraction and
# crash dumping both shell out to sibling binaries.
find "$DEBROOT$INSTALL_DIR" -type f \( -name '*.so' -o -name 'createdump' \) -exec chmod 0755 {} +

# Reachable on PATH, and the path the manifests point at.
mkdir -p "$DEBROOT/usr/bin"
ln -sf "$INSTALL_DIR/$EXE" "$DEBROOT$LAUNCH_PATH"

# Per-user registration helper — the only way to reach snap/flatpak browsers, and the escape hatch
# for a developer-mode extension whose ID differs from the pinned one.
install -Dm0755 "$REPO_ROOT/scripts/register-host.sh" "$DEBROOT$REGISTER_PATH"
# Record the IDs this package was built with, so the helper defaults to the same ones.
install -d -m0755 "$DEBROOT$SHARE_DIR"
printf '%s\n' "$EXTENSION_ID" > "$DEBROOT$SHARE_DIR/extension-id"
chmod 0644 "$DEBROOT$SHARE_DIR/extension-id"
printf '%s\n' "$EDGE_ID" > "$DEBROOT$SHARE_DIR/edge-extension-id"
chmod 0644 "$DEBROOT$SHARE_DIR/edge-extension-id"

# Native-messaging manifest, pinned to both extension IDs and pointing at the launcher symlink.
render_manifest() {
  sed -e "s|__HOST_PATH__|$LAUNCH_PATH|" -e "s|__EXTENSION_ID__|$EXTENSION_ID|" \
    -e "s|__EDGE_EXTENSION_ID__|$EDGE_ID|" \
    "$REPO_ROOT/scripts/com.pdfeditor.host.json.template"
}
# System-wide manifest dirs for the common Chromium-based browsers. Each browser reads only its
# own dir and ignores the rest, so shipping to all of them is safe and means the host works no
# matter which browser (and which channel) is installed.
for dir in "${LINUX_MANIFEST_DIRS[@]}"; do
  mkdir -p "$DEBROOT/$dir"
  render_manifest > "$DEBROOT/$dir/$HOST_NAME.json"
  chmod 0644 "$DEBROOT/$dir/$HOST_NAME.json"
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
Depends: libc6 (>= 2.27), libgcc-s1 | libgcc1, libstdc++6, zlib1g
Recommends: libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66 | libicu63, libssl3t64 | libssl3 | libssl1.1
Description: PDF Editor native messaging host
 Local backend for the PDF Editor browser extension. Performs PDF processing
 (edit, redact, merge, sign, OCR) on your machine and is registered as a Chrome/
 Chromium native messaging host so the extension can talk to it.
 .
 The ICU and OpenSSL runtime libraries are listed as Recommends rather than
 Depends because their package names change with every Debian/Ubuntu release;
 the host needs them, and the post-install check reports it if they are absent.
EOF

# Post-install: prove the host can actually run, and point at the per-user helper for the two
# cases a system-wide manifest cannot cover (sandboxed browsers, developer-mode extension IDs).
cat > "$DEBROOT/DEBIAN/postinst" <<POSTINST
#!/bin/sh
set -e

if [ "\$1" != "configure" ]; then
  exit 0
fi

# dpkg preserves the packaged modes, but a host that is not executable is the one failure that
# produces no diagnostic at all in the browser, so make sure of it.
chmod 0755 "$INSTALL_DIR/$EXE" 2>/dev/null || true

echo "PDF Editor native messaging host installed."
echo "  host:     $LAUNCH_PATH -> $INSTALL_DIR/$EXE"
echo "  manifest: $HOST_NAME.json, registered for Chrome, Chromium, Edge, Brave, Vivaldi and Opera"
echo "  allowed:  chrome-extension://$EXTENSION_ID/"
echo "            chrome-extension://$EDGE_ID/"

if "$LAUNCH_PATH" --version >/dev/null 2>&1; then
  echo "  self-test: OK"
else
  echo "" >&2
  echo "WARNING: the host is installed but did not start." >&2
  echo "  Run it directly to see the error:" >&2
  echo "      $LAUNCH_PATH --diagnostics" >&2
  echo "  A self-contained .NET host needs the system ICU and OpenSSL libraries. On Debian/Ubuntu" >&2
  echo "  the runtime package name carries the ICU version, so the simplest way to get whichever" >&2
  echo "  one this release ships is:" >&2
  echo "      sudo apt-get install -y libicu-dev libssl3 || sudo apt-get install -y libicu-dev libssl1.1" >&2
  echo "  Until it starts, the browser will report the host as unavailable." >&2
fi

echo ""
echo "Restart your browser to pick the host up."
echo "Using a snap/flatpak browser, or a developer-mode (unpacked) extension? The system-wide"
echo "manifest cannot reach either. Register per-user instead:"
echo "    $(basename "$REGISTER_PATH") [--extension-id <your-extension-id>]"

exit 0
POSTINST
chmod 0755 "$DEBROOT/DEBIAN/postinst"

# Post-removal: the per-user manifests live in other people's home directories, which a package
# script must not touch. Say how to clear them instead.
cat > "$DEBROOT/DEBIAN/postrm" <<POSTRM
#!/bin/sh
set -e

if [ "\$1" = "purge" ]; then
  rm -rf "$SHARE_DIR"
  echo "Removed the PDF Editor native messaging host."
  echo "Per-user manifests, if any were registered, are left in place — clear them with:"
  echo "    $(basename "$REGISTER_PATH") --uninstall"
fi

exit 0
POSTRM
chmod 0755 "$DEBROOT/DEBIAN/postrm"

DEB_PATH="$OUTPUT_DIR/pdf-editor-host_${VERSION}_amd64.deb"
echo "Building $DEB_PATH ..."
# --root-owner-group: files owned by root:root without needing fakeroot.
dpkg-deb --root-owner-group --build "$DEBROOT" "$DEB_PATH" >/dev/null

echo
echo "Built: $DEB_PATH ($(du -h "$DEB_PATH" | cut -f1))"
echo "Extension ID pinned:      $EXTENSION_ID"
echo "Edge extension ID pinned: $EDGE_ID"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "deb_path=$DEB_PATH" >> "$GITHUB_OUTPUT"
fi
