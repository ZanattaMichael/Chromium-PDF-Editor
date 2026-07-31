# Documentation assets

This folder holds a sample document and a set of feature screenshots used across the README and
other docs. Everything here is **generated and reproducible** — no manual editing.

## Sample document

[`sample/PDF-Editor-Sample.pdf`](sample/PDF-Editor-Sample.pdf) is a small, realistic two-page
"Quarterly Business Review": a titled cover page with body copy, a bar chart, a pie chart and an
embedded raster logo, followed by a gridded data table and a callout box. It deliberately mixes
flowing text, vector graphics, a real image and hidden document metadata so a single file
exercises the whole editor — redaction, highlighting, inline text editing, move, forms, "remove
hidden information", and the rest.

Regenerate it with:

```bash
node scripts/generate-sample-pdf.mjs
```

## Feature screenshots

Captured by driving the real extension and native host against the sample document. Regenerate
the whole set (this builds the native host and launches Chromium with the extension loaded):

```bash
cd e2e
npm install            # first time only
node scripts/doc-shots.js
```

| Feature | Screenshot |
| --- | --- |
| Open a document — text, charts and images render together | ![Overview](screenshots/01-overview.png) |
| Redact — mark a region… | ![Redaction marked](screenshots/02-redact-marked.png) |
| …and preview exactly what will be removed before committing | ![Redaction preview](screenshots/03-redact-preview.png) |
| Highlight — sweep across text | ![Highlight](screenshots/04-highlight.png) |
| Add text anywhere on the page | ![Add text](screenshots/05-add-text.png) |
| Edit existing text in place (font and size are recovered) | ![Edit text](screenshots/06-edit-text.png) |
| Find & replace across the whole document | ![Find and replace](screenshots/07-find-replace.png) |
| Fillable forms — fill existing fields and build new ones | ![Forms](screenshots/08-forms.png) |
| Organize pages — reorder or delete | ![Organize pages](screenshots/09-organize.png) |
| Remove hidden information before sharing | ![Remove hidden information](screenshots/10-remove-hidden-info.png) |
| Draw freehand | ![Draw](screenshots/11-draw.png) |
| Electronic signatures — draw or upload, then place | ![Sign](screenshots/12-sign.png) |
| Activity console — every host action, timing and error | ![Activity console](screenshots/13-activity-console.png) |
