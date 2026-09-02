# Privacy Policy — reDACT PDF Editor

_Last updated: 2026-08-31_

> Fill in the **Developer** and **Contact** placeholders below, host this page at a public
> URL (e.g. GitHub Pages), and enter that URL in the Chrome Web Store listing. A privacy
> policy URL is **required** for this extension because it requests broad host access.

## The short version

reDACT edits your PDFs **on your own computer**. The document content is processed by a
**native host** that runs locally on your machine — it is never uploaded to us or to any server
we control. We do **not** operate any backend, do **not** run analytics or telemetry, and do
**not** create accounts or collect personal information.

## What the extension accesses, and why

- **Your PDFs and images.** Files you open, edit, redact, sign, merge, etc. are handled locally
  by the native host installed on your device. Their content is not transmitted to the developer.
- **Pages you navigate to (host access).** The extension detects PDFs in the browser so it can
  offer to open them in the editor. It reads only enough of the page to recognise a PDF and to
  place its "Edit in reDACT" control; it does not read or transmit page content otherwise.
- **Downloads.** Used to save your edited PDFs and reports to your computer, at your request.
- **Local settings storage.** Preferences (such as the activity-console state and any Cloudflare
  credentials you choose to enter) are stored in your browser's local extension storage on your
  device. They are not sent to the developer.

## Network activity

- **Opening a PDF by URL.** When you open a PDF that lives at a web address, the extension
  fetches that file from that address (the same request your browser would make) so it can be
  edited. This goes to the site hosting the PDF, not to the developer.
- **Optional link-safety scanning (off unless you enable it).** If — and only if — you enter your
  **own** Cloudflare URL Scanner credentials in the extension's options, the extension will send
  the **link URLs found inside a document** to Cloudflare's URL Scanner API to classify them as
  safe/suspicious. This is entirely opt-in; without your credentials, links are assessed locally
  with a heuristic and nothing leaves your device. Data you send this way is governed by
  Cloudflare's own privacy terms.

Apart from the two cases above, the extension does not send your data anywhere.

## Data we collect

**None.** We do not collect, store, sell, or share personal data. There is no server component
operated by the developer.

## Children's privacy

The extension is a document tool and is not directed at children; it collects no personal data
from anyone.

## Changes

If this policy changes, the "Last updated" date above will change and the new version will be
published at the same URL.
