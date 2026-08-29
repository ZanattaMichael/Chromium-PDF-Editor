# PDF Editor 2.0.3

A small release with no changes to editing. 2.0.3 drops a browser permission the extension never
needed, and fixes the version number the host reports about itself.

## Highlights

- **The extension no longer asks for the `tabs` permission.** It was never required. The two tabs
  APIs the extension calls — opening the editor in a new tab, and redirecting a PDF navigation into
  it — need no permission at all, and the one piece of tab information it reads (the address of the
  PDF you asked it to open) is already covered by the site access it holds. Chrome was being asked
  for a capability that went unused. Everything behaves exactly as before; the extension simply
  requests less.
- **The host reports its real version.** The version number was stamped into the extension at
  release time but never into the host binary, so the 2.0.2 packages were *named* 2.0.2 while the
  host inside them still answered **2.0.0** — on the options page, to `pdf-editor-host --version`
  and in `--diagnostics`. The release now stamps both from the same tag.

## About that version number

If you installed the 2.0.2 host, its **Native host** section reads `Host v2.0.0` next to
`extension v2.0.2`. Nothing is wrong with that install: it is a correct 2.0.2 host that
misreports its own version, and the mismatch check compares major and minor only, so it was never
flagged. Installing the 2.0.3 package corrects the number.

The reason it is worth a release rather than a note: the comparison is what puts a *"your host is
out of date"* warning in front of users, and at the next minor version a host that under-reports
itself by two patch levels would start failing that check while being perfectly current.

## Installing / upgrading

- **From the Chrome Web Store:** the extension updates itself. Because 2.0.3 *removes* a
  permission and adds none, Chrome applies the update silently — there is no prompt to accept and
  nothing to re-approve.
- **Host package:** optional. A 2.0.2 host works correctly with the 2.0.3 extension; reinstall only
  if you want the reported version to be right. Anyone installing fresh gets the correct number.
- **Coming from 2.0.1 or earlier:** install the host package for your platform — 2.0.2 is the
  release that fixed native-host detection on Linux and moved the Windows MSI to
  `C:\Program Files\PDF Editor Host`. See the [2.0.2 notes](RELEASE_NOTES_2.0.2.md).

## Notes

- Firefox is still not covered — it uses a different native-messaging manifest format.
- The OS packages pin to the published Chrome Web Store extension ID. A developer-mode/unpacked
  build has a different ID: register it with
  `pdf-editor-host-register --extension-id <your-id>` (Linux) or
  `register-host.ps1 -ExtensionId <your-id>` (Windows).

**Full changelog:** #123 (since 2.0.2).
