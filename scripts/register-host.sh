#!/usr/bin/env bash
# Registers an already-installed PDF Editor native messaging host with the *current user's*
# Chromium-based browsers.
#
# The .deb / .rpm / Arch packages register the host system-wide under /etc, which covers every
# browser installed from a normal distro/vendor package. Two common cases that /etc cannot reach:
#
#   * snap and flatpak browsers  -- they run sandboxed and read their manifests from inside the
#     sandbox's own config tree (~/snap/... or ~/.var/app/...), never from /etc.
#   * a developer-mode extension -- the packages pin allowed_origins to the published Web Store
#     extension ID; an unpacked extension has a different, machine-specific ID, so it needs its own
#     manifest. A per-user manifest takes precedence over the system-wide one.
#
# This script fixes both. It only writes manifests -- it never downloads or builds anything, so it
# is safe to re-run. The Linux packages install it as /usr/bin/pdf-editor-host-register; it also
# runs on macOS, where scripts/install-host.sh delegates its registration step to it.
#
# Usage:
#   pdf-editor-host-register [--extension-id ID] [--host-path PATH] [--uninstall] [--list]
set -euo pipefail

HOST_NAME="com.pdfeditor.host"
# Pinned Chrome Web Store extension ID, used when nothing else supplies one. The packages drop the
# ID they were built with next to the host so a rebuild for a different ID stays self-consistent.
DEFAULT_EXTENSION_ID="ikbkielkpaloojhibinmcfbeekhkdblc"
EXTENSION_ID_FILE="/usr/share/pdf-editor-host/extension-id"
# Where the OS packages put the host. The /usr/bin entry is a symlink onto the second path; it is
# preferred because sandboxed browsers are far more likely to be allowed to execute /usr/bin.
HOST_PATH_CANDIDATES=(
  "/usr/bin/pdf-editor-host"
  "/opt/pdf-editor-host/PdfEditor.NativeHost"
  "$HOME/.local/share/pdf-editor-host/PdfEditor.NativeHost"
)

EXTENSION_ID=""
HOST_PATH=""
MODE="install"

usage() {
  cat >&2 <<'USAGE'
Usage: pdf-editor-host-register [options]

  --extension-id ID   Extension ID to allow (default: the pinned Web Store ID, or the ID the
                      installed package was built with). Use your unpacked extension's ID --
                      find it at chrome://extensions with Developer mode on.
  --host-path PATH    Path to the host executable (default: autodetected).
  --uninstall         Remove the per-user manifests this script wrote.
  --list              Show what would be written (or removed) and exit.
  -h, --help          This message.
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --extension-id) EXTENSION_ID="${2:?--extension-id needs a value}"; shift 2 ;;
    --host-path)    HOST_PATH="${2:?--host-path needs a value}"; shift 2 ;;
    --uninstall)    MODE="uninstall"; shift ;;
    --list)         MODE="list"; shift ;;
    -h|--help)      usage; exit 0 ;;
    *) echo "error: unknown argument '$1'" >&2; usage; exit 1 ;;
  esac
done

if [[ -z "$EXTENSION_ID" ]]; then
  EXTENSION_ID="${CHROME_EXTENSION_ID:-}"
fi
if [[ -z "$EXTENSION_ID" && -r "$EXTENSION_ID_FILE" ]]; then
  EXTENSION_ID="$(tr -d '[:space:]' < "$EXTENSION_ID_FILE")"
fi
: "${EXTENSION_ID:=$DEFAULT_EXTENSION_ID}"

# A Chrome extension ID is exactly 32 characters from a-p (a base-16 digest re-encoded into
# letters). Catching a typo here beats a browser silently refusing the connection later.
if [[ ! "$EXTENSION_ID" =~ ^[a-p]{32}$ ]]; then
  echo "error: '$EXTENSION_ID' is not a valid extension ID (expected 32 characters, a-p)" >&2
  exit 1
fi

if [[ -z "$HOST_PATH" ]]; then
  for candidate in "${HOST_PATH_CANDIDATES[@]}"; do
    if [[ -x "$candidate" ]]; then HOST_PATH="$candidate"; break; fi
  done
fi
# Only writing a manifest needs a real host path; --list and --uninstall work without one.
if [[ -z "$HOST_PATH" && "$MODE" == "install" ]]; then
  echo "error: no PDF Editor native host found. Looked in:" >&2
  printf '         %s\n' "${HOST_PATH_CANDIDATES[@]}" >&2
  echo "       Install the OS package first, or pass --host-path <path>." >&2
  exit 1
fi

# -------------------------------------------------------------------- targets
#
# Per-user manifest directories, in three families:
#
#   plain    ~/.config/<browser>/NativeMessagingHosts            -- distro/vendor-packaged browsers
#   snap     ~/snap/<snap>/current/.config/<browser>/NativeMes... -- the snap's confined $HOME
#   flatpak  ~/.var/app/<app-id>/config/<browser>/NativeMessa...  -- the flatpak's confined config
#
# Only the plain ones are created unconditionally (a browser may not have run yet). Snap and
# flatpak entries are written only when that sandbox actually exists on this machine, so we never
# scatter directories for software that is not installed.
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}"
# macOS keeps per-user manifests under ~/Library/Application Support instead, and has no snap or
# flatpak equivalent; the add_snap/add_flatpak calls below simply find nothing there.
if [[ "$(uname -s)" == "Darwin" ]]; then
  CONFIG_DIR="$HOME/Library/Application Support"
fi

TARGETS=()          # directories to write the manifest into
NOTES=()            # extra advice to print at the end (flatpak filesystem overrides, etc.)

