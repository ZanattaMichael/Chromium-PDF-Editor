#!/usr/bin/env bash
# Fast regression test for the Linux packaging scripts: builds a .deb, .rpm and Arch package with
# a *stub* host payload, then runs scripts/verify-linux-package.sh over all three.
#
# The point is to exercise the packaging logic -- which directories the manifest lands in, what the
# manifest says, which files are shipped and with what permissions -- on every push, without the
# .NET SDK and without the two-minute self-contained publish. It does that by putting a `dotnet`
# stub on PATH ahead of any real one, so `dotnet publish` drops a few placeholder files instead of
# a runtime. Nothing in the packaging scripts knows or cares that it is a test.
#
# Needs: dpkg-deb, rpmbuild, bsdtar, zstd, python3.
#
# Usage: ./scripts/test-package-scripts.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

for tool in dpkg-deb rpmbuild bsdtar zstd python3; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "error: '$tool' is required. On Ubuntu: sudo apt-get install -y dpkg-dev rpm libarchive-tools zstd" >&2
    exit 1
  }
done

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# A `dotnet` that answers `publish` by writing a handful of files with deliberately awkward
# permissions (0700 exe, 0600 native libs and data), so the packagers' mode normalisation is
# actually under test rather than inherited from the build umask.
mkdir -p "$WORK/bin"
cat > "$WORK/bin/dotnet" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
out=""
prev=""
for arg in "$@"; do
  case "$prev" in --output|-o) out="$arg" ;; esac
  prev="$arg"
done
[[ -n "$out" ]] || { echo "stub dotnet: no --output given" >&2; exit 1; }
mkdir -p "$out/sub"
printf '#!/bin/sh\ncase "$1" in --version|--diagnostics|--selftest) echo stub-host ;; esac\n' \
  > "$out/PdfEditor.NativeHost"
chmod 0700 "$out/PdfEditor.NativeHost"
echo '{}'      > "$out/PdfEditor.NativeHost.runtimeconfig.json"; chmod 0600 "$out/PdfEditor.NativeHost.runtimeconfig.json"
echo 'stub-so' > "$out/libSkiaSharp.so";                         chmod 0600 "$out/libSkiaSharp.so"
echo 'stub'    > "$out/createdump";                              chmod 0600 "$out/createdump"
echo 'stub'    > "$out/sub/icudt.dat";                           chmod 0600 "$out/sub/icudt.dat"
STUB
chmod +x "$WORK/bin/dotnet"

echo "Building the three Linux packages with a stub host payload..."
PATH="$WORK/bin:$PATH" "$REPO_ROOT/scripts/package-deb.sh"  "$WORK/dist" > "$WORK/deb.log"  2>&1 \
  || { cat "$WORK/deb.log"; exit 1; }
PATH="$WORK/bin:$PATH" "$REPO_ROOT/scripts/package-rpm.sh"  "$WORK/dist" > "$WORK/rpm.log"  2>&1 \
  || { cat "$WORK/rpm.log"; exit 1; }
PATH="$WORK/bin:$PATH" "$REPO_ROOT/scripts/package-arch.sh" "$WORK/dist" > "$WORK/arch.log" 2>&1 \
  || { cat "$WORK/arch.log"; exit 1; }
echo

"$REPO_ROOT/scripts/verify-linux-package.sh" \
  "$WORK"/dist/pdf-editor-host_*.deb \
  "$WORK"/dist/pdf-editor-host-*.rpm \
  "$WORK"/dist/pdf-editor-host-*.pkg.tar.zst

# The stub sets 0700/0600 throughout; if the packagers did not normalise, the verifier's
# world-readable check above would already have failed. Assert the executable bit too, which the
# verifier only checks for the one path the manifest names.
echo "Checking payload permissions in the .deb..."
bad="$(dpkg-deb -c "$WORK"/dist/pdf-editor-host_*.deb \
  | awk '$1 ~ /^-/ && $6 ~ /opt\/pdf-editor-host/ && $1 !~ /^-rw.r..r../ && $1 !~ /^-rwxr-xr-x/ { print $1, $6 }')"
if [[ -n "$bad" ]]; then
  echo "  FAIL: payload files with unexpected modes:" >&2
  echo "$bad" >&2
  exit 1
fi
echo "  ok:   every payload file is 0644 or 0755"

echo
echo "Packaging scripts OK."
