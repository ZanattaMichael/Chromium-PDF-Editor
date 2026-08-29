# PDF Editor 2.0.2

2.0.1 shipped native-host installers for every OS. 2.0.2 is about the gap that left: a package
could install perfectly and the browser still not find the host — and nothing in the extension
said so. This release fixes the packaging faults that caused it, and makes the extension diagnose
the rest instead of failing silently.

The editing features are unchanged from 2.0.0.

## Highlights

- **The Linux packages now register with every Chromium browser you might actually be running.**
  The manifest went to 7 directories, all of them stable channels. It now goes to 18: the beta and
  dev channels of Chrome, Edge, Brave and Opera, Brave's and Vivaldi's alternate paths, and Chrome
  for Testing. Chromium compiles its native-messaging directory in and treats every channel as a
  separate product, so Chrome Beta and Edge Dev were reading a directory the package never wrote
  to.
- **Snap and flatpak browsers have a supported route.** Those are sandboxed away from `/etc`, so no
  system-wide registration can ever reach them. The packages now ship
  **`pdf-editor-host-register`**, which registers the host per-user across the plain `~/.config`,
  snap and flatpak layouts, prints the `flatpak override` command a flatpak browser needs, and
  removes what it wrote with `--uninstall`.
- **The Windows MSI installs where every instruction says it does.** It was built as a 32-bit
  package, so Windows Installer redirected it to `C:\Program Files (x86)` and put its registry keys
  under `WOW6432Node` — while the README, the extension's guidance and the installer's own
  registration script all pointed at `C:\Program Files\PDF Editor Host`. The MSI is now x64, and
  the build fails if it ever isn't.
- **The extension tells you when the host isn't there, and what to do about it.** A failed
  connection is now sorted into *not installed*, *installed but not allowed for this extension*,
  and *installed but won't start* — three states the browser reports in near-identical words that
  need completely different fixes — each with numbered, copyable, per-platform commands. The
  toolbar icon carries a badge while the host is unreachable, and the viewer's empty state says so
  where you hit it rather than only in a settings page you'd have to find.
- **The host reports a real version, and a mismatch is named before you hit it.** An out-of-date
  host answers a ping perfectly well and then does nothing useful for anything added since it was
  built. The options page now shows both versions and flags the mismatch.

## Improvements & fixes

- **Packages declare their runtime dependencies and self-test on install.** A self-contained .NET
  build still needs the system ICU and OpenSSL; without them the host exits immediately and the
  browser reports it exactly like a host that was never installed. The `.deb` declared no
  dependencies at all. All three Linux packages now run the host once at install time and print the
  exact remedy if it doesn't start.
- **`pdf-editor-host` is on your `PATH`.** The packages symlink the host into `/usr/bin`, which
  a sandboxed browser is far likelier to be permitted to execute, and which makes
  `pdf-editor-host --diagnostics` available without involving a browser at all.
- **Flatpak gets a path flatpak will actually share.** Flatpak reserves `/usr`, `/etc`, `/app`,
  `/dev` and `/proc` and refuses to bind-mount anything inside them, so a manifest naming
  `/usr/bin/pdf-editor-host` pointed a flatpak browser at a file it does not have — and the
  `flatpak override` command we shipped named a reserved path, which failed the whole command and
  granted nothing. Flatpak registrations now use `/opt`, which flatpak does not reserve.
- **A stale per-user manifest is diagnosed instead of blamed on your extension ID.** Chromium reads
  `~/.config/...` before `/etc`, so an old `install-host.sh <id>` keeps winning long after a correct
  package is installed. The extension now tells you to *remove* it; the previous advice — re-register
  for your ID — wrote the shadowing file rather than removing it.
- **Every shipped PowerShell script is invoked through an explicit execution-policy bypass.**
  Windows client editions default to `Restricted`, under which the commands we published were
  refused before their first line ran.
- **`register-host.ps1` ships inside the MSI.** It previously existed only in a source checkout,
  so the instructions pointed at a file an installer user did not have.
- **Copyable diagnostics carry the guidance too**, so a bug report includes the diagnosis rather
  than just the symptom.

## Release guards

None of this is observable without installing a package — the browser says
`Specified native messaging host not found.` whether the manifest is missing, in a directory that
browser doesn't read, or naming a path the package doesn't ship. So the release now proves it:

- `verify-linux-package.sh` unpacks each built `.deb` / `.rpm` / `.pkg.tar.zst` and checks the
  manifest is in every directory, identical across them, pinned to the right extension ID, and
  pointing at a path the package ships as an executable.
- `verify-msi.ps1` does the same for the MSI, including that it is a 64-bit package installing
  under the 64-bit Program Files.
- A CI job installs the real `.deb`, launches a browser with the extension loaded and **no**
  registration of its own, and renders a PDF through whatever the package left behind. This is
  what caught the missing Chrome for Testing directory.

All three run before artifacts are attached to a release.

## Installing / upgrading

- **From the Chrome Web Store:** update the extension, then reinstall the host package for your
  platform to pick up the wider browser coverage. The options page's **Native host** section will
  tell you if the two are out of step.
- **Snap or flatpak browser:** install the package for your distro, then run
  `pdf-editor-host-register` and follow the `flatpak override` line it prints.
- **Already installed the Windows MSI?** The old one installed to `C:\Program Files (x86)`. It
  still works there and every command we document finds either location; to move it, uninstall
  from *Apps & features* and install the 2.0.2 MSI.
- **Host connects but a feature does nothing?** That is the version mismatch this release added a
  check for — install the host package matching your extension version.

## Notes

- Firefox is still not covered — it uses a different native-messaging manifest format.
- The OS packages pin to the published Chrome Web Store extension ID. A developer-mode/unpacked
  build has a different ID: register it with
  `pdf-editor-host-register --extension-id <your-id>` (Linux) or
  `register-host.ps1 -ExtensionId <your-id>` (Windows).

**Full changelog:** #122 (since 2.0.1).
