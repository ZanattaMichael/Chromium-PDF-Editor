#!/usr/bin/env bash
# Builds one all-in-one bundle for a platform: the browser extension AND the matching
# self-contained native host together in a single zip, plus the install scripts. A user
# downloads one file, unzips it, loads extension/ unpacked, and runs the install script
# (which auto-detects the bundled host/). No .NET SDK is needed to use it.
#
# Layout inside pdf-editor-bundle-<rid>.zip:
#   extension/   the MV3 extension (manifest.json at its root)
#   host/        the self-contained native host for <rid> (runtime + native libs bundled)
#   scripts/     install-host.sh / install-host.ps1 / com.pdfeditor.host.json.template
#   INSTALL.md   three-step setup
#
# Usage: ./scripts/package-bundle.sh <runtime-id> [output-dir]
set -euo pipefail

RID="${1:?usage: package-bundle.sh <runtime-id> [output-dir]  (win-x64, linux-x64, osx-x64, osx-arm64)}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${2:-$REPO_ROOT/dist}"

mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"

VERSION=$(python3 -c "import json; print(json.load(open('$REPO_ROOT/extension/manifest.json'))['version'])")
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
BUNDLE="$STAGE/bundle"

# 1) Extension — reuse package-extension.sh so contents and exclusions stay identical, then
#    unzip its output into the bundle's extension/ folder. GITHUB_OUTPUT is cleared for the
#    nested call so it doesn't clobber this script's own step outputs.
echo "Staging extension v$VERSION..."
mkdir -p "$STAGE/_ext" "$BUNDLE/extension"
GITHUB_OUTPUT="" "$REPO_ROOT/scripts/package-extension.sh" "$STAGE/_ext" >/dev/null
unzip -q "$STAGE/_ext/pdf-editor-extension-v${VERSION}.zip" -d "$BUNDLE/extension"

# 2) Native host — self-contained publish for this platform.
echo "Publishing native host ($RID, self-contained)..."
dotnet publish "$REPO_ROOT/src/PdfEditor.NativeHost" \
  --configuration Release --runtime "$RID" --self-contained true \
  -p:PublishSingleFile=false --output "$BUNDLE/host" --nologo -v q

# 3) Install scripts + native-messaging manifest template.
mkdir -p "$BUNDLE/scripts"
cp "$REPO_ROOT/scripts/install-host.sh" \
   "$REPO_ROOT/scripts/install-host.ps1" \
   "$REPO_ROOT/scripts/com.pdfeditor.host.json.template" \
   "$BUNDLE/scripts/"

# 4) Short install guide.
cat > "$BUNDLE/INSTALL.md" <<EOF
# PDF Editor ($RID) — install

This bundle contains the browser extension and the matching native host, so no .NET SDK
is required.

1. **Load the extension**: open \`chrome://extensions\`, enable *Developer mode*, click
   *Load unpacked*, and select the \`extension/\` folder. Note the extension ID it shows.
2. **Register the native host** (auto-detects the bundled \`host/\`):
   - Linux / macOS: \`./scripts/install-host.sh <extension-id>\`
   - Windows (PowerShell): \`.\\scripts\\install-host.ps1 -ExtensionId <extension-id>\`
3. **Restart your browser.** The extension's options page shows the host connection status.
EOF

ZIP_PATH="$OUTPUT_DIR/pdf-editor-bundle-$RID.zip"
rm -f "$ZIP_PATH"
(
  cd "$BUNDLE"
  zip -X -r -q "$ZIP_PATH" .
)

echo "Packaged: $ZIP_PATH ($(du -h "$ZIP_PATH" | cut -f1))"

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "zip_path=${ZIP_PATH}" >> "$GITHUB_OUTPUT"
  echo "version=${VERSION}" >> "$GITHUB_OUTPUT"
fi