# add_plain <browser-config-dir>
add_plain() {
  local browser_dir="$1"
  TARGETS+=("$CONFIG_DIR/$browser_dir/NativeMessagingHosts")
}

# add_snap <snap-name> <browser-config-dir>
add_snap() {
  local snap_name="$1"
  local browser_dir="$2"
  local root="$HOME/snap/$snap_name/current"
  [[ -d "$root" ]] || return 0
  TARGETS+=("$root/.config/$browser_dir/NativeMessagingHosts")
  NOTES+=("snap '$snap_name' detected. Strictly confined snaps may still refuse to launch a host that lives outside the sandbox; if the connection is still refused, install the .deb build of the browser (or use the flatpak) instead.")
}

# add_flatpak <app-id> <browser-config-dir>
add_flatpak() {
  local app_id="$1"
  local browser_dir="$2"
  local root="$HOME/.var/app/$app_id"
  [[ -d "$root" ]] || return 0
  TARGETS+=("$root/config/$browser_dir/NativeMessagingHosts")
  # A flatpak browser cannot see /usr or /opt on the host unless it is granted access.
  NOTES+=("flatpak '$app_id' detected. Grant it access to the host binary once with:
    flatpak override --user --filesystem=/opt/pdf-editor-host:ro --filesystem=/usr/bin/pdf-editor-host:ro $app_id")
}

# The per-browser directory name differs between the two platforms (Linux uses the packaged name,
# macOS the product name), so each gets its own list.
if [[ "$(uname -s)" == "Darwin" ]]; then
  BROWSER_DIRS=(
    "Google/Chrome" "Google/Chrome Beta" "Google/Chrome Dev" "Google/Chrome Canary"
    "Chromium"
    "Microsoft Edge" "Microsoft Edge Beta" "Microsoft Edge Dev"
    "BraveSoftware/Brave-Browser" "BraveSoftware/Brave-Browser-Beta"
    "BraveSoftware/Brave-Browser-Nightly"
    "Vivaldi" "com.operasoftware.Opera"
  )
else
  BROWSER_DIRS=(
    google-chrome google-chrome-beta google-chrome-unstable
    chromium chromium-browser
    microsoft-edge microsoft-edge-beta microsoft-edge-dev
    BraveSoftware/Brave-Browser BraveSoftware/Brave-Browser-Beta
    BraveSoftware/Brave-Browser-Nightly
    vivaldi opera opera-beta opera-developer
  )
fi
for name in "${BROWSER_DIRS[@]}"; do
  add_plain "$name"
done

add_snap chromium chromium
add_snap brave BraveSoftware/Brave-Browser
add_snap opera opera

add_flatpak com.google.Chrome google-chrome
add_flatpak org.chromium.Chromium chromium
add_flatpak com.microsoft.Edge microsoft-edge
add_flatpak com.brave.Browser BraveSoftware/Brave-Browser
add_flatpak com.vivaldi.Vivaldi vivaldi
add_flatpak com.opera.Opera opera

# -------------------------------------------------------------------- actions

if [[ "$MODE" == "list" ]]; then
  echo "Extension ID: $EXTENSION_ID"
  echo "Host path:    ${HOST_PATH:-<not found>}"
  echo "Manifests:"
  for dir in "${TARGETS[@]}"; do echo "  $dir/$HOST_NAME.json"; done
  exit 0
fi

if [[ "$MODE" == "uninstall" ]]; then
  removed=0
  for dir in "${TARGETS[@]}"; do
    if [[ -f "$dir/$HOST_NAME.json" ]]; then
      rm -f "$dir/$HOST_NAME.json"
      echo "Removed: $dir/$HOST_NAME.json"
      removed=$((removed + 1))
    fi
  done
  echo
  echo "Removed $removed per-user manifest(s). The system-wide ones under /etc belong to the OS"
  echo "package -- remove those with your package manager (e.g. sudo apt remove pdf-editor-host)."
  exit 0
fi

read -r -d '' MANIFEST <<MANIFEST_JSON || true
{
  "name": "$HOST_NAME",
  "description": "PDF Editor native messaging host (C#/.NET)",
  "path": "$HOST_PATH",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://$EXTENSION_ID/"
  ]
}
MANIFEST_JSON

written=0
for dir in "${TARGETS[@]}"; do
  mkdir -p "$dir"
  printf '%s\n' "$MANIFEST" > "$dir/$HOST_NAME.json"
  chmod 0644 "$dir/$HOST_NAME.json"
  echo "Registered: $dir/$HOST_NAME.json"
  written=$((written + 1))
done

echo
echo "Registered the host for this user in $written location(s)."
echo "  host path:    $HOST_PATH"
echo "  extension ID: $EXTENSION_ID"

# The host is only useful if it actually starts; a self-contained .NET build that is missing a
# system library exits immediately and the browser reports it as a host that "has exited", which
# looks exactly like a host that was never installed. Say so here instead.
if [[ -x "$HOST_PATH" ]] && ! "$HOST_PATH" --version >/dev/null 2>&1; then
  echo
  echo "warning: $HOST_PATH did not start. Run it directly to see why:"
  echo "    $HOST_PATH --diagnostics"
  echo "  A self-contained .NET host needs ICU and OpenSSL; on Debian/Ubuntu:"
  echo "    sudo apt install libicu-dev libssl3 || sudo apt install libicu-dev libssl1.1"
fi

if [[ ${#NOTES[@]} -gt 0 ]]; then
  echo
  for note in "${NOTES[@]}"; do echo "note: $note"; done
fi

echo
echo "Restart your browser, then re-test from the PDF Editor options page."
