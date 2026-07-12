# A Chromeium PDF Editor

A free PDF editor extension for Chromium browsers (Chrome, Edge, Brave, Chromium, Opera).
The UI is a Manifest V3 browser extension; all document processing is done in **C#/.NET 8**
by a local [native messaging host](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging),
so your documents never leave your machine.

## Features

| Feature | Notes |
| --- | --- |
| ✏ **Edit existing text** | Drag a box around text, edit it in place. The original text operators are removed from the file and the new text is stamped into the same region. Includes document-wide find & replace. |
| ⬛ **Redaction** | Draw boxes over anything. A **preview window** shows the result before you commit. Applying **permanently removes** the content underneath (text operators, image pixels, annotations) — not just covers it — then paints an opaque black box. |
| 💾 **Save changes** | Save the edited document via the file picker or the downloads bar. Undo history while editing. |
| 🧭 **Sits on top of browser PDF viewing** | Navigating to a `.pdf` opens the editor automatically (toggleable). Embedded PDF viewers on web pages get an “Edit in PDF Editor” overlay button, plus a toolbar button and right-click menu. Adobe sites and viewers are always left alone. |
| 🔒 **Password protection** | AES-256 encryption with user/owner passwords; open, edit, and decrypt protected files. |
| ➕ **Merge** | Append any number of PDFs to the open document, including encrypted sources. |
| 🖋 **Electronic signatures** | Draw a signature on a pad (or upload an image) and place it anywhere; or apply a cryptographic **digital signature** from a PKCS#12 certificate — the editor can also generate a self-signed certificate for you. Signature validity is verified and shown in the status bar. |

## Architecture

```
┌───────────────────────────── Chromium browser ─────────────────────────────┐
│  content.js ──── overlay button on built-in/embedded PDF viewers           │
│  background.js ─ intercepts *.pdf navigation, context menus, toolbar       │
│  viewer.html/js ─ toolbar UI, page canvas, region drawing, preview modal   │
│        │  chrome.runtime.connectNative (JSON, 4-byte LE framing, chunked)  │
└────────┼────────────────────────────────────────────────────────────────────┘
         ▼
  PdfEditor.NativeHost (C# console app, stdio)
         │ dispatch (MessageProcessor, ChunkAssembler)
         ▼
  PdfEditor.Core (C# class library)
    ├─ Redactor / ContentStreamEditor  — true content removal
    ├─ TextTools                       — region text read/replace, find & replace
    ├─ Merger, Encryptor, Signer, CertificateFactory
    └─ PageRenderer (PDFium)           — page PNGs for the viewer
```

The host is **stateless**: every request carries the document as base64 and returns the
transformed document. Host→browser responses over Chrome's 1 MB limit are split into
chunk frames and reassembled by the extension (and vice versa for large requests).

### How redaction really removes content

`ContentStreamEditor` re-parses the page content stream and rewrites it operator by
operator:

- **Text** — every text-showing operator (`Tj`, `TJ`, `'`, `"`) is re-emitted in `TJ`
  form with per-glyph granularity: glyphs inside a redaction region are replaced by an
  equivalent-width kerning displacement, so the hidden text is gone from the file while
  surrounding words keep their exact positions.
- **Images** — image XObjects fully inside a region are dropped; partially covered
  images are decoded, their covered pixels painted black, and re-encoded (the original
  pixel data is destroyed). Inline images touching a region are dropped.
- **Form XObjects** — recursively edited on a cloned copy with the regions transformed
  into form space, so shared forms on other pages are unaffected.
- **Annotations** — links/widgets intersecting a region are removed.

Then an opaque black box is painted on top. Text extraction on the output confirms the
content is unrecoverable — that's exactly what the test suite asserts.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build            # build everything
dotnet test             # run the full suite (52 tests: 37 unit + 15 integration)
```

The integration tests launch the real host binary and speak Chrome's framed protocol
over stdin/stdout — ping, chunked requests/responses, and full user workflows
(edit → redact → merge → encrypt → sign) are exercised end to end.

## Installing

1. **Generate the icons** (one-time, standard-library Python only):

   ```bash
   python3 scripts/generate-icons.py
   ```
2. **Load the extension**: `chrome://extensions` → enable *Developer mode* →
   *Load unpacked* → select the `extension/` folder. Note the extension ID.
3. **Install the native host**:

   ```bash
   # Linux / macOS
   ./scripts/install-host.sh <extension-id>

   # Windows (PowerShell)
   .\scripts\install-host.ps1 -ExtensionId <extension-id>
   ```

   This publishes the .NET host and registers `com.pdfeditor.host` for Chrome,
   Chromium, Edge, and Brave.
4. Restart the browser. The extension's options page shows the host connection status.

## Usage notes

- **Redaction preview**: draw boxes with the ⬛ tool, then *Preview* renders the result
  in a window before anything is committed. *Apply* performs the destructive removal.
- **Text editing**: with the ✏ tool, drag around the text you want to change; the
  editor pre-fills what it found there. Replacement text is stamped in Helvetica at a
  matched size (font-exact reproduction of arbitrary embedded fonts is not attempted).
- **Sign then don't rewrite**: a digital signature is invalidated by any subsequent
  full rewrite — encrypt **before** signing (the UI warns about this ordering).
- Files opened from `file://` URLs need *Allow access to file URLs* enabled for the
  extension, or just use the 📂 Open button.

## Repository layout

```
extension/                    Chromium MV3 extension (UI)
src/PdfEditor.Core/           PDF engine (iText 9 + PDFium rendering)
src/PdfEditor.NativeHost/     native messaging host executable
tests/PdfEditor.Core.Tests/   unit tests
tests/PdfEditor.IntegrationTests/  process-level protocol & workflow tests
scripts/                      host install scripts + manifest template
```

## License

GPL-3.0 (see `LICENSE`). Uses [iText Core](https://github.com/itext/itext-dotnet) (AGPL)
and [PDFtoImage](https://github.com/sungaila/PDFtoImage)/PDFium for rendering.
