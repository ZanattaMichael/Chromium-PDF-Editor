# PDF backend portability: iText → OfficeIMO

Tracks **#115** (should the engine change) and **#116** (the abstraction seam). This
document supersedes the README-only investigation recorded in the [#115 comment
thread](https://github.com/ZanattaMichael/reDACT-PDF-Editor/issues/115#issuecomment-5233486742):
that pass could not reach OfficeIMO's source (`api.github.com` was blocked from that
session) and had to guess at the 🔴 items from documentation alone. This pass cloned
[`EvotecIT/OfficeIMO`](https://github.com/EvotecIT/OfficeIMO) at `master`
(commit `aba60b7b`, 2026‑09‑03) and read the actual implementation behind every
capability this project depends on.

## Headline finding

**The migration is very likely viable**, including the content-stream-level
guarantees that were the previous investigation's open question. `OfficeIMO.Pdf`
3.3.0 (published to NuGet the same day as the cloned commit) ships a first-party,
dependency-free PDF engine with a purpose-built redaction subsystem
(`OfficeIMO.Pdf/Manipulation/PdfRedaction*.cs`, ~5,200 lines) that plans, applies, and
*independently verifies* rectangle-based redaction — removing intersecting text runs,
paths, annotations, form fields, and image pixels, then re-opening the output to prove
the removed content is gone. That is not a coincidence: OfficeIMO's own current-state
docs describe redaction, forms, signatures, and content-level text/image editing as
core, tested workflows, not aspirational ones.

This does **not** mean the migration is risk-free — see [Caveats and what
remains unverified](#caveats-and-what-remains-unverified) — but it means the
project should proceed past Phase 0 rather than conclude "stay on iText."

## Method

- Read `src/PdfEditor.Core/*.cs` in this repo and catalogued every `using iText.*`
  (28 files use iText directly; `CertificateFactory.cs`, `OcrTool.cs`,
  `DocumentImport.cs`'s Word path via LibreOffice, `Models.cs`, `ValidationModels.cs`,
  `UrlClassifier.cs`, `PageRenderer.cs`, and `VisualDiff.cs` do not).
- Cloned `EvotecIT/OfficeIMO` read-only and read the actual source under
  `OfficeIMO.Pdf/` (not just its README) for every capability area this project
  touches, including the internal `PdfRedactionApplier*`, `PdfRedactionPlanner*`,
  and `PdfRedactionVerification*` implementation files.
- Confirmed NuGet publication: `OfficeIMO.Pdf` and `OfficeIMO.Core` are both published
  through `3.3.0`, MIT-licensed, targeting `netstandard2.0;net8.0;net10.0` (plus
  `net472` on Windows) — compatible with this project's `net8.0` target.
- **Did not** run a compiled prototype against this project's own golden/fuzz PDF
  corpus. This session's egress proxy allows `nuget.org` and `github.com` but blocks
  `builds.dotnet.microsoft.com` (the .NET SDK installer), and there is no `dotnet` CLI
  in this container. The runtime spike issue #115 asks for is therefore still
  outstanding — see [Recommended next step](#recommended-next-step).

## Licensing

`OfficeIMO.Pdf`'s dependency footprint (from its own README, confirmed by its
`.csproj`): only `OfficeIMO.Core`, both MIT. No third-party PDF parser, writer,
renderer, or cryptography package ships in the base package. Certificate-based CMS,
RFC 3161, and X.509 signing move to the **optional** `OfficeIMO.Security` package —
also referenced only when the host application explicitly needs certificate signing
or validation, so a build that doesn't sign never pulls in cryptography beyond
`System.Security.Cryptography`. This resolves the motivation in #115: no AGPL
network/distribution clause anywhere in the dependency graph.

## Capability mapping

Organized by the capability seams #116 proposes, so this table doubles as the
per-interface migration plan. Status: 🟢 direct equivalent found in OfficeIMO source ·
🟡 equivalent exists but needs a fidelity check against this project's test corpus ·
🔴 no equivalent found — needs bespoke code or an upstream OfficeIMO feature.

### `IPdfDocumentStore` — open/save/merge/split/rotate/arrange/import

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `PdfIo.Open`/`OpenReadOnly` (`PdfReader`/`PdfWriter`) | `PdfDocument.Load(...)` — one entry point for bytes/file/stream, `Save`/`SaveAsync` | 🟢 |
| `Merger.Merge` (`PdfMerger`) | `PdfDocument.MergeWith`/`MergeWithReport`, independent per-source passwords/permissions | 🟢 |
| `PageTools.Arrange` (reorder/delete via `PdfMerger`) | `Pages.Extract`/`Split`/`Delete`/`Duplicate`/`Move` with range selectors (`1-3`, `last`, `odd`, exclusions) | 🟢 |
| `PageTools.Rotate` | `Pages.Rotate(degrees, pageRanges)` | 🟢 |
| `DocumentImport.ImageToPdf` | `PdfDocument.Create` + `.InlineImage`/image content block sized to page | 🟢 (straightforward rewrite) |
| `DocumentImport.DocxToPdf` (external LibreOffice process) | `OfficeIMO.Word` + `OfficeIMO.Word.Pdf`: `WordDocument.Load(...).SaveAsPdf(...)`, in-process, no external binary | 🟢 — **and an improvement**: removes the `soffice` external-process dependency and its `CanConvertWord` runtime probe entirely |

### `IInspector` — pages/fonts/images/attachments/outlines/security/DoS guards

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `PdfInspector.GetInfo` (page geometry, effective crop×media box, rotation, encryption) | `PdfDocument.Inspect()` → page count/geometry; `Security` info via preflight | 🟢 |
| `Encryptor.IsEncrypted`/`CanOpen` | `PdfDocument.Load` + `PdfLoadOptions.Password`, `PdfDocumentSecurityInfo` | 🟢 |
| `ExportValidator.Validate` (re-read output: xref/trailer, stream round-trip, page tree, AcroForm appearance invariants) | `PdfDocument.Analyze(...)` (health/rewrite-safety/diagnostics/repair/signature/compliance in one report) + `PdfRepairReport` | 🟡 — the *shape* of findings differs; needs a small adapter reproducing this project's specific checks (see [Gaps](#verified-gaps--upstream-issue-candidates)) |
| `PdfStructureGuard.EnsureFormXObjectsTerminate` (cyclic form-XObject guard before content processing) | Built into the parser itself: `PdfRedactionPlan`'s form-resource traversal enforces `MaxFormResourceTraversals`/depth limits (`PdfRedactionPlan.cs`, `AppendFormRenderingResourceIdentity`, `maximumDepth = 64`), and the general reader is fuzzed against a pinned GovDocs corpus + Open Preservation Foundation/veraPDF fixtures | 🟢 — likely **deletable** rather than portable: the guard becomes the library's job |
| `PdfContentGuard.InDefaultUserSpace` (draw without inheriting a page's leftover CTM) | `Stamp.Content((canvas, page) => ...)` canvas stamping operates in a fixed, page-relative coordinate system by contract | 🟢 |
| `Sanitizer.Inspect` (counts: metadata fields, attachments, scripts/actions, markup annotations, bookmarks, OC layers) | No single matching "hidden data" counter; the underlying facts are all independently readable (`Inspect()`, `JavaScript.List()`, `Attachments.Extract()`, `Annotations` list, `Bookmarks`, catalog optional-content) | 🟡 — portable, but as a small aggregation written against this repo, not a single OfficeIMO call (see Gaps) |

### `ITextEditor` / `IContentStreamEditor` — the highest-risk seam

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `TextTools.GetTextInRegion`/`GetTextSpans` (via `LocationTextExtractionStrategy`-style `IEventListener`) | `Text.Inspect(region)`, `PdfDocument.Read()` → `PdfLogicalPage.TextBlocks`/`PdfTextSpan` (font, size, color, transform per span) | 🟢 |
| `TextTools.ReplaceTextInRegion` (drop matched glyphs, re-stamp) | `document.Text.Replace(region, newText, options)` — README states it "preserves unmatched source-span text" and fails closed for invisible/clipped text rather than guessing | 🟢, pending the fidelity check below |
| `TextTools.MoveText` | `Text.Add` + prior region removal, or the dedicated move path implied by `Images.Move`'s sibling text API | 🟡 — confirm a first-class move exists vs. compose remove+add |
| `TextTools.AddText` (canvas + `PdfFont`) | `document.Text.Add(region, text, PdfTextEditOptions)` | 🟢 |
| `TextTools.FindText` (`TextMatchMode`, whole/partial) | `Text.Find(...)` — "case and whole-word filters over visible, unclipped text" | 🟢 |
| **`ContentStreamEditor`** — subclasses `PdfCanvasProcessor`, intercepts `Tj/TJ/'/"` and `Do`, does **per-glyph** replacement (drops only the glyphs inside a region, re-emits the rest as an equivalent-width `TJ` displacement so surrounding text doesn't reflow), recurses into form XObjects up to depth 6, handles inline images | `PdfRedactionApplier.TextScrubbing.cs` (945 lines) + `PdfRedactionApplier.TextFormScrubbing.cs` (157 lines, form-XObject-aware) implement the same class of operation: matched text objects are physically removed from the content stream, not merely covered | 🟡→🟢 by evidence of scale and dedicated form-XObject handling, but **this is the one operation in the whole migration worth an actual before/after content-stream diff** before trusting it — see [Gaps](#verified-gaps--upstream-issue-candidates) item 1 |
| `ImageTools.MoveImage` (remove draw call, redraw at shifted rect) | `Images.Move(placement, deltaX, deltaY)` | 🟢 |
| `ImageScrubber.TryScrubPixels` (re-encode image XObject with pixels inside a region blacked out or paper-colored, for OCR'd scanned pages) | `PdfRedactionApplier.Images.PixelRewrite.cs` (1,007 lines) — dedicated pixel-level image rewrite path, distinct from the 988-line whole-placement `Images.cs` | 🟢 by evidence of scale/dedication |

### `IRedactionEngine` — the project's core differentiator

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `Redactor.Redact`/`RemoveContent` (drives `ContentStreamEditor`, then removes intersecting annotations, then paints the box) | `document.Redactions.Apply(areas, options)` — "removes matched text objects and annotations, then paints redaction marks" (`PdfRedactionApplier.cs:9`); `PdfRedactionApplyOptions` exposes `RemoveIntersectingPaths`, `PaintUnmatchedAreas`, `UnsupportedImagePolicy`, `CleanupScope` | 🟢 |
| `RedactionBox.Prepare`/`MergeAdjacent`/`Expand` (pre-processing regions before applying) | `document.Redactions.Plan(areas)` — a **non-mutating** preview returning every `PdfRedactionMatch` (text/annotation/image) intersecting each area, plus preflight/diagnostics | 🟢 — and arguably better: this project has no equivalent dry-run today |
| `RedactionReporter.Analyze` (per-region: text removed, image thumbnails, annotation/text-run counts, computed from the *original* doc) | `PdfRedactionMatch` already carries `Text`, `Kind` (TextBlock/Annotation/ImagePlacement), `ImagePlacement`, page/rect — everything except a rendered thumbnail, which is one `Images.Extract()` call away | 🟢 — direct rebuild on `Plan(...).Matches`, likely simpler than today's hand-rolled version that separately re-derives spans/images/annotations |
| *(nothing today)* | `document.Redactions.Verify`/`AssertVerified`/`VerifyAppliedPlan` — re-opens the rewritten PDF and proves configured content no longer intersects the reviewed areas (`PdfRedactionVerification.Residue.cs`, 375 lines) | 🟢 **capability upgrade** — an independent, automatable proof this project does not currently have for its own compliance claims |

### `IAnnotationEngine` — highlight/ink/watermark/Bates/stamps

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `HighlightTool.AddHighlight` (`Multiply` blend rectangle in default user space) | `Stamp.Content` canvas supports fills and blend-mode effects (`SetBlendMode` appears in `PdfStamper.Pages.cs`); confirm the fluent canvas exposes blend mode directly | 🟡 — see Gaps item 2 |
| `InkTools.AddInk` (freehand polylines, round caps/joins) | `Stamp.Content` canvas: README lists "shapes, drawings, clipping, and effects are supported" for canvas stamping | 🟢, pending a round-cap/join fidelity check |
| `WatermarkTool.AddTextWatermark` | `PdfOptions.TextWatermark` at creation time, or `Stamp.TextWatermark(text, PdfTextStampOptions { RotationDegrees, Opacity, ... })` on an existing document | 🟢 |
| `BatesTool.AddBatesNumbers` | `PdfBatesNumberer.Apply(documents, PdfBatesNumberingOptions { StartNumber, Prefix, MinimumDigits, Position })` — a **named, first-class feature**, not something to reimplement on the canvas | 🟢 |
| `Signer.AddImageSignature` (stamp an uploaded/drawn signature image) | `Stamp` image placement, or `Images.Add` fit-to-region | 🟢 |

### `IFormEngine`

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `FormTools.AddTextField`/`AddDropdown`/`AddRadioGroup`/`AddCheckbox`/`AddButton` | `Forms.Edit(form => form.Create(new PdfFormFieldCreateOptions { Kind, Name, PageNumber, X, Y, Width, Height, Style, ... }))` — text/checkbox/combo/list/radio-group/push-button/signature fields in one transaction | 🟢 |
| `FormTools.ListFields` (name, type, value, options, readonly, geometry, attached script) | `PdfDocument.Inspect().FormFields` / `PdfDocumentReadResult.FormFields` | 🟡 — confirm per-field JS/activation script surfaces (README shows `JavaScript` as a *create*-time option on push buttons; reading it back per-field, and setting it on non-button fields' `/AA /U`, needs confirming) — see Gaps item 3 |
| `FormTools.FillFields` (+ optional flatten) | `Forms.FillAndFlatten(values)`; `Forms.Edit` for fill-without-flatten | 🟢 |
| `FlattenTool.Flatten` (Mode: forms-only / annotations-only / all) | `Forms.FillAndFlatten` (forms) + `Annotations.Flatten` (non-form annotations), per the annotation workflow row ("...flatten... authored or diagnosed synthesized appearances") | 🟢 — composes to the same three modes |

### `ISecurityEngine` — encrypt/sanitize/JS/URLs

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `Encryptor.Encrypt`/`Decrypt` (AES-256, `WriterProperties`) | `PdfStandardEncryptionOptions` (AES-256 default, AES-128, legacy RC4, Unicode passwords, typed permission bits) via `PdfOptions().SetEncryption(...)`; platform AES by default, managed AES from `OfficeIMO.Core` for restricted hosts | 🟢 |
| `JavaScriptTool.AddDocumentScript`/`ListScripts`/`RemoveScript` (`/Names /JavaScript` tree) | `document.JavaScript.List()` / `.Edit(scripts => scripts.AddOrReplace(name, code).Remove(name))` — named, exact-match, name-tree-preserving | 🟢 (closer to a 1:1 API match than most items in this table) |
| `PdfSafety.Scan`/`StripActive` (document JS, open/page/annotation actions, outward URI/Launch/SubmitForm/GoToR/ImportData) | The sanitizer workflow ("policy-driven full rewrites remove or quarantine embedded payloads, unsafe actions and URI targets") plus `JavaScript.List()` for the JS half | 🟡 — confirm the *action-kind* granularity (URI vs. Launch vs. SubmitForm vs. GoToR vs. ImportData) is individually selectable, since `PdfSafety.Scan` reports and this project's UI surfaces those separately | see Gaps item 4 |
| `UrlTools.ExtractLinks`/`ExtractLinkAnnotations` (URI links + every link kind with geometry and chained `/Next` actions) | `PdfDocumentReadResult.Links` (`PdfLogicalLinkAnnotation`) | 🟡 — confirm action-kind classification (goto/remote-goto/launch/named/submit vs. plain uri) and chained-action traversal match today's `UrlTools.ClassifyLink`/`CollectUri` |
| `Sanitizer.Sanitize` (metadata incl. XMP, attachments, comment annotations, bookmarks, OC layers, scripts/actions) | Composable from `UpdateMetadata(...)` (clear standard fields) + catalog `/Metadata` removal + `Attachments` remove-all + `Annotations` remove-by-kind + `Bookmarks` remove-all + optional-content removal + the JS/action sanitizer above | 🟡 — every piece exists; there's no single call. See Gaps item 5 for whether OfficeIMO should offer one |

### `ISigningEngine`

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `Signer.SignDigitally` (PKCS#12, `PdfSigner` + BouncyCastle, detached CMS) | `Security.SignExternal(signer, PdfExternalSignatureOptions)` — PDF package owns byte ranges/incremental updates/signature dictionary; `PdfCmsExternalSigner` (from optional `OfficeIMO.Security`) supplies the CMS/PKCS#12 side | 🟢, with a package split (core PDF signature mechanics vs. optional crypto provider) that is actually cleaner than today's single `Signer.cs` |
| `Signer.GetSignatures` (list + `coversWholeDocument` / integrity) | `Security.ValidateSignatures(cryptography)` → `PdfSignatureValidationReport`; byte-range/incremental-update inspection is native to the PDF package (not the optional crypto package) | 🟢 — this is exactly the item the earlier README-only investigation flagged 🔴→🟡 as unresolved; source inspection resolves it to 🟢 |
| `CertificateFactory.CreateSelfSignedPkcs12` (`System.Security.Cryptography` + BouncyCastle, **not iText today**) | No change needed — this file has no `iText` dependency now and none after migration | 🟢 (no migration work) |

### Unaffected / independent of iText already

`PageRenderer` (PDFium via `PDFtoImage` + SkiaSharp), `VisualDiff` (renders both versions via `PageRenderer`, diffs pixels), `DocComparer` (word-level LCS diff over `TextTools.GetTextSpans` output — portable once `TextTools` is), `OcrTool` (Tesseract over rendered pages; writes its invisible text layer through `TextTools.AddText`, so it inherits that seam's status), `UrlClassifier` (pure string classification, no PDF library at all).

*Aside, out of scope for #115 but worth flagging separately*: `OfficeIMO.Pdf` also ships its own page-to-image renderer (`ToImages()`/`ToImage()`) and its own PDF/A, signature-appearance, and font-embedding stack. Once the engine migration lands, `PDFtoImage`/PDFium could in principle be retired too — but that's a second, independent decision with its own risk (PDFium is a mature, widely-deployed renderer; OfficeIMO's is comparatively new) and should be a separate issue, not bundled into #115.

## Verified gaps / upstream issue candidates

These are the concrete items this pass could not resolve to 🟢 from source alone —
each needs either a code spike against this project's test corpus, or is a real,
filable gap in OfficeIMO. Draft issue text for the OfficeIMO-side items is in
[`docs/officeimo-issues-to-file.md`](officeimo-issues-to-file.md).

1. **Per-glyph redaction fidelity vs. run-level.** `ContentStreamEditor` specifically
   preserves layout by splitting a partially-overlapping text-show operator at the
   *glyph* boundary — glyphs outside a region survive with their original spacing,
   only the glyphs inside are replaced by an equivalent-width displacement. Whether
   `PdfRedactionApplier.TextScrubbing.cs` does the same (vs. dropping/keeping whole
   runs, which would over- or under-redact at a run's edges) cannot be confirmed by
   reading around a 945-line file at reasonable depth — it needs a real round-trip:
   redact half of a multi-word run and diff the resulting content stream. **This is
   the one spike issue #115 explicitly calls for, and it's still open** — this
   session's egress proxy blocks the .NET SDK installer, so it needs to run in the
   project's own devcontainer (which already carries a .NET SDK per
   `.devcontainer/Dockerfile`) or on a developer machine, not in this remote session.
2. **Blend-mode control on canvas stamping isn't documented at the fluent level.**
   `HighlightTool` relies on `Multiply` specifically so glyphs stay legible under the
   highlight color. `SetBlendMode` exists in OfficeIMO's internal stamping code but
   the public `Stamp.Content` canvas API surface in the README doesn't show a
   blend-mode setter. If it's missing, this is an upstream issue (draft #1).
3. **Per-field JavaScript activation, read and write, on non-button fields.** Today's
   `FormTools` attaches script to a push button's `/A` and to every other field
   type's widget `/AA /U`, and reads it back the same way for the forms panel. The
   README only demonstrates `JavaScript` as a create-time option on a push button.
   If `Forms.Edit`/`ListFields`-equivalent doesn't expose `/AA /U` for text/choice/
   checkbox/radio fields, that's an upstream issue (draft #2).
4. **Individually selectable outward-action kinds when stripping active content.**
   `PdfSafety` distinguishes `URI`, `Launch`, `SubmitForm`, `GoToR`, `ImportData` and
   lets the caller strip JavaScript and URL-reaching actions independently. Confirm
   OfficeIMO's sanitizer exposes the same granularity rather than an all-or-nothing
   "unsafe actions" toggle (draft #3 covers the case it doesn't).
5. **No single "strip everything a user didn't mean to share" call.** Every
   ingredient of `Sanitizer.Sanitize` exists somewhere in OfficeIMO, but as separate
   calls across metadata, attachments, annotations, bookmarks, optional content, and
   the JS/action sanitizer — with no combined report shaped like
   `HiddenDataReport` (one count per category, before removal). This is a
   product-shape gap worth filing upstream since `PdfEditor.Core`'s `Sanitizer` is a
   plausible template for it (draft #4).
6. **`ExportValidator`'s specific checks vs. `Analyze()`'s shape.** `ExportValidator`
   checks are narrow and read-only-forever (never throws, reports findings): xref/
   trailer integrity, stream length/filter round-trip, page-tree consistency, cheap
   AcroForm appearance invariants. `PdfDocument.Analyze()` looks like a superset, but
   confirm it never throws on a malformed *output* it just produced (a self-check
   must not itself be fragile) and that its diagnostics are granular enough to
   reproduce this project's specific post-export assertions.
7. **`TextTools.MoveText` as a first-class operation.** Confirm whether moving
   selected text is a direct OfficeIMO API or has to be composed from
   `Text.Inspect` + region removal + `Text.Add` (likely fine either way, just needs
   confirming before committing to the seam's method shape).

## Recommended plan

Recommendation stands as the original investigation framed it, updated for what
source access changed:

1. **Phase 0 — build the engine seam (#116), regardless of outcome.** This pass's
   findings make Phase 0 unconditionally worth doing sooner: nearly every capability
   table above landed 🟢 or 🟡, which means the seam is very likely to be *used*
   (not built and then abandoned back onto iText). Introduce the interfaces #116
   lists, move the 28 files behind them with **iText as the sole implementation**,
   zero behavior change, existing tests + 90% coverage gate stay green.
2. **Phase 1 — the one required spike, not a broad one.** Where the prior
   investigation asked "can OfficeIMO do content-stream redaction and text editing
   at all", this pass's source reading answers "yes, with dedicated, sizeable
   implementation code for exactly that." The remaining spike is narrower and
   mechanical: build a small console harness against `OfficeIMO.Pdf` 3.3.0,
   round-trip a handful of this repo's existing golden/fuzz fixtures
   (`tests/PdfEditor.Core.Tests/Golden`, `.../Fuzz`) through `Redactions.Plan` →
   `Redactions.Apply` → `Redactions.Verify`, and diff against today's `Redactor`
   output on the same inputs. Needs a real .NET SDK (the project's own devcontainer
   has one; this session's sandboxed egress does not).
3. **Phase 2 — migrate capability-by-capability behind the seam**, in the order the
   mapping table suggests confidence: `ISigningEngine`/`IPdfDocumentStore` first
   (cleanest 🟢s, including deleting the LibreOffice dependency), then
   `IAnnotationEngine`/`IFormEngine`/`ISecurityEngine`, then `IRedactionEngine`/
   `ITextEditor` once Phase 1's spike confirms fidelity.
4. **File the upstream issues** in [`docs/officeimo-issues-to-file.md`](officeimo-issues-to-file.md)
   against `EvotecIT/OfficeIMO` for the items in [Verified gaps](#verified-gaps--upstream-issue-candidates)
   that are genuine product gaps (drafts #1–#4) rather than this project's own
   spike work (items 1, 6, 7 above stay this project's problem).

## Caveats and what remains unverified

- This is a static-source-reading pass, not a compiled, executed one. OfficeIMO's own
  documentation is unusually precise about boundaries (it explicitly lists what each
  workflow does *not* claim), which is a good sign for trustworthiness, but "the code
  exists and looks purpose-built" is not the same evidence as "it passed on our
  fixtures."
- `OfficeIMO.Pdf`'s current-state doc describes itself as "useful and broad, but
  still evolving," with open items in font/color/pattern rendering fidelity — none
  of which this project depends on for its core redaction/editing guarantees, but
  worth knowing the engine is young relative to iText's ~20 years of edge-case
  hardening.
- No attempt was made in this pass to build or run anything against OfficeIMO —
  the egress proxy in this session allows `github.com`/`nuget.org` (used to clone
  the repo and confirm package publication) but rejected
  `builds.dotnet.microsoft.com` (the .NET SDK installer), and no `dotnet` CLI is
  present in this container.
