# Shared list of system-wide native-messaging manifest directories for the common Chromium-based
# browsers on Linux, sourced by the .deb / .rpm / Arch packagers so they register the host in the
# same set of places. Each browser reads only its own directory and ignores the others, so shipping
# the manifest to all of them is safe and means the host connects no matter which browser is
# installed. Paths are relative (no leading slash) so each packager can prefix its build root.
#
# The directory a Chromium build reads is baked in at compile time (chrome/common/chrome_paths.cc):
# branded Chrome uses /etc/opt/<product>, unbranded Chromium uses /etc/<product>, and every channel
# is a separate product ("chrome-beta", "chrome-unstable", "edge-dev", ...). Missing a channel's
# directory is the single most common reason a system-wide host installs cleanly and is then never
# seen by the browser, so all of them are covered here. A manifest in a directory whose browser is
# not installed is an inert 259-byte file.
#
# Firefox is intentionally omitted: it uses a different manifest schema (allowed_extensions with an
# add-on ID, not allowed_origins with a chrome-extension:// origin) and a different location.
#
# Not covered here, because no system-wide directory can reach them: snap- and flatpak-packaged
# browsers, which are sandboxed away from /etc and /opt. Those are handled per-user by
# scripts/register-host.sh (shipped by the packages as /usr/bin/pdf-editor-host-register).
LINUX_MANIFEST_DIRS=(
  # --- Google Chrome (branded): /etc/opt/<product>, one product per channel.
  "etc/opt/chrome/native-messaging-hosts"           # Chrome stable
  "etc/opt/chrome-beta/native-messaging-hosts"      # Chrome Beta
  "etc/opt/chrome-unstable/native-messaging-hosts"  # Chrome Dev ("unstable" is the packaged name)

  # --- Chromium (unbranded): /etc/<product>.
  "etc/chromium/native-messaging-hosts"             # Chromium (Debian/Fedora/Arch packages)
  "etc/chromium-browser/native-messaging-hosts"     # older Ubuntu/Debian chromium-browser package

  # --- Chrome for Testing: its own branding, and so its own product directory
  # (GOOGLE_CHROME_FOR_TESTING_BRANDING in chrome_paths.cc). This is the build Playwright and
  # Puppeteer download and drive, which makes it the browser most likely to be pointed at a
  # freshly installed host on a developer machine -- including by this project's own
  # package-install e2e suite, which caught the omission.
  "etc/opt/chrome_for_testing/native-messaging-hosts"

  # --- Microsoft Edge.
  "etc/opt/edge/native-messaging-hosts"             # Edge stable
  "etc/opt/edge-beta/native-messaging-hosts"        # Edge Beta
  "etc/opt/edge-dev/native-messaging-hosts"         # Edge Dev

  # --- Brave. Current builds read /etc/opt/brave; some older/derived builds read /etc/brave.
  "etc/opt/brave/native-messaging-hosts"            # Brave stable
  "etc/opt/brave-beta/native-messaging-hosts"       # Brave Beta
  "etc/opt/brave-nightly/native-messaging-hosts"    # Brave Nightly
  "etc/brave/native-messaging-hosts"                # Brave (alternate unbranded-style path)

  # --- Vivaldi.
  "etc/opt/vivaldi/native-messaging-hosts"          # Vivaldi stable
  "etc/vivaldi/native-messaging-hosts"              # Vivaldi (alternate unbranded-style path)

  # --- Opera.
  "etc/opt/opera/native-messaging-hosts"            # Opera stable
  "etc/opt/opera-beta/native-messaging-hosts"       # Opera Beta
  "etc/opt/opera-developer/native-messaging-hosts"  # Opera Developer
)
