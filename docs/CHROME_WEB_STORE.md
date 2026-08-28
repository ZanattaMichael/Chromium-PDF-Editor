# Publishing to the Chrome Web Store

This project already automates the upload: `.github/workflows/release-extension.yml` packages
`extension/` into a store-ready zip and, on a published (non-prerelease) GitHub Release, runs
`chrome-webstore-upload-cli` to upload **and publish**. What that automation still needs is a
registered extension, four API secrets, and a completed store listing. This document is the
checklist for all of that.

---

## 0. Before you start — things to decide/fix

- [ ] **Name.** `"PDF Editor"` in `extension/manifest.json` is very generic; the Web Store often
      rejects generic/already-existing names. Pick a distinctive name (e.g. a brand word +
      "PDF Editor"). Update `manifest.json` `name` and the store listing to match.
- [ ] **Privacy policy hosted.** Host `docs/PRIVACY.md` at a public URL (GitHub Pages works) and
      have that URL ready — it is **required** for this listing (broad host access).
- [ ] **Native host reality.** This extension needs a separately-installed native messaging host
      to do the actual PDF work. The host is **not** distributed through the Web Store; the listing
      and the review notes must explain how users install it (link to the install scripts / bundle).

## 1. One-time account + registration

1. Create/enter a **Chrome Web Store developer account** (one-time US$5 fee) at
   <https://chrome.google.com/webstore/devconsole>.
2. Upload the packaged zip once (from a build artifact or `./scripts/package-extension.sh`) to
   **register the item** and obtain its **Extension ID**. (You can save it as a draft.)

## 2. API credentials for automated publishing

The workflow needs four repo secrets. Get them via the Chrome Web Store API setup
(<https://developer.chrome.com/docs/webstore/using-api>):

1. In **Google Cloud Console**: create a project, enable the **Chrome Web Store API**, and create
   an **OAuth client ID** of type *Desktop app* → gives `CLIENT_ID` and `CLIENT_SECRET`.
2. Run the one-time OAuth consent flow to get a **`REFRESH_TOKEN`** for that client.
3. Add these as GitHub **repository secrets** (they gate the `publish-to-chrome-web-store` job):
   - `CHROME_EXTENSION_ID`
   - `CHROME_CLIENT_ID`
   - `CHROME_CLIENT_SECRET`
   - `CHROME_REFRESH_TOKEN`

   > If any secret is missing, the job skips gracefully and leaves the packaged zip as a build
   > artifact for manual upload (see the workflow's "Check … credentials are configured" step).

## 3. Store listing content

Paste these into the Developer Dashboard listing.

**Summary (≤132 chars):**
> Edit, redact, merge, sign and password-protect PDFs in your browser. Local processing via a
> native host — your files never leave your device.

**Description:** adapt the feature list from the README. Lead with the single purpose (a PDF
editor), then the highlights (edit text, true redaction with a compliance report, watermark,
Bates numbering, forms, OCR, signing, compare). **State clearly** that a free native host must be
installed and link to the install instructions.

**Category:** Productivity. **Language(s):** as applicable.

**Graphics required by the store:**
- [ ] Store icon 128×128 (already in `extension/icons/icon128.png`).
- [ ] At least one **screenshot 1280×800** (or 640×400). The generated shots in
      `docs/screenshots/` are a starting point but are captured at a different size — recapture at
      1280×800 (adjust the viewport in `e2e/scripts/doc-shots.js`) or crop/pad to the required size.
- [ ] Optional: small promo tile 440×280.

**Single purpose statement:**
> A PDF editor: it lets the user view and edit PDF documents (text, redaction, forms, pages,
> signatures) via a local native host.

## 4. Permission justifications (the review will ask for each)

| Permission | Why it's needed |
| --- | --- |
| `nativeMessaging` | All PDF processing runs in a local native host; this is how the extension talks to it. |
| `downloads` | Save edited PDFs and the redaction/compliance reports to the user's computer. |
| `storage` | Persist user preferences (activity-console state, optional Cloudflare credentials) locally. |
| `contextMenus` | Right-click actions (Edit, Redact, Highlight, etc.) on selected text and pages. |
| `webNavigation` / `tabs` | Detect when the user navigates to a PDF so the editor can offer to open it, and open the editor in a tab. |
| `host_permissions: <all_urls>` | PDFs can live on **any** site; the content script must run everywhere to detect them and show the "Edit in PDF Editor" control. It reads only enough to recognise a PDF; it does not collect page content. |

> **Minimise if you can.** `<all_urls>` + `tabs` + `webNavigation` draw the most scrutiny. If the
> "detect a PDF on any page" feature can tolerate `activeTab`/narrower matches, consider it — but
> the current auto-detect-anywhere behaviour does need broad access, so justify it as above.

## 5. Data-use disclosures (Dashboard "Privacy practices" tab)

- **Personal/most data types:** *not collected.*
- Certify: not sold, not used for unrelated purposes, not used for creditworthiness/lending.
- If you ship the **optional Cloudflare link-scanning** enabled-by-config, disclose that a user who
  enters their own Cloudflare credentials sends document link URLs to Cloudflare (see `PRIVACY.md`).
- Provide the hosted **privacy policy URL**.

## 6. Publish

Two paths:

- **Automated:** publish a **non-prerelease GitHub Release** whose tag sets the version (e.g.
  `v2.0.0`). The workflow packages, uploads and publishes to the store. (Release-candidate tags are
  prereleases and stop before the store step.)
- **Manual:** download the `pdf-editor-extension-v<version>.zip` build artifact (or run
  `./scripts/package-extension.sh`) and upload it in the Dashboard, then click Publish.

## 7. After first submission

- Review can take days; native-messaging + broad host access may prompt reviewer questions —
  answer with the justifications above and the native-host explanation.
- Bump `manifest.json` `version` for every subsequent submission (the release tag stamps it in CI).
