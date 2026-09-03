# PDF backend portability: iText → OfficeIMO

Tracks **#115** (should the engine change) and **#116** (the abstraction seam).

This document has been through two investigation passes:

1. A README/NuGet-only pass (recorded in the [#115 comment
   thread](https://github.com/ZanattaMichael/reDACT-PDF-Editor/issues/115#issuecomment-5233486742)),
   which could not reach OfficeIMO's source and flagged content-stream redaction as an
   unresolved 🔴 risk.
2. A source-level pass (this document), which cloned
   [`EvotecIT/OfficeIMO`](https://github.com/EvotecIT/OfficeIMO) at `master`
   (commit `aba60b7b`, 2026‑09‑03) and read the redaction and text-editing
   implementations line by line, verified the published NuGet dependency graph, and
   measured PDF text-object granularity empirically against real Microsoft Word output.

**The second pass reversed the first pass's own initial optimism.** An earlier draft of
this file rated the content-stream work "🟡→🟢 by evidence of scale" — inferring
capability from the size and naming of OfficeIMO's redaction files rather than from
reading the algorithm. Reading the algorithm gives a different answer, recorded below.

## Headline finding

**The migration is clean for roughly 80% of the surface and blocked at the core.**

The licensing motivation is real and OfficeIMO delivers on it: verified from the
published `.nuspec` files, `OfficeIMO.Pdf` 3.3.0 depends on exactly one package
(`OfficeIMO.Core` 3.3.0), which itself declares **zero** dependencies on every target
framework. No transitive AGPL, no ImageSharp-style split license, nothing. Page
operations, merge/split, forms, encryption, signatures, JavaScript/action handling,
Bates numbering, watermarks, and Word→PDF import all map cleanly and in several cases
are an upgrade on what this project does today.

But `OfficeIMO.Pdf` redacts and edits text at **whole-text-object (`BT`…`ET`)
granularity**, where this project's `ContentStreamEditor` operates at **glyph**
granularity. That is an architectural difference, not a maturity gap, and it lands
squarely on this application's two differentiating features. Because a hybrid that
keeps iText for redaction still ships iText, a partial migration does **not** achieve
the AGPL goal — which is exactly the trap the original #115 comment warned about.

## Method

- Catalogued every `using iText.*` in `src/PdfEditor.Core` (28 of 40 files).
- Read OfficeIMO's actual redaction/editing implementation, not its documentation:
  `PdfRedactionApplier.TextScrubbing.cs`, `PdfRedactionApplier.cs`,
  `PdfRedactionPlan.cs`, `PdfRedactionVerification.Residue.cs`, `PdfTextEditor.cs`,
  `PdfDocumentRedactions.cs`, `PdfRedactionApplyOptions.cs`.
- **Verified licensing from the published artifacts**, not the README: downloaded
  `OfficeIMO.Pdf.3.3.0.nupkg` and `OfficeIMO.Core.3.3.0.nupkg` from nuget.org and read
  their `.nuspec` dependency groups directly.
- **Measured text-object granularity empirically.** Wrote a content-stream analyzer
  (decompress streams → enumerate `BT`…`ET` blocks → count characters, show-operators,
  and distinct baselines per block) and ran it against this repo's
  `docs/sample/PDF-Editor-Sample.pdf` and against real Microsoft Word–produced PDFs
  shipped as reference baselines in OfficeIMO's own test corpus.
- **Did not** compile or execute anything against OfficeIMO. There is no `dotnet` CLI
  in this container and the egress proxy rejects `builds.dotnet.microsoft.com`, so the
  SDK could not be installed. Findings below are from source reading plus static
  analysis of PDF bytes — strong evidence, but not a passing test.

## Licensing — verified clean ✅

```
OfficeIMO.Pdf 3.3.0  ──depends on──>  OfficeIMO.Core 3.3.0  ──depends on──>  (nothing)
```

Read from the packages' own `.nuspec` files. MIT license expression on both. The
optional `OfficeIMO.Security` package (CMS/RFC 3161/X.509 for certificate signing) is
**not** a transitive dependency — a build that doesn't sign never pulls it in. This is
a genuine, verified escape from iText's AGPL terms, and it is the strongest argument
in favour of the migration.

## The blocking finding: redaction granularity

### What this project does today

`ContentStreamEditor` subclasses iText's `PdfCanvasProcessor` and intercepts the
show-text operators (`Tj`/`TJ`/`'`/`"`). For a text run that only *partially* overlaps
a redaction rectangle, it splits at the **glyph** boundary: glyphs outside the region
are re-emitted unchanged, glyphs inside are replaced by an equivalent-width `TJ`
displacement so the surviving text keeps its original spacing and does not reflow
(`ContentStreamEditor.cs`, `HandleShowText`/`DisplacementFor`).

### What OfficeIMO does

`PdfRedactionApplier.TextScrubbing.cs` works one level up, on whole text objects:

- `BuildRedactionTextObject` computes **a single union bounding box for the entire
  `BT`…`ET` block** (`AddSpanBounds` takes `Math.Min`/`Math.Max` across every span in
  the object).
- `MarkMatchingTextObjects` → `IntersectsTarget` tests the redaction rectangle against
  that union box.
- On any intersection, `RemoveTextObjectSpans` **excises the entire `BT`→`ET` byte
  range** from the content stream. There is no re-emission of surviving glyphs and no
  spacing compensation.
- Nothing in `PdfRedactionApplyOptions` controls this granularity (its knobs are fill
  color, unmatched-area painting, image policy, path removal, and document-level
  cleanup scope).

### How much text that actually destroys

Measured, not assumed. One `BT`…`ET` block per line is the dominant pattern in real
output:

| Document | Text objects | Largest object |
|---|---|---|
| `docs/sample/PDF-Editor-Sample.pdf` (this repo's own sample) | 93 | 98 chars, ~1 baseline |
| `microsoft-word-16.109-native-word-report.pdf` | 73 | 89 chars, ~1 baseline |
| `microsoft-word-windows-word-business-delivery-summary.pdf` | ~136 per stream | 105 chars, ~1 baseline |

So the practical blast radius is **one whole line of text per redaction box**. A user
who draws a box over `John Smith` in the line

> Prepared by John Smith on 14 March for internal review

loses the entire line from the content stream, while the painted black box still covers
only the rectangle they drew.

### The mitigation, and why it doesn't fully rescue it

The text-editing path is smarter about this. `PdfTextEditor.RemoveTextPreservingUnmatchedSpans`
snapshots spans before, removes the whole text objects, re-reads the result, diffs which
*non-targeted* spans disappeared as collateral, and **re-stamps them back onto the
page**. That is how the README's "preserves unmatched source-span text" claim is
honoured — by delete-and-redraw, not by surgical retention.

Three consequences for this project:

1. **Font fidelity regresses.** The re-stamp resolves style through
   `ResolveStandardFont(...)` — the closest **standard PDF font** — and emits
   `BuildSubstitutionWarnings`. Text originally set in an embedded Calibri comes back
   as Helvetica. This is precisely the defect issue #29 was filed for and that
   `tests/PdfEditor.Core.Tests/TextFontFidelityTests.cs` now guards against.
2. **OCR'd scans fail closed.** `IsSafelyEditableSpan` requires
   `span.IsVisible && !span.ClipPath.HasValue && span.TextRenderingMode == 0 && ...`,
   and the editor throws `NotSupportedException` otherwise. A searchable scan's OCR
   layer is invisible text (`Tr 3`) by definition — so the text-edit path rejects the
   exact documents `OcrTool.MakeSearchable` produces and that
   `ContentKinds.TextAndPixelsBeneath` was built to handle. (The pure-redaction path
   does not throw; it removes the objects.)
3. **`Plan()` and `Apply()` use different geometry engines.** The planner
   (`PdfRedactionPlanner.cs:79`) walks `document.TextBlocks` from the logical read
   model, which uses real font metrics. The applier parses the raw content stream and
   approximates every glyph advance as a flat half-em —
   `SumWidth1000 = bytes.Length * 500D` (`PdfRedactionApplier.TextScrubbing.cs:525`).
   Preview and effect are therefore computed two different ways, which matters a great
   deal for a tool whose compliance story is "here is exactly what was removed" (#48).
   Under-estimated widths are also the one path by which this design could *under*-redact
   rather than over-redact; worth explicit testing if the migration proceeds.

### On the verification API

`Redactions.Verify`/`AssertVerified` is real and useful, but it is **marker-based**:
`ContainsEncodedPdfMarker`/`ContainsDecodedStreamMarker` search the output bytes for
strings the caller names. It proves "the string I told you about is gone." It is not a
geometric proof that a rectangle is free of extractable content, so it complements
rather than replaces this project's own assertions.

## Capability mapping

Organized by the seams #116 proposes. 🟢 direct equivalent verified in source ·
🟡 exists, needs a fidelity check · 🔴 architectural gap.

### `IPdfDocumentStore` — 🟢 clean port

| Today | OfficeIMO.Pdf |
|---|---|
| `PdfIo.Open`/`OpenReadOnly` | `PdfDocument.Load(...)`, `Save`/`SaveAsync` |
| `Merger.Merge` | `MergeWith`/`MergeWithReport`, per-source passwords and permission policy |
| `PageTools.Arrange` | `Pages.Extract`/`Split`/`Delete`/`Duplicate`/`Move` with range selectors |
| `PageTools.Rotate` | `Pages.Rotate(degrees, ranges)` |
| `DocumentImport.ImageToPdf` | `PdfDocument.Create` + image content block |
| `DocumentImport.DocxToPdf` (spawns LibreOffice) | `WordDocument.Load(...).SaveAsPdf(...)` in-process — **removes the external `soffice` dependency entirely** |

### `IInspector` — 🟢/🟡

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `PdfInspector.GetInfo` | `Inspect()` page geometry/count | 🟢 |
| `Encryptor.IsEncrypted`/`CanOpen` | `PdfLoadOptions.Password`, `PdfDocumentSecurityInfo` | 🟢 |
| `ExportValidator.Validate` | `Analyze(...)` + `PdfRepairReport` | 🟡 different finding shape; needs an adapter |
| `PdfStructureGuard`/`PdfContentGuard` | Parser-level bounded limits (`PdfReadLimits`: max operations, nesting depth, operands, decoded bytes) | 🟢 likely **deletable** — becomes the library's job |
| `Sanitizer.Inspect` | No single equivalent; composable from `Inspect()`/`JavaScript.List()`/`Attachments`/`Annotations`/`Bookmarks` | 🟡 |

### `ITextEditor` / `IContentStreamEditor` — 🔴 the blocker

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `TextTools.GetTextInRegion`/`GetTextSpans` | `Text.Inspect(region)`, public `PdfReadPage.GetTextSpans()` → `PdfTextSpan` with position/font/size/transform | 🟢 |
| `TextTools.FindText` | `Text.Find(...)` with case/whole-word filters | 🟢 |
| `TextTools.AddText` | `Text.Add(region, text, PdfTextEditOptions)` | 🟢 |
| `TextTools.ReplaceTextInRegion`, `MoveText` | `Text.Replace`/`Add` — but via whole-text-object removal + re-stamp in a **substituted standard font**; throws on invisible/clipped text | 🔴 regresses #29 font fidelity; rejects OCR'd scans |
| **`ContentStreamEditor`** (per-glyph split, width-compensated `TJ`, form-XObject recursion, inline images) | Whole-`BT`…`ET` excision against a union bbox with flat half-em width estimates | 🔴 **architectural gap** |
| `ImageTools.MoveImage` | `Images.Move(placement, dx, dy)` | 🟢 |
| `ImageScrubber.TryScrubPixels` | `PdfRedactionApplier.Images.PixelRewrite.cs`; JPEG/masked/indexed cases need a caller-supplied `IPdfRedactionImageDecoder` or fail closed | 🟡 |

### `IRedactionEngine` — 🔴 core, 🟢 periphery

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `Redactor.Redact` content removal | `Redactions.Apply(areas, options)` — correct security posture (errs toward removing more), wrong granularity for an interactive editor | 🔴 |
| `RedactionBox.Prepare`/`Expand` | `Redactions.Plan(areas)` non-mutating preview | 🟢 — better than today; no dry-run exists now |
| `RedactionReporter.Analyze` | `PdfRedactionMatch` carries `Text`, `Kind`, `ImagePlacement`, page/rect | 🟡 — rebuildable, but plan/apply geometry mismatch (above) undermines "exactly what was removed" |
| *(nothing today)* | `Redactions.Verify`/`AssertVerified` marker-based residue proof | 🟢 net-new, worth adopting regardless |

### `IAnnotationEngine` / `IFormEngine` / `ISecurityEngine` / `ISigningEngine` — 🟢/🟡 clean

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `HighlightTool` (`Multiply` blend) | `Stamp.Content` canvas; blend mode used internally, not clearly public | 🟡 upstream draft #1 |
| `InkTools`, `WatermarkTool` | `Stamp.Content` shapes/drawings; `Stamp.TextWatermark`, `PdfOptions.TextWatermark` | 🟢 |
| `BatesTool` | `PdfBatesNumberer` with prefix/digits/position — a first-class feature | 🟢 |
| `FormTools` add/list/fill | `Forms.Edit(...)`/`Forms.FillAndFlatten(...)`; per-field JS on non-button fields unconfirmed | 🟢/🟡 upstream draft #2 |
| `FlattenTool` 3 modes | `Forms.FillAndFlatten` + `Annotations.Flatten` | 🟢 |
| `Encryptor` | `PdfStandardEncryptionOptions` (AES-256/128, RC4, permissions) | 🟢 |
| `JavaScriptTool` | `JavaScript.List()`/`.Edit(...)` — near 1:1 | 🟢 |
| `PdfSafety`, `UrlTools` | Sanitizer + `Links`; action-subtype granularity unconfirmed | 🟡 upstream drafts #3/#4 |
| `Signer.SignDigitally`/`GetSignatures` | `Security.SignExternal(...)`, `ValidateSignatures(...)` → `PdfSignatureValidationReport` | 🟢 — resolves the earlier 🔴 |
| `CertificateFactory` | No iText dependency today; unaffected | 🟢 no work |

### Unaffected

`PageRenderer`/`VisualDiff` (PDFium + SkiaSharp), `DocComparer` (LCS over extracted
text), `OcrTool` (Tesseract; but see the invisible-text finding), `UrlClassifier`.

## Revised recommendation

1. **Phase 0 — build the seam (#116). Unchanged, and still worth doing on its own.**
   It isolates the iText surface, makes the tools testable against fakes, and is the
   only way to migrate the 🟢 majority without a big-bang rewrite.
2. **Do not plan a full engine swap on today's OfficeIMO.** The 🟢 items are genuinely
   ready, but migrating them alone leaves iText in the tree for redaction and text
   editing, so the AGPL goal — the entire point of #115 — is not met. Migrating the
   core as-is would trade an AGPL problem for a product-quality regression: losing a
   line of text per redaction box, standard-font substitution on every edit, and
   `NotSupportedException` on OCR'd scans.
3. **Open the upstream conversation now** (drafts in
   [`docs/officeimo-issues-to-file.md`](officeimo-issues-to-file.md)). Draft #5 is the
   one that decides this project's outcome: sub-text-object redaction granularity,
   and/or public content-stream tokenizer + writer primitives so a per-glyph editor can
   be built on an MIT base. OfficeIMO already has the machinery internally
   (`PdfContentStreamInterpreter`, `TextContentParser`, font decoders, real metrics in
   the read model) — it is `internal`, not absent. This is a plausible ask, not a
   rewrite request.
4. **Re-evaluate when that lands.** If OfficeIMO exposes sub-object granularity or the
   primitives, the migration becomes attractive across the board and Phase 0 will have
   made it incremental. If it does not, the honest answer to #115 is **stay on iText**
   and keep the seam as the net win.
5. **Adopt one idea regardless of the outcome:** a post-redaction residue assertion in
   the spirit of `Redactions.Verify`. This project makes a removal guarantee (#48) and
   currently has no independent post-hoc check that the bytes are actually gone.

## Caveats

- Source reading and PDF byte analysis, not execution. The granularity finding is
  structural and I consider it solid (it follows from the shape of the algorithm, not
  from a judgement call about quality), but the *severity* estimates — how often a
  producer emits multi-line text objects, whether the half-em width approximation ever
  causes under-redaction — deserve a compiled test before anyone acts on them.
- Everything here describes `OfficeIMO.Pdf` 3.3.0 / `master` at `aba60b7b`. This is a
  fast-moving library (3.2.x → 3.3.0 moved several public engine classes to `internal`
  per its `MIGRATION.md`); re-check before relying on any specific API shape.
- The flip side of that churn is a maintenance consideration this project should weigh
  independently of features: a security-critical removal guarantee resting on a young,
  rapidly-changing library with a small maintainer team is a different risk profile
  from iText's two decades of hardening — even though iText's licence is the reason
  this exercise exists at all.
