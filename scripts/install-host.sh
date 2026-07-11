#!/usr/bin/env bash
# Publishes the native host and registers it with Chromium-based browsers.
# Usage: ./scripts/install-host.sh <extension-id> [--configuration Release]
set -euo pipefail

EXTENSION_ID="${1:-}"
CONFIGURATION="${3:-Release}"
if [[ -z "$EXTENSION_ID" ]]; then
  echo "Usage: $0 <extension-id> [--configuration Release]" >&2
  echo "Find the ID at chrome://extensions with Developer mode enabled." >&2
  exit 1
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="$HOME/.local/share/pdf-editor-host"
HOST_NAME="com.pdfeditor.host"

echo "Publishing native host ($CONFIGURATION)..."
dotnet publish "$REPO_ROOT/src/PdfEditor.NativeHost" -c "$CONFIGURATION" -o "$PUBLISH_DIR" --nologo -v q

LAUNCHER="$PUBLISH_DIR/pdf-editor-host.sh"
cat > "$LAUNCHER" <<LAUNCH
#!/usr/bin/env bash
exec dotnet "$PUBLISH_DIR/PdfEditor.NativeHost.dll" "\$@"
LAUNCH
chmod +x "$LAUNCHER"

MANIFEST_JSON=$(sed -e "s|__HOST_PATH__|$LAUNCHER|" -e "s|__EXTENSION_ID__|$EXTENSION_ID|" \
  "$REPO_ROOT/scripts/com.pdfeditor.host.json.template")

case "$(uname -s)" in
  Darwin)
    TARGETS=(
      "$HOME/Library/Application Support/Google/Chrome/NativeMessagingHosts"
      "$HOME/Library/Application Support/Chromium/NativeMessagingHosts"
      "$HOME/Library/Application Support/Microsoft Edge/NativeMessagingHosts"
      "$HOME/Library/Application Support/BraveSoftware/Brave-Browser/NativeMessagingHosts"
    )
    ;;
  *)
    TARGETS=(
      "$HOME/.config/google-chrome/NativeMessagingHosts"
      "$HOME/.config/chromium/NativeMessagingHosts"
      "$HOME/.config/microsoft-edge/NativeMessagingHosts"
      "$HOME/.config/BraveSoftware/Brave-Browser/NativeMessagingHosts"
    )
    ;;
esac

for dir in "${TARGETS[@]}"; do
  mkdir -p "$dir"
  printf '%s\n' "$MANIFEST_JSON" > "$dir/$HOST_NAME.json"
  echo "Registered: $dir/$HOST_NAME.json"
done

echo
echo "Done. Restart your browser, then re-test from the extension's options page."
