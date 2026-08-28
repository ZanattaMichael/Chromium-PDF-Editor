#!/usr/bin/env bash
# Builds an Arch Linux package (.pkg.tar.zst) for the PDF Editor native messaging host
# (linux-x64), installable with `pacman -U`.
#
# Like the .deb/.rpm, it installs the self-contained host under /opt/pdf-editor-host, exposes it
# on PATH as /usr/bin/pdf-editor-host, and registers it system-wide with Chrome/Chromium by
# shipping the native-messaging manifest as a package-owned file (`pacman -R` cleans it up). The
# manifest points at the /usr/bin launcher rather than at /opt, because a sandboxed browser is far
# more likely to be permitted to execute it there. allowed_origins is pinned to the extension ID
# (from $CHROME_EXTENSION_ID, else scripts/extension-id.txt).
#
# It also ships /usr/bin/pdf-editor-host-register, the per-user helper for the two cases no
# system-wide manifest can reach: snap/flatpak browsers, and a developer-mode extension whose ID
# differs from the pinned one.
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
# The path browsers are told to launch: a symlink onto $INSTALL_DIR/$EXE. The .NET apphost resolves
# its own directory through /proc/self/exe, which follows the link, so it still finds its runtime.
LAUNCH_PATH="/usr/bin/pdf-editor-host"
REGISTER_PATH="/usr/bin/pdf-editor-host-register"
SHARE_DIR="/usr/share/pdf-editor-host"

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

# dotnet publish leaves the payload with whatever the build umask produced; a file the browser's
# user cannot read is a host that cannot start. Normalise the whole tree.
find "$PKGDIR$INSTALL_DIR" -type d -exec chmod 0755 {} +
find "$PKGDIR$INSTALL_DIR" -type f -exec chmod 0644 {} +
chmod 0755 "$PKGDIR$INSTALL_DIR/$EXE"
find "$PKGDIR$INSTALL_DIR" -type f \( -name '*.so' -o -name 'createdump' \) -exec chmod 0755 {} +

# Reachable on PATH, and the path the manifests point at.
mkdir -p "$PKGDIR/usr/bin"
ln -sf "$INSTALL_DIR/$EXE" "$PKGDIR$LAUNCH_PATH"

# Per-user registration helper, plus the extension ID this package was built with so the helper
# defaults to the same one.
install -Dm0755 "$REPO_ROOT/scripts/register-host.sh" "$PKGDIR$REGISTER_PATH"
install -d -m0755 "$PKGDIR$SHARE_DIR"
printf '%s\n' "$EXTENSION_ID" > "$PKGDIR$SHARE_DIR/extension-id"
chmod 0644 "$PKGDIR$SHARE_DIR/extension-id"

# Native-messaging manifest, pinned to the extension ID and pointing at the launcher symlink.
render_manifest() {
  sed -e "s|__HOST_PATH__|$LAUNCH_PATH|" -e "s|__EXTENSION_ID__|$EXTENSION_ID|" \
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

# .INSTALL -- pacman's post-install scriptlet. Proves the host can actually run: a self-contained
# .NET build still needs the system ICU and OpenSSL libraries, and without them it exits at once
# and the browser reports a host that "has exited" -- indistinguishable, from the extension's side,
# from one that was never installed.
cat > "$PKGDIR/.INSTALL" <<INSTALL_SCRIPT
post_install() {
  echo "PDF Editor native messaging host installed."
  echo "  host:     $LAUNCH_PATH -> $INSTALL_DIR/$EXE"
  echo "  allowed:  chrome-extension://$EXTENSION_ID/"
  if "$LAUNCH_PATH" --version >/dev/null 2>&1; then
    echo "  self-test: OK"
  else
    echo "WARNING: the host is installed but did not start. Run '$LAUNCH_PATH --diagnostics' to see why." >&2
    echo "  A self-contained .NET host needs icu and openssl: sudo pacman -S --needed icu openssl" >&2
  fi
  echo ""
  echo "Restart your browser to pick the host up."
  echo "Using a snap/flatpak browser, or a developer-mode (unpacked) extension? Neither can see the"
  echo "system-wide manifest -- register per-user instead:"
  echo "    pdf-editor-host-register [--extension-id <your-extension-id>]"
}

post_upgrade() {
  post_install
}
INSTALL_SCRIPT
chmod 0644 "$PKGDIR/.INSTALL"

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
# listing .PKGINFO, .INSTALL and the payload tree but not .MTREE itself.
(
  cd "$PKGDIR"
  LANG=C bsdtar -czf .MTREE --format=mtree \
    --options='!all,use-set,type,uid,gid,mode,time,size,md5,sha256,link' \
    .PKGINFO .INSTALL opt etc usr
)

PKG_PATH="$OUTPUT_DIR/pdf-editor-host-${PKG_VERSION}-${PKG_REL}-x86_64.pkg.tar.zst"
echo "Building $PKG_PATH ..."
# .PKGINFO, .INSTALL and .MTREE must be the first entries; then the payload tree.
(
  cd "$PKGDIR"
  LANG=C bsdtar --zstd -cf "$PKG_PATH" .PKGINFO .INSTALL .MTREE opt etc usr
)

echo
echo "Built: $PKG_PATH ($(du -h "$PKG_PATH" | cut -f1))"
echo "Extension ID pinned: $EXTENSION_ID"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "arch_path=$PKG_PATH" >> "$GITHUB_OUTPUT"
fi
