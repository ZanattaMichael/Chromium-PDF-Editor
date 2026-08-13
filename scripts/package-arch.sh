#!/usr/bin/env bash
# Builds an Arch Linux package (.pkg.tar.zst) for the PDF Editor native messaging host
# (linux-x64), installable with `pacman -U`.
#
# Like the .deb/.rpm, it installs the self-contained host under /opt/pdf-editor-host and
# registers it system-wide with Chrome/Chromium by shipping the native-messaging manifest as
# a package-owned file (`pacman -R` cleans it up). The manifest's allowed_origins is pinned to
# the extension ID (from $CHROME_EXTENSION_ID, else scripts/extension-id.txt).
#
# Arch's own makepkg only runs on Arch, so this assembles the package format by hand with
# bsdtar (libarchive-tools) + zstd -- both available on the ubuntu runner -- producing the
# same .PKGINFO + .MTREE + payload layout makepkg emits. No Arch host required.
#
# Usage: ./scripts/package-arch.sh [output-dir]
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

HOST_NAME="com.pdfeditor.host"
INSTALL_DIR="/opt/pdf-editor-host"
EXE="PdfEditor.NativeHost"

# pacman versions forbid a dash outside the pkgrel separator; the RC stamps 4-part dotted
# versions (2.0.1.57) which are fine -- swap any stray dash for an underscore defensively.
PKG_VERSION="${VERSION//-/_}"
PKG_REL="1"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
PKGDIR="$STAGE/pkg"

echo "Publishing native host (linux-x64, self-contained)..."
dotnet publish "$REPO_ROOT/src/PdfEditor.NativeHost" \
  --configuration Release --runtime linux-x64 --self-contained true \
  -p:PublishSingleFile=false --output "$PKGDIR$INSTALL_DIR" --nologo -v q

chmod 0755 "$PKGDIR$INSTALL_DIR/$EXE"

# Native-messaging manifest, pinned to the extension ID and pointing at the installed binary.
render_manifest() {
  sed -e "s|__HOST_PATH__|$INSTALL_DIR/$EXE|" -e "s|__EXTENSION_ID__|$EXTENSION_ID|" \
    "$REPO_ROOT/scripts/com.pdfeditor.host.json.template"
}
# Register for every common Chromium-based browser (see linux-manifest-dirs.sh). Each manifest is
# marked as a pacman backup file (accumulated in BACKUP_LINES) so an upgrade won't clobber edits.
BACKUP_LINES=""
for dir in "${LINUX_MANIFEST_DIRS[@]}"; do
  mkdir -p "$PKGDIR/$dir"
  render_manifest > "$PKGDIR/$dir/$HOST_NAME.json"
  BACKUP_LINES+="backup = $dir/$HOST_NAME.json"$'\n'
done

# .PKGINFO -- package metadata pacman reads. size is the installed byte total.
INSTALLED_SIZE=$(du -sb "$PKGDIR" | cut -f1)
BUILD_DATE=$(date +%s)
cat > "$PKGDIR/.PKGINFO" <<EOF
pkgname = pdf-editor-host
pkgbase = pdf-editor-host
pkgver = ${PKG_VERSION}-${PKG_REL}
pkgdesc = PDF Editor native messaging host
url = https://github.com/ZanattaMichael/Chromium-PDF-Editor
builddate = $BUILD_DATE
packager = PDF Editor <noreply@users.noreply.github.com>
size = $INSTALLED_SIZE
arch = x86_64
license = MIT
EOF

# Mark every registered manifest as a pacman backup file so an upgrade doesn't clobber local edits.
printf '%s' "$BACKUP_LINES" >> "$PKGDIR/.PKGINFO"

# .MTREE -- a gzip-compressed mtree manifest of every packaged file (makepkg's exact options),
# listing .PKGINFO and the payload tree but not .MTREE itself.
(
  cd "$PKGDIR"
  LANG=C bsdtar -czf .MTREE --format=mtree \
    --options='!all,use-set,type,uid,gid,mode,time,size,md5,sha256,link' \
    .PKGINFO opt etc
)

PKG_PATH="$OUTPUT_DIR/pdf-editor-host-${PKG_VERSION}-${PKG_REL}-x86_64.pkg.tar.zst"
echo "Building $PKG_PATH ..."
# .PKGINFO and .MTREE must be the first entries; then the payload tree.
(
  cd "$PKGDIR"
  LANG=C bsdtar --zstd -cf "$PKG_PATH" .PKGINFO .MTREE opt etc
)

echo
echo "Built: $PKG_PATH ($(du -h "$PKG_PATH" | cut -f1))"
echo "Extension ID pinned: $EXTENSION_ID"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "arch_path=$PKG_PATH" >> "$GITHUB_OUTPUT"
fi
