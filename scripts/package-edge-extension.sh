#!/usr/bin/env bash
# Repackages an already-built Chrome Web Store zip (see package-extension.sh) into an Edge
# Add-ons-ready one: byte-for-byte identical except manifest.json's "key" is removed.
#
# The committed extension/manifest.json keeps "key" -- it pins the extension's ID to the
# published Chrome Web Store one even when loaded unpacked (see the README's "Browser end-to-end
# tests" section and extension/src/host-install.js), which the package-install e2e suite and the
# native-messaging host packages both depend on. But the Microsoft Edge Add-ons store's own
# validator rejects any package whose manifest.json has a "key" property at all, and Edge itself
# already knows the extension's ID from its own first registration (see docs/EDGE_ADD_ONS_STORE.md)
# -- so the field serves no purpose in an Edge submission and cannot be present in one. Rather than
# strip it from the shared source manifest (which would break the ID-pinning above for everyone),
# only the copy destined for Edge has it removed, at packaging time.
#
# Usage: ./scripts/package-edge-extension.sh <chrome-zip-path> [output-dir]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CHROME_ZIP="${1:?Usage: $0 <chrome-zip-path> [output-dir]}"
OUTPUT_DIR="${2:-$REPO_ROOT/dist}"

if [[ ! -f "$CHROME_ZIP" ]]; then
  echo "error: no such file: $CHROME_ZIP" >&2
  exit 1
fi
CHROME_ZIP="$(cd "$(dirname "$CHROME_ZIP")" && pwd)/$(basename "$CHROME_ZIP")"

mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"

VERSION=$(python3 -c "import json; print(json.load(open('$REPO_ROOT/extension/manifest.json'))['version'])")
EDGE_ZIP_PATH="$OUTPUT_DIR/pdf-editor-extension-edge-v${VERSION}.zip"
rm -f "$EDGE_ZIP_PATH"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "Unpacking $(basename "$CHROME_ZIP")..."
unzip -q "$CHROME_ZIP" -d "$STAGE"

echo "Removing manifest.json's \"key\" (Edge's validator rejects a manifest that has one)..."
python3 - "$STAGE/manifest.json" <<'EOF'
import json
import sys

path = sys.argv[1]
with open(path) as f:
    manifest = json.load(f)
if "key" not in manifest:
    sys.exit(f"error: {path} has no \"key\" to remove -- is this already an Edge package?")
del manifest["key"]
with open(path, "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
EOF

echo "Packaging Edge extension v${VERSION}..."
(
  cd "$STAGE"
  zip -X -r -q "$EDGE_ZIP_PATH" . \
    -x ".*" \
    -x "*.map" \
    -x "**/.DS_Store"
)

echo "Verifying manifest.json sits at the zip root and has no \"key\"..."
if ! unzip -l "$EDGE_ZIP_PATH" | awk '{print $4}' | grep -qx "manifest.json"; then
  echo "error: manifest.json is not at the root of $EDGE_ZIP_PATH" >&2
  exit 1
fi
if unzip -p "$EDGE_ZIP_PATH" manifest.json | python3 -c 'import json,sys; sys.exit(1 if "key" in json.load(sys.stdin) else 0)'; then
  :
else
  echo "error: $EDGE_ZIP_PATH still has manifest.json[\"key\"]" >&2
  exit 1
fi

echo
echo "Packaged: $EDGE_ZIP_PATH ($(du -h "$EDGE_ZIP_PATH" | cut -f1))"

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "version=${VERSION}" >> "$GITHUB_OUTPUT"
  echo "edge_zip_path=${EDGE_ZIP_PATH}" >> "$GITHUB_OUTPUT"
fi
