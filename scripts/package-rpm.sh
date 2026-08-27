#!/usr/bin/env bash
# Builds an RPM package (.rpm) for the PDF Editor native messaging host (linux-x64), for
# RedHat-family distros (Fedora, RHEL, CentOS, openSUSE, etc.).
#
# Like the .deb, it installs the self-contained host under /opt/pdf-editor-host, exposes it on
# PATH as /usr/bin/pdf-editor-host, and registers it system-wide with Chrome/Chromium by shipping
# the native-messaging manifest as a package-owned file (marked %config so `dnf remove` cleans it
# up). The manifest points at the /usr/bin launcher rather than at /opt, because a sandboxed
# browser is far more likely to be permitted to execute it there. allowed_origins is pinned to the
# extension ID (from $CHROME_EXTENSION_ID, else scripts/extension-id.txt).
#
# It also ships /usr/bin/pdf-editor-host-register, the per-user helper for the two cases no
# system-wide manifest can reach: snap/flatpak browsers, and a developer-mode extension whose ID
# differs from the pinned one.
#
# rpmbuild runs fine on Debian/Ubuntu (`apt-get install rpm`), so this builds on the same
# ubuntu runner as the .deb -- no RedHat host required.
#
# Usage: ./scripts/package-rpm.sh [output-dir]
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

# RPM forbids a dash in the Version field; the RC build stamps a 4-part version like
# 2.0.1.57 which is fine, but a dash-bearing tag would break it -- swap any dash for an
# underscore defensively.
RPM_VERSION="${VERSION//-/_}"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
BUILDROOT="$STAGE/buildroot"

echo "Publishing native host (linux-x64, self-contained)..."
dotnet publish "$REPO_ROOT/src/PdfEditor.NativeHost" \
  --configuration Release --runtime linux-x64 --self-contained true \
  -p:PublishSingleFile=false --output "$BUILDROOT$INSTALL_DIR" --nologo -v q

# dotnet publish leaves the payload with whatever the build umask produced; a file the browser's
# user cannot read is a host that cannot start. Normalise the whole tree.
find "$BUILDROOT$INSTALL_DIR" -type d -exec chmod 0755 {} +
find "$BUILDROOT$INSTALL_DIR" -type f -exec chmod 0644 {} +
chmod 0755 "$BUILDROOT$INSTALL_DIR/$EXE"
find "$BUILDROOT$INSTALL_DIR" -type f \( -name '*.so' -o -name 'createdump' \) -exec chmod 0755 {} +

# Reachable on PATH, and the path the manifests point at.
mkdir -p "$BUILDROOT/usr/bin"
ln -sf "$INSTALL_DIR/$EXE" "$BUILDROOT$LAUNCH_PATH"

# Per-user registration helper, plus the extension ID this package was built with so the helper
# defaults to the same one.
install -Dm0755 "$REPO_ROOT/scripts/register-host.sh" "$BUILDROOT$REGISTER_PATH"
install -d -m0755 "$BUILDROOT$SHARE_DIR"
printf '%s\n' "$EXTENSION_ID" > "$BUILDROOT$SHARE_DIR/extension-id"
chmod 0644 "$BUILDROOT$SHARE_DIR/extension-id"

# Native-messaging manifest, pinned to the extension ID and pointing at the launcher symlink.
render_manifest() {
  sed -e "s|__HOST_PATH__|$LAUNCH_PATH|" -e "s|__EXTENSION_ID__|$EXTENSION_ID|" \
    "$REPO_ROOT/scripts/com.pdfeditor.host.json.template"
}
# Register for every common Chromium-based browser (see linux-manifest-dirs.sh). Each %config
# line for the spec's %files section is accumulated here so `dnf remove` reclaims them all.
CONFIG_FILES=""
for dir in "${LINUX_MANIFEST_DIRS[@]}"; do
  mkdir -p "$BUILDROOT/$dir"
  render_manifest > "$BUILDROOT/$dir/$HOST_NAME.json"
  CONFIG_FILES+="%config /$dir/$HOST_NAME.json"$'\n'
done

# RPM spec. %install copies the pre-staged buildroot into rpmbuild's own buildroot; there is
# no compile step (the payload is already published).
SPEC="$STAGE/pdf-editor-host.spec"
cat > "$SPEC" <<EOF
Name:           pdf-editor-host
Version:        $RPM_VERSION
Release:        1
Summary:        PDF Editor native messaging host
License:        MIT
BuildArch:      x86_64
AutoReqProv:    no

%description
Local backend for the PDF Editor browser extension. Performs PDF processing
(edit, redact, merge, sign, OCR) on your machine and is registered as a Chrome/
Chromium native messaging host so the extension can talk to it.

%install
cp -a %{_sourcedir}/buildroot/. %{buildroot}/

%post
# Prove the host can actually run. A self-contained .NET build still needs the system ICU and
# OpenSSL libraries; without them it exits immediately and the browser reports a host that "has
# exited" — indistinguishable, from the extension's side, from one that was never installed.
echo "PDF Editor native messaging host installed."
echo "  host:     $LAUNCH_PATH -> $INSTALL_DIR/$EXE"
echo "  allowed:  chrome-extension://$EXTENSION_ID/"
if "$LAUNCH_PATH" --version >/dev/null 2>&1; then
  echo "  self-test: OK"
else
  echo "WARNING: the host is installed but did not start. Run '$LAUNCH_PATH --diagnostics' to see why." >&2
  echo "  A self-contained .NET host needs libicu and openssl-libs: sudo dnf install libicu openssl-libs" >&2
fi
echo ""
echo "Restart your browser to pick the host up."
echo "Using a snap/flatpak browser, or a developer-mode (unpacked) extension? Neither can see the"
echo "system-wide manifest — register per-user instead:"
echo "    pdf-editor-host-register [--extension-id <your-extension-id>]"

%files
$INSTALL_DIR
$LAUNCH_PATH
$REGISTER_PATH
$SHARE_DIR
$CONFIG_FILES

%changelog
* $(LC_ALL=C date '+%a %b %d %Y') PDF Editor <noreply@users.noreply.github.com> - $RPM_VERSION-1
- Automated release-candidate build.
EOF

echo "Building RPM ..."
# _sourcedir points at our stage so %install can copy the pre-published buildroot; disabling
# the debuginfo/strip/compress machinery keeps the self-contained .NET payload intact.
rpmbuild -bb "$SPEC" \
  --define "_topdir $STAGE/rpmbuild" \
  --define "_sourcedir $STAGE" \
  --define "_rpmdir $STAGE/rpmbuild/RPMS" \
  --define "debug_package %{nil}" \
  --define "__brp_strip %{nil}" \
  --define "__brp_strip_static_archive %{nil}" \
  --define "__brp_strip_comment_note %{nil}" \
  --define "__os_install_post %{nil}" \
  >/dev/null

BUILT=$(find "$STAGE/rpmbuild/RPMS" -name '*.rpm' -print -quit)
RPM_PATH="$OUTPUT_DIR/pdf-editor-host-${RPM_VERSION}-1.x86_64.rpm"
mv "$BUILT" "$RPM_PATH"

echo
echo "Built: $RPM_PATH ($(du -h "$RPM_PATH" | cut -f1))"
echo "Extension ID pinned: $EXTENSION_ID"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "rpm_path=$RPM_PATH" >> "$GITHUB_OUTPUT"
fi
