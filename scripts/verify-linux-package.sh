#!/usr/bin/env bash
# Checks that a built Linux host package really registers the native messaging host where the
# browser will look for it. Understands .deb, .rpm and Arch .pkg.tar.zst.
#
# Everything about a native messaging host is silent when it is wrong: the browser reports
# "Specified native messaging host not found" whether the manifest is missing, in a directory that
# browser does not read, malformed, or pointing at a path that is not in the package. This turns
# each of those into a build failure with a name.
#
# Usage: ./scripts/verify-linux-package.sh <package> [<package>...]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=scripts/linux-manifest-dirs.sh
source "$REPO_ROOT/scripts/linux-manifest-dirs.sh"

HOST_NAME="com.pdfeditor.host"
LAUNCH_PATH="/usr/bin/pdf-editor-host"
REGISTER_PATH="/usr/bin/pdf-editor-host-register"

EXPECTED_ID="${CHROME_EXTENSION_ID:-}"
if [[ -z "$EXPECTED_ID" && -f "$REPO_ROOT/scripts/extension-id.txt" ]]; then
  EXPECTED_ID="$(tr -d '[:space:]' < "$REPO_ROOT/scripts/extension-id.txt")"
fi

EXPECTED_EDGE_ID="${EDGE_EXTENSION_ID:-}"
if [[ -z "$EXPECTED_EDGE_ID" && -f "$REPO_ROOT/scripts/edge-extension-id.txt" ]]; then
  EXPECTED_EDGE_ID="$(tr -d '[:space:]' < "$REPO_ROOT/scripts/edge-extension-id.txt")"
fi

if [[ $# -eq 0 ]]; then
  echo "Usage: $0 <package> [<package>...]" >&2
  exit 1
fi

failures=0
fail() { echo "  FAIL: $*" >&2; failures=$((failures + 1)); }
pass() { echo "  ok:   $*"; }

# Unpacks a package into $1 (a directory), whichever of the three formats it is.
extract() {
  local pkg="$1" dest="$2"
  case "$pkg" in
    *.deb)
      dpkg-deb -x "$pkg" "$dest" ;;
    *.rpm)
      # An RPM payload is a cpio stream; bsdtar reads it, and is already needed for the Arch
      # package, so this adds no tool the packaging steps do not already have.
      rpm2cpio "$pkg" | bsdtar -x -C "$dest" -f - ;;
    *.pkg.tar.zst)
      bsdtar -xf "$pkg" -C "$dest" ;;
    *)
      echo "error: don't know how to unpack '$pkg'" >&2; exit 1 ;;
  esac
}

verify_one() {
  local pkg="$1"
  echo "== $(basename "$pkg")"

  local root
  root="$(mktemp -d)"
  # shellcheck disable=SC2064  # $root is intentionally expanded now, not at trap time.
  trap "rm -rf '$root'" RETURN
  extract "$pkg" "$root"

  # 1. A manifest in every directory a supported browser reads. Missing one means that browser
  #    silently never sees the host.
  local dir manifest missing=0
  for dir in "${LINUX_MANIFEST_DIRS[@]}"; do
    manifest="$root/$dir/$HOST_NAME.json"
    if [[ ! -f "$manifest" ]]; then
      fail "no manifest in /$dir"
      missing=$((missing + 1))
    fi
  done
  [[ $missing -eq 0 ]] && pass "manifest present in all ${#LINUX_MANIFEST_DIRS[@]} browser directories"

  # 2. Every manifest is identical, valid JSON, names the host, and points at a path the package
  #    actually ships as an executable.
  local reference="$root/${LINUX_MANIFEST_DIRS[0]}/$HOST_NAME.json"
  if [[ ! -f "$reference" ]]; then
    fail "no manifest to inspect — skipping content checks"
    return
  fi

  local host_path origins name
  if ! host_path="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["path"])' "$reference" 2>/dev/null)"; then
    fail "$reference is not valid JSON, or has no \"path\""
    return
  fi
  name="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["name"])' "$reference")"
  origins="$(python3 -c 'import json,sys; print(",".join(json.load(open(sys.argv[1]))["allowed_origins"]))' "$reference")"

  if [[ "$name" == "$HOST_NAME" ]]; then
    pass "manifest name is $HOST_NAME"
  else
    fail "manifest name is '$name', expected '$HOST_NAME'"
  fi

  for dir in "${LINUX_MANIFEST_DIRS[@]}"; do
    manifest="$root/$dir/$HOST_NAME.json"
    [[ -f "$manifest" ]] || continue
    if ! cmp -s "$reference" "$manifest"; then
      fail "/$dir/$HOST_NAME.json differs from /${LINUX_MANIFEST_DIRS[0]}/$HOST_NAME.json"
    fi
  done

  # 3. The launch path. Chrome execs exactly this string, so it must be absolute and it must
  #    resolve — through the package's own symlink — to a file the package ships with +x.
  if [[ "$host_path" != /* ]]; then
    fail "manifest path '$host_path' is not absolute"
  elif [[ ! -e "$root$host_path" && ! -L "$root$host_path" ]]; then
    fail "manifest path '$host_path' is not shipped by the package"
  else
    local target="$root$host_path"
    if [[ -L "$target" ]]; then
      local link
      link="$(readlink "$target")"
      target="$root$link"
      if [[ -f "$target" ]]; then
        pass "manifest path $host_path -> $link (shipped)"
      else
        fail "manifest path $host_path is a symlink to '$link', which the package does not ship"
      fi
    fi
    if [[ -f "$target" && ! -x "$target" ]]; then
      fail "$host_path resolves to a file that is not executable"
    elif [[ -f "$target" ]]; then
      pass "$host_path is executable"
    fi
  fi

  if [[ "$host_path" == "$LAUNCH_PATH" ]]; then
    pass "manifest points at $LAUNCH_PATH (reachable from sandboxed browsers)"
  else
    fail "manifest points at '$host_path', expected '$LAUNCH_PATH'"
  fi

  # 4. allowed_origins must be the pinned Chrome and Edge extension IDs, with the trailing slash
  #    Chrome requires, Chrome first then Edge (the order every renderer here uses).
  if [[ -n "$EXPECTED_ID" && -n "$EXPECTED_EDGE_ID" ]]; then
    expected="chrome-extension://$EXPECTED_ID/,chrome-extension://$EXPECTED_EDGE_ID/"
    if [[ "$origins" == "$expected" ]]; then
      pass "allowed_origins pinned to $EXPECTED_ID and $EXPECTED_EDGE_ID"
    else
      fail "allowed_origins is '$origins', expected '$expected'"
    fi
  fi

  # 5. The per-user helper — the only route to snap/flatpak browsers and developer-mode IDs.
  if [[ -x "$root$REGISTER_PATH" ]]; then
    pass "$REGISTER_PATH shipped and executable"
  else
    fail "$REGISTER_PATH is missing or not executable"
  fi

  # 6. Nothing in the payload may be unreadable by the user the browser runs as.
  local unreadable
  unreadable="$(find "$root/opt/pdf-editor-host" ! -perm -o=r -print 2>/dev/null | head -5)"
  if [[ -z "$unreadable" ]]; then
    pass "payload is world-readable"
  else
    fail "not readable by other users: $(echo "$unreadable" | tr '\n' ' ')"
  fi
}

for pkg in "$@"; do
  [[ -f "$pkg" ]] || { echo "error: no such file: $pkg" >&2; exit 1; }
  verify_one "$pkg"
  echo
done

if [[ $failures -gt 0 ]]; then
  echo "$failures check(s) failed." >&2
  exit 1
fi
echo "All packages register the native messaging host correctly."
