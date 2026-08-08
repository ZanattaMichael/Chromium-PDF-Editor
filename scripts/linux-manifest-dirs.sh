# Shared list of system-wide native-messaging manifest directories for the common Chromium-based
# browsers on Linux, sourced by the .deb / .rpm / Arch packagers so they register the host in the
# same set of places. Each browser reads only its own directory and ignores the others, so shipping
# the manifest to all of them is safe and means the host connects no matter which browser is
# installed. Paths are relative (no leading slash) so each packager can prefix its build root.
#
# Firefox is intentionally omitted: it uses a different manifest schema (allowed_extensions with an
# add-on ID, not allowed_origins with a chrome-extension:// origin) and a different location.
LINUX_MANIFEST_DIRS=(
  "etc/opt/chrome/native-messaging-hosts"        # Google Chrome (Brave also reads this path)
  "etc/chromium/native-messaging-hosts"          # Chromium (distro packages) and Vivaldi (legacy)
  "etc/chromium-browser/native-messaging-hosts"  # older Ubuntu/Debian chromium-browser package
  "etc/opt/edge/native-messaging-hosts"          # Microsoft Edge
  "etc/opt/brave/native-messaging-hosts"         # Brave (current builds use their own path)
  "etc/opt/vivaldi/native-messaging-hosts"       # Vivaldi
  "etc/opt/opera/native-messaging-hosts"         # Opera
)
