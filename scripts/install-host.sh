#!/usr/bin/env bash
# Installs the PDF Editor native messaging host and registers it with Chromium-based
# browsers (Chrome, Chromium, Edge, Brave).
#
# How the host binary is obtained, in priority order:
#   1. --host-dir <dir>   use an already-extracted self-contained host (e.g. the host/
#                         folder from a release bundle) -- no download, no build.
#   2. a sibling host/    if this script sits inside an unzipped release bundle (a host/
#                         folder next to it), that is used automatically.
#   3. --from-source      build locally with `dotnet publish` (needs the .NET SDK).
#   4. default            download the platform bundle from the latest GitHub release and
#                         use the host/ inside it -- the runtime and native libs are
#                         bundled, so no .NET SDK is required.
#
# Usage:
#   ./scripts/install-host.sh <extension-id> [options]
#     --host-dir <dir>           use an extracted self-contained host in <dir>
#     --from-source              build locally with dotnet instead of downloading
#     --configuration <cfg>      build configuration for --from-source (default: Release)
#     --repo <owner/name>        GitHub repo to download from (default: inferred from git remote)
#     --tag <vX.Y.Z>             release tag to download (default: latest release)
set -euo pipefail

DEFAULT_REPO="ZanattaMichael/Chromium-PDF-Editor"

EXTENSION_ID=""
FROM_SOURCE=false
CONFIGURATION="Release"
REPO_OVERRIDE=""
TAG_OVERRIDE=""
HOST_DIR=""

usage() {
  echo "Usage: $0 <extension-id> [--host-dir dir] [--from-source] [--configuration Release] [--repo owner/name] [--tag vX.Y.Z]" >&2
  echo "Find the extension ID at chrome://extensions with Developer mode enabled." >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --host-dir) HOST_DIR="${2:?--host-dir needs a value}"; shift 2 ;;
    --from-source) FROM_SOURCE=true; shift ;;
    --configuration) CONFIGURATION="${2:?--configuration needs a value}"; shift 2 ;;
    --repo) REPO_OVERRIDE="${2:?--repo needs a value}"; shift 2 ;;
    --tag) TAG_OVERRIDE="${2:?--tag needs a value}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    -*) echo "Unknown option: $1" >&2; usage; exit 1 ;;
    *)
      if [[ -z "$EXTENSION_ID" ]]; then EXTENSION_ID="$1"; else echo "Unexpected argument: $1" >&2; usage; exit 1; fi
      shift ;;
  esac
done

if [[ -z "$EXTENSION_ID" ]]; then
  usage
  exit 1
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="$HOME/.local/share/pdf-editor-host"
HOST_NAME="com.pdfeditor.host"

# The runtime identifier for this machine, in the naming the release assets use.
detect_rid() {
  local os arch
  case "$(uname -s)" in
    Linux) os="linux" ;;
    Darwin) os="osx" ;;
    *) echo "error: unsupported OS '$(uname -s)' for a prebuilt host — use --from-source" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    x86_64|amd64) arch="x64" ;;
    arm64|aarch64) arch="arm64" ;;
    *) echo "error: unsupported architecture '$(uname -m)' for a prebuilt host — use --from-source" >&2; exit 1 ;;
  esac
  # Only osx ships both x64 and arm64; linux is published as x64 only.
  if [[ "$os" == "linux" && "$arch" != "x64" ]]; then
    echo "error: no prebuilt linux-$arch host is published — use --from-source" >&2
    exit 1
  fi
  echo "$os-$arch"
}

# owner/name to download from: explicit override, else inferred from origin's URL, else default.
resolve_repo() {
  if [[ -n "$REPO_OVERRIDE" ]]; then echo "$REPO_OVERRIDE"; return; fi
  local url
  url="$(git -C "$REPO_ROOT" remote get-url origin 2>/dev/null || true)"
  if [[ "$url" =~ github\.com[:/]+([^/]+)/([^/.]+) ]]; then
    echo "${BASH_REMATCH[1]}/${BASH_REMATCH[2]}"
  else
    echo "$DEFAULT_REPO"
  fi
}

extract_tag() {
  grep -m1 '"tag_name"' | sed -E 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/'
}

