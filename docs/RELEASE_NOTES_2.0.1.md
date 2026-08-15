# PDF Editor 2.0.1

This release is all about **getting the app installed and connected reliably**, on more browsers and more operating systems. The editing features from 2.0.0 are unchanged; 2.0.1 makes the native host easy to install, works across every major Chromium browser, and hardens the release pipeline.

## Highlights

- **One-step native-host installers for every OS.** Install the local processing host from a proper OS package — no .NET SDK required:
  - **Linux:** `.deb` (Debian/Ubuntu), `.rpm` (Fedora/RHEL/openSUSE), and Arch `.pkg.tar.zst`.
  - **Windows:** a signed-friendly `.msi`.
  - **Any platform:** the all-in-one `pdf-editor-bundle-<platform>.zip` (extension + matching host + install script).
- **Works across all major Chromium browsers.** The host now registers system-wide for **Chrome, Chromium, Edge, Brave, Vivaldi, and Opera** (plus the older Ubuntu `chromium-browser`). This fixes the *"Specified native messaging host not found"* error people hit on non-Chrome browsers.
- **Built-in diagnostics for troubleshooting.**
  - A **Copy diagnostics** button on the options page copies your host/connection details (extension version, browser, host version, runtime, OS) — ready to paste into a bug report. It now appears even when the host is *disconnected*, which is exactly when you need it.
  - The host can self-report from the command line: `PdfEditor.NativeHost --diagnostics` (also `--version`).
- **Verified release downloads.** Every release artifact is now scanned with VirusTotal before it's published or deployed, so the files you download have been checked for malware.

## Improvements & fixes

- Fixed the Windows `.msi` build (pinned WiX to v5 and split the file/registry components) so the installer builds reliably.
- Added clear documentation for the OS installer packages, including the important note that they register the host for the **published Chrome Web Store extension** — with instructions for re-registering if you run a developer-mode/unpacked build.
- Chrome Web Store readiness: pinned extension ID, privacy policy, and packaging playbook.

## Installing / upgrading

- **From the Chrome Web Store:** update the extension, then (if you use local processing) install the native host with the OS package for your platform or the all-in-one bundle. See the **"Installing"** section of the README.
- **Already have the host?** Re-run your platform's installer to pick up the wider browser coverage.
- **Troubleshooting a connection:** open the extension's **Options** page, check the **Native host** status, and use **Copy diagnostics** if you need to report a problem.

## Notes

- Firefox is intentionally not covered — it uses a different native-messaging manifest format.
- The OS packages pin to the Web Store extension ID; a developer-mode/unpacked extension has a different ID and needs a per-user re-registration (`install-host.sh <id>` / `install-host.ps1 -ExtensionId <id>`). See the README for details.

**Full changelog:** #109, #112, #113, #114, #117 (since 2.0.0).
