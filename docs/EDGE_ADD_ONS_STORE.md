# Publishing to the Microsoft Edge Add-ons store

This project automates the upload the same way it does for Chrome (see
[`CHROME_WEB_STORE.md`](CHROME_WEB_STORE.md)): `.github/workflows/release-extension.yml` packages
`extension/` into a store-ready zip and, on a published (non-prerelease) GitHub Release, uploads
**and publishes** it to the Edge Add-ons store via Microsoft's REST API (v1.1) — plain `curl` calls
in the `publish-to-edge-add-ons-store` job, not a third-party package or Action, matching how the
Chrome step avoids one too. What that automation needs is three repo secrets. This document is the
checklist for that, and for what is deliberately *not* automated.

---

## 0. One-time account + registration — done

- [x] **Registered.** Store ID `0RDCKF3D4DH2`, extension ID (CRXID)
      `kcppllhgnfmdbmglohgmabipeikopfhb`, committed at `scripts/edge-extension-id.txt` and used
      everywhere the native-messaging packages pin `allowed_origins` to it (alongside Chrome's ID).

If you ever need to redo this from scratch: create/enter a **Microsoft Partner Center developer
account** (one-time US$19 fee) at <https://partner.microsoft.com/dashboard/registration>, then
submit the packaged zip once (`./scripts/package-edge-extension.sh <chrome-zip> dist` — see
below for why it needs the Chrome zip, not `extension/` directly) to register the item and obtain
its **Product ID**, **Store ID** and **extension (CRX) ID**. Update
`scripts/edge-extension-id.txt` to the new CRXID, and re-run
`node --test "extension/test/**/*.test.mjs"` — `host-install.test.mjs` cross-checks it against
`extension/src/host-install.js`'s `EDGE_PINNED_EXTENSION_ID`.

## 1. Why the Edge package differs from the Chrome one

`extension/manifest.json` carries a `"key"` field, and it has to stay: it is what makes an
**unpacked** load of this extension get the exact same ID as the published Chrome Web Store build,
in every Chromium-based browser (Edge included) — see the README's "Browser end-to-end tests"
section and `extension/src/host-install.js`. Removing it would silently break that, and the
`package-install-e2e` suite along with it.

Edge's own submission validator, though, **rejects any package whose `manifest.json` has a `"key"`
property at all** — Edge already knows this extension's ID from when it was first registered (see
above), so the field would be redundant even if Edge allowed it.

Both are satisfied by never editing the source manifest at all: `scripts/package-edge-extension.sh`
takes the already-built Chrome zip (from `package-extension.sh`) and re-packages it with `"key"`
stripped from the copy — so the two zips are byte-for-byte identical except for that one line, and
can never drift apart in any other way. `release-extension.yml`'s `package` job builds both from
the same run.

## 2. API credentials for automated publishing

The workflow needs three repo secrets, from Partner Center's **Publish API** access page
(<https://partner.microsoft.com/dashboard/microsoftedge/> → your extension → **API access**):

1. Generate a **Client ID** and **API Key** (Partner Center's v1.1 API auth — just two header
   values, no OAuth token exchange, unlike Chrome's flow or Edge's older v1 API).
2. Note the **Product ID** from the extension's Partner Center overview page (`2219de73-...` —
   *not* the Store ID or the CRXID; the API addresses submissions by Product ID).
3. Add these as GitHub **repository secrets** (they gate the `publish-to-edge-add-ons-store` job):
   - `EDGE_PRODUCT_ID`
   - `EDGE_CLIENT_ID`
   - `EDGE_API_KEY`

   > If any secret is missing, the job skips gracefully and leaves the packaged zip as a build
   > artifact for manual upload (see the workflow's "Check … credentials are configured" step).

**Not secrets, and not needed by the pipeline at all:**
- The **Store ID** (`0RDCKF3D4DH2`) only matters for the public listing URL
  (`https://microsoftedge.microsoft.com/addons/detail/0RDCKF3D4DH2`); nothing in CI reads it.
- The **public key** Partner Center showed alongside the CRXID at registration has no operational
  role anywhere — Edge's validator forbids `"key"` in the manifest, so nothing ever embeds it. It
  was useful for one thing only: confirming the CRXID Partner Center reported really does hash from
  that key (standard Chromium extension-ID algorithm — SHA-256 of the DER-encoded key, first 16
  bytes, nibble-mapped to a-p), which is how `scripts/edge-extension-id.txt` was verified before
  being committed.

## 3. Store listing content

Reuse the same summary/description/category as the Chrome listing (see
[`CHROME_WEB_STORE.md`](CHROME_WEB_STORE.md) §3) — there is no reason for the two stores to say
something different about the same extension. Same graphics too; Partner Center accepts the same
1280×800 screenshots and 128×128 icon Chrome does.

## 4. Permission justifications

Same permissions, same justifications as [`CHROME_WEB_STORE.md`](CHROME_WEB_STORE.md) §4 — Edge is
Chromium-based and reads the same `manifest.json`. Partner Center's review form asks for them in a
different shape than the Chrome dashboard does, but the answers do not change.

## 5. Publish

Two paths, mirroring Chrome's:

- **Automated:** publish a **non-prerelease GitHub Release** whose tag sets the version (e.g.
  `v2.0.0`). The workflow packages, uploads and publishes to both stores. (Release-candidate tags
  are prereleases and stop before either store step.)
- **Manual:** download the `pdf-editor-extension-edge-v<version>.zip` build artifact (or run
  `./scripts/package-extension.sh dist && ./scripts/package-edge-extension.sh dist/pdf-editor-extension-v<version>.zip dist`)
  and upload it in Partner Center, then submit for certification.

## 6. After first submission

- Certification review is typically faster than Chrome's, but native-messaging + broad host access
  still tend to prompt reviewer questions — answer with the justifications above and the
  native-host explanation (the host is **not** distributed through the Add-ons store; link to the
  install scripts / bundle).
- Bump `manifest.json` `version` for every subsequent submission, same as Chrome (the release tag
  stamps it in CI, once, for both packages).

## 7. Not done here: native messaging for Edge users

The host is already registered into Edge's own OS-specific native-messaging locations by every
installer (`HKCU:\...\Microsoft\Edge\...` on Windows, `/etc/opt/edge*/native-messaging-hosts` on
Linux — see `installer/windows/PdfEditorHost.wxs` and `scripts/linux-manifest-dirs.sh`), and the
manifest's `allowed_origins` lists both the Chrome and Edge extension IDs (see
`scripts/com.pdfeditor.host.json.template`), so a user who installs the OS package and the
published Edge extension should already be able to connect. This document is scoped to the store
listing and its automation, not to auditing that end-to-end connection on a real Edge install —
worth doing once Edge's listing is actually live.