# The release tag to install: explicit override, else the latest final release, else the
# newest release of any kind (so a repo with only prereleases still resolves).
resolve_tag() {
  local repo="$1" tag
  if [[ -n "$TAG_OVERRIDE" ]]; then echo "$TAG_OVERRIDE"; return; fi
  tag="$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" 2>/dev/null | extract_tag || true)"
  if [[ -z "$tag" ]]; then
    tag="$(curl -fsSL "https://api.github.com/repos/$repo/releases?per_page=1" 2>/dev/null | extract_tag || true)"
  fi
  if [[ -z "$tag" ]]; then
    echo "error: could not determine the latest release tag for $repo — pass --tag <vX.Y.Z>" >&2
    exit 1
  fi
  echo "$tag"
}

# Copies an extracted self-contained host directory into PUBLISH_DIR and sets HOST_PATH.
install_from_dir() {
  local src="$1"
  echo "Installing host from $src..."
  echo "  (will be copied to $PUBLISH_DIR and registered as $HOST_NAME.json)"
  src="$(cd "$src" && pwd)"
  if [[ ! -f "$src/PdfEditor.NativeHost" ]]; then
    echo "error: no PdfEditor.NativeHost found in $src ($src/PdfEditor.NativeHost)" >&2
    exit 1
  fi
  rm -rf "$PUBLISH_DIR"
  mkdir -p "$PUBLISH_DIR"
  cp -R "$src/." "$PUBLISH_DIR/"
  HOST_PATH="$PUBLISH_DIR/PdfEditor.NativeHost"
  chmod +x "$HOST_PATH"
}

# When this script is run from inside an unzipped release bundle, the self-contained host
# sits in a host/ folder next to it (REPO_ROOT is the bundle root here). Use it if present.
BUNDLED_HOST="$REPO_ROOT/host"

if [[ -n "$HOST_DIR" ]]; then
  echo "Using the host in $HOST_DIR..."
  install_from_dir "$HOST_DIR"
elif [[ "$FROM_SOURCE" == true ]]; then
  echo "Building native host from source ($CONFIGURATION)..."
  rm -rf "$PUBLISH_DIR"
  mkdir -p "$PUBLISH_DIR"
  dotnet publish "$REPO_ROOT/src/PdfEditor.NativeHost" -c "$CONFIGURATION" -o "$PUBLISH_DIR" --nologo -v q
  # A framework-dependent build runs via the installed `dotnet`, so launch it through a shim.
  LAUNCHER="$PUBLISH_DIR/pdf-editor-host.sh"
  cat > "$LAUNCHER" <<LAUNCH
#!/usr/bin/env bash
exec dotnet "$PUBLISH_DIR/PdfEditor.NativeHost.dll" "\$@"
LAUNCH
  chmod +x "$LAUNCHER"
  HOST_PATH="$LAUNCHER"
elif [[ -f "$BUNDLED_HOST/PdfEditor.NativeHost" ]]; then
  echo "Using the bundled host next to this script..."
  install_from_dir "$BUNDLED_HOST"
else
  for tool in curl unzip; do
    command -v "$tool" >/dev/null 2>&1 || { echo "error: '$tool' is required to download the prebuilt host — install it or use --from-source" >&2; exit 1; }
  done
  RID="$(detect_rid)"
  REPO="$(resolve_repo)"
  TAG="$(resolve_tag "$REPO")"
  URL="https://github.com/$REPO/releases/download/$TAG/pdf-editor-bundle-$RID.zip"

  echo "Downloading prebuilt bundle $TAG ($RID) from $REPO..."
  TMP="$(mktemp -d)"
  trap 'rm -rf "$TMP"' EXIT
  if ! curl -fSL -o "$TMP/bundle.zip" "$URL"; then
    echo "error: could not download $URL" >&2
    echo "       That release may not have a $RID bundle yet. Try --from-source, or --tag/--repo." >&2
    exit 1
  fi
  unzip -q -o "$TMP/bundle.zip" -d "$TMP/bundle"
  if [[ ! -d "$TMP/bundle/host" ]]; then
    echo "error: the downloaded bundle did not contain a host/ folder" >&2
    exit 1
  fi
  install_from_dir "$TMP/bundle/host"
fi

MANIFEST_JSON=$(sed -e "s|__HOST_PATH__|$HOST_PATH|" -e "s|__EXTENSION_ID__|$EXTENSION_ID|" \
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
