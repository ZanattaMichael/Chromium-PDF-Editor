#!/usr/bin/env bash
# Packages extension/ into a Chrome Web Store-ready zip: manifest.json sits at the
# zip's root (not nested in a folder), icons are generated first, and only the files
# actually shipped to users are included.
#
# Usage: ./scripts/package-extension.sh [output-dir]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXTENSION_DIR="$REPO_ROOT/extension"
OUTPUT_DIR="${1:-$REPO_ROOT/dist}"

echo "Generating icons..."
python3 "$REPO_ROOT/scripts/generate-icons.py"

echo "Validating manifest.json..."
python3 - "$EXTENSION_DIR/manifest.json" <<'EOF'
import json, sys
path = sys.argv[1]
with open(path) as f:
    manifest = json.load(f)
if manifest.get("manifest_version") != 3:
    sys.exit(f"error: expected manifest_version 3, got {manifest.get('manifest_version')!r}")
version = manifest.get("version")
if not version:
    sys.exit("error: manifest.json has no 'version' field")
print(version)
EOF
VERSION=$(python3 -c "import json; print(json.load(open('$EXTENSION_DIR/manifest.json'))['version'])")

mkdir -p "$OUTPUT_DIR"
# Resolve to an absolute path: the zip is written from inside a subshell that cds
# into $EXTENSION_DIR below, so a relative $OUTPUT_DIR (e.g. the plain "dist" that
# CI passes in) would otherwise be looked up relative to the wrong directory.
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
ZIP_PATH="$OUTPUT_DIR/pdf-editor-extension-v${VERSION}.zip"
rm -f "$ZIP_PATH"

echo "Packaging extension v${VERSION}..."
(
  cd "$EXTENSION_DIR"
  # -X: no extra file attributes (deterministic-ish across platforms).
  # Exclude anything that shouldn't ship: OS cruft, editor swap files, source maps.
  zip -X -r -q "$ZIP_PATH" . \
    -x ".*" \
    -x "*.map" \
    -x "**/.DS_Store"
)

echo "Verifying manifest.json sits at the zip root..."
if ! unzip -l "$ZIP_PATH" | awk '{print $4}' | grep -qx "manifest.json"; then
  echo "error: manifest.json is not at the root of $ZIP_PATH" >&2
  exit 1
fi

echo
echo "Packaged: $ZIP_PATH ($(du -h "$ZIP_PATH" | cut -f1))"

# When run as a GitHub Actions step, expose the version and zip path to later steps.
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "version=${VERSION}" >> "$GITHUB_OUTPUT"
  echo "zip_path=${ZIP_PATH}" >> "$GITHUB_OUTPUT"
fi
