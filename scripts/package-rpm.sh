#!/usr/bin/env bash
# Builds an RPM package (.rpm) for the PDF Editor native messaging host (linux-x64), for
# RedHat-family distros (Fedora, RHEL, CentOS, openSUSE, etc.).
#
# Like the .deb, it installs the self-contained host under /opt/pdf-editor-host and registers
# it system-wide with Chrome/Chromium by shipping the native-messaging manifest as a
# package-owned file (marked %config so `dnf remove` cleans it up). The manifest's
# allowed_origins is pinned to the extension ID (from $CHROME_EXTENSION_ID, else
# scripts/extension-id.txt).
#
# rpmbuild runs fine on Debian/Ubuntu (`apt-get install rpm`), so this builds on the same
# ubuntu runner as the .deb -- no RedHat host required.
#
# Usage: ./scripts/package-rpm.sh [output-dir]
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

chmod 0755 "$BUILDROOT$INSTALL_DIR/$EXE"

# Native-messaging manifest, pinned to the extension ID and pointing at the installed binary.
# Chrome reads /etc/opt/chrome/... ; Chromium reads /etc/chromium/... -- register both.
render_manifest() {
  sed -e "s|__HOST_PATH__|$INSTALL_DIR/$EXE|" -e "s|__EXTENSION_ID__|$EXTENSION_ID|" \
    "$REPO_ROOT/scripts/com.pdfeditor.host.json.template"
}
for dir in "etc/opt/chrome/native-messaging-hosts" "etc/chromium/native-messaging-hosts"; do
  mkdir -p "$BUILDROOT/$dir"
  render_manifest > "$BUILDROOT/$dir/$HOST_NAME.json"
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

%files
$INSTALL_DIR
%config /etc/opt/chrome/native-messaging-hosts/$HOST_NAME.json
%config /etc/chromium/native-messaging-hosts/$HOST_NAME.json

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
