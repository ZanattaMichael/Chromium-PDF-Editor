#!/usr/bin/env bash
# Publishes the native messaging host as a self-contained build for one runtime identifier
# and zips it for release. "Self-contained" means the .NET runtime and every native
# dependency (SkiaSharp, PDFium) are bundled, so the target machine needs no .NET SDK or
# runtime installed — unlike scripts/install-host.sh, which builds from source locally.
#
# Usage: ./scripts/package-host.sh <runtime-id> [output-dir]
#   e.g. ./scripts/package-host.sh linux-x64 dist
set -euo pipefail

RID="${1:?usage: package-host.sh <runtime-id> [output-dir]  (e.g. win-x64, linux-x64, osx-x64, osx-arm64)}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${2:-$REPO_ROOT/dist}"

mkdir -p "$OUTPUT_DIR"
# Resolve to absolute so the zip path is stable regardless of the caller's directory.
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
PUBLISH_DIR="$OUTPUT_DIR/host-$RID"
rm -rf "$PUBLISH_DIR"

echo "Publishing native host ($RID, self-contained)..."
dotnet publish "$REPO_ROOT/src/PdfEditor.NativeHost" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  --output "$PUBLISH_DIR" \
  --nologo -v q

ZIP_PATH="$OUTPUT_DIR/pdf-editor-host-$RID.zip"
rm -f "$ZIP_PATH"
(
  cd "$PUBLISH_DIR"
  zip -X -r -q "$ZIP_PATH" .
)

echo "Packaged: $ZIP_PATH ($(du -h "$ZIP_PATH" | cut -f1))"

# When run as a GitHub Actions step, expose the zip path to later steps.
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "zip_path=${ZIP_PATH}" >> "$GITHUB_OUTPUT"
fi
