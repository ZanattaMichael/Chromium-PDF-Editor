# Draft issues for EvotecIT/OfficeIMO

Written while evaluating `OfficeIMO.Pdf` as an iText replacement for
[`reDACT-PDF-Editor`](https://github.com/ZanattaMichael/reDACT-PDF-Editor) (see
[`docs/backend-portability.md`](backend-portability.md) for the full investigation).
These are drafted, not filed: this session has read-only access to
`EvotecIT/OfficeIMO` and no authorization to open issues on someone else's
repository on the project owner's behalf. Each entry is ready to paste into
[github.com/EvotecIT/OfficeIMO/issues/new](https://github.com/EvotecIT/OfficeIMO/issues).

Every item below is a genuine capability question found by reading OfficeIMO's own
source (not a misunderstanding correctable by reading further docs) — but they are
phrased as questions/requests, since it's possible the capability already exists
under a name this pass didn't find.

**Draft #5 is the one that matters.** Drafts #1–#4 are papercuts; #5 decides whether
a full migration off iText is possible at all.

---

## Draft #1 — Is blend mode (e.g. `Multiply`) settable on `Stamp.Content` canvas fills?

**Labels:** question, enhancement

**Body:**

Evaluating `OfficeIMO.Pdf` as a replacement for a PDF engine that currently paints
text highlights as a semi-transparent rectangle using a `Multiply` blend mode, so the
highlight color tints the paper while the (usually dark) glyphs underneath stay
legible — a plain alpha-blended fill would wash the text out instead.

`Stamp.Content((canvas, page) => ...)` looks like the right primitive (README:
"Text, rich tables, images, shapes, drawings, clipping, and effects are supported").
I can see `SetBlendMode` used internally (e.g. `PdfStamper.Pages.cs`), but I couldn't
find a blend-mode setter on the public canvas API surface shown in the README or
package docs.

Is there a supported way to set the blend mode (specifically `Multiply`) for a fill
made through the `Stamp.Content` canvas, or another route to a markup-highlight
effect (paint under existing text, blended, without an annotation)? If not, would a
canvas method for this be in scope — something like
`canvas.WithBlendMode(PdfBlendMode.Multiply, draw => draw.Rectangle(...).Fill())`?

---

## Draft #2 — Reading/writing per-field JavaScript activation on non-button AcroForm fields

**Labels:** question, enhancement

**Body:**

Evaluating `OfficeIMO.Pdf`'s form APIs as a replacement for a tool that lets a user
attach a JavaScript snippet to any inserted form field (text, checkbox, combo,
radio group, push button) so it runs when the field is activated in Acrobat/Chrome —
and later list that script back for display/editing.

The convention: a push button's script goes on its widget's `/A` (activation) entry;
every other field type's goes on the widget's `/AA` dictionary's `/U` (mouse-up)
entry. Radio groups get it per-option-widget.

The README shows `JavaScript` as a `PdfFormFieldCreateOptions` property, demonstrated
only on a `PushButton` kind:

```csharp
.Create(new PdfFormFieldCreateOptions {
    Name = "calculate",
    Kind = PdfFormFieldCreationKind.PushButton,
    ...
    JavaScript = "this.getField('total').value = 42;"
})
```

Two questions:
1. Does `JavaScript` on `PdfFormFieldCreateOptions` write to `/AA /U` when `Kind` is
   `Text`/`CheckBox`/`Combo`/`List`/`RadioGroup` (not just the button's `/A`)?
2. Is there a way to **read back** the activation script for an existing field —
   something on the form-field-listing result equivalent to "the JS attached to this
   field's widget," checking both `/A` and `/AA /U` depending on field type?

If neither exists today, would you consider it in scope? Happy to describe the exact
current behavior we're trying to match if useful (widget-type → action-slot mapping).

---

## Draft #3 — Independently selectable outward-action kinds when stripping active content

**Labels:** question, enhancement

**Body:**

Evaluating `OfficeIMO.Pdf`'s active-content sanitizer as a replacement for a tool
that lets a user strip **JavaScript** and **outward-reaching actions** independently,
where "outward-reaching" is itself broken into distinct action subtypes: `URI`
(open a URL), `Launch` (open a file), `SubmitForm`, `GoToR` (jump to a remote file),
and `ImportData`. A scan reports counts per bucket (JS count vs. URL-action count),
and the strip call takes independent `javaScript`/`urls` flags.

The `README.md` "Sanitize active content" section describes "policy-driven full
rewrites remove or quarantine embedded payloads, unsafe actions and URI targets, and
rich-media content," which reads as an all-or-nothing "unsafe actions" bucket rather
than the five-subtype breakdown above.

Is that granularity available — i.e. can a caller strip JavaScript without touching
`Launch`/`SubmitForm` actions, or strip only `URI` actions while leaving
`GoToR`/`ImportData` alone, and get a scan result broken out the same way? If the
policy is currently coarser than this, would finer-grained action-kind selection (and
a matching diagnostic count per kind) be a reasonable addition?

---

## Draft #4 — A single "what would sanitizing this document remove" report + one-call sanitize

**Labels:** enhancement

**Body:**

Evaluating `OfficeIMO.Pdf` as a replacement for a "sanitize before sharing" feature
that, in one pass, both **previews** and **removes** everything a user typically
doesn't intend to share in a PDF that never shows on the rendered page:

- document metadata (Author/Title/Subject/Keywords/Creator + the XMP packet — but
  *not* Producer/CreationDate/ModDate/Trapped, which are tool-authored, not
  user-authored)
- embedded file attachments (name-tree `EmbeddedFiles`, catalog `/AF`, and
  per-page `FileAttachment` annotations)
- JavaScript and outward-reaching actions (see draft #3)
- comment/markup annotations (keeping `Link`/`Widget`, which are structural, not
  "hidden info")
- the bookmark/outline tree
- optional-content (layer) definitions

The preview is a single typed report — one count per category — so a UI can show
"this will remove: 3 metadata fields, 1 attachment, 2 scripts, 5 comments, 8
bookmarks, 1 hidden layer" before the user commits, and the removal call takes the
same category flags to let the user opt out of specific categories.

Every ingredient exists somewhere in `OfficeIMO.Pdf` individually (metadata update,
`Attachments`, `Annotations`, `Bookmarks`, catalog optional-content, the active-content
sanitizer) — this issue is asking whether a **combined** "hidden data" inspect+strip
call (or a documented recipe composing the pieces into one call with one combined
report) is in scope, since composing six separate subsystems into one user-facing
"sanitize" action is exactly the kind of thing worth having a single supported entry
point for.

Happy to share our current shape (`HiddenDataReport` with one int per category, and a
`SanitizeOptions` with one bool per category) as a concrete reference if that's useful
for scoping.

---

## Draft #5 — Sub-text-object redaction granularity (or public content-stream primitives)

**Labels:** enhancement

**Body:**

First: thank you for `OfficeIMO.Pdf` — the redaction subsystem is clearly built with
care, the fail-toward-removal posture in `IntersectsTarget` is the right call for a
confidentiality boundary, and `Redactions.Verify` is a feature most PDF libraries
don't offer at all.

I'm evaluating it as a replacement for iText in an interactive PDF redaction editor,
where a user drags a rectangle over a few words and expects exactly that content to
disappear. Reading `PdfRedactionApplier.TextScrubbing.cs`, redaction resolves to whole
text objects:

- `BuildRedactionTextObject` unions the bounds of every span in a `BT`…`ET` block into
  one bounding box (`AddSpanBounds`).
- `MarkMatchingTextObjects` → `IntersectsTarget` tests the requested rectangle against
  that union box.
- `RemoveTextObjectSpans` then excises the whole `BT`→`ET` byte range.

Because most producers emit one text object per line, a rectangle over one word removes
the whole line. Measured on real files: Word-produced PDFs in your own
`OfficeIMO.Pdf.Tests/Pdf/ReferenceBaselines/` run ~73–145 text objects per stream with
the largest holding 89–105 characters across a single baseline.

`PdfTextEditor.RemoveTextPreservingUnmatchedSpans` compensates by re-stamping the
collateral spans, which is a clever recovery — but the re-stamp resolves through
`ResolveStandardFont(...)`, so text originally in an embedded font returns as its
closest standard-14 approximation (correctly reported via
`BuildSubstitutionWarnings`). For a redaction tool the visible result is that
redacting one word changes the typeface of the rest of the line.

**Ask — either of these would unblock this use case, whichever fits your design:**

1. **Sub-text-object redaction granularity**: when a rectangle partially intersects a
   text object, rewrite the show-text operators so glyphs outside the rectangle survive
   in place — replacing removed glyphs with equivalent-width `TJ` displacements so the
   surviving text keeps its original spacing and doesn't reflow. Possibly opt-in via
   `PdfRedactionApplyOptions` (e.g. `TextGranularity = TextObject | Span | Glyph`) so
   the current conservative default is preserved.
2. **Or make the content-stream primitives public** so callers can implement that
   themselves on top of your parser: `PdfContentStreamInterpreter`, `TextContentParser`
   (and a supported way to write a rewritten content stream back). These already exist
   and are used internally by exactly this code path; today they're `internal`, so the
   only route to glyph-level editing is a different library. `PdfReadPage.GetTextSpans()`
   being public is already halfway there — the missing half is writing.

Two smaller observations from the same file, offered as data rather than requests:

- The applier estimates every glyph advance as a flat half-em
  (`SumWidth1000 => bytes.Length * 500D`, line ~525), while `PdfRedactionPlanner` derives
  its matches from `document.TextBlocks` in the logical read model, which uses real
  font metrics. So `Plan()` and `Apply()` compute geometry two different ways; a
  proportional-font line whose true width the estimate undershoots could in principle
  leave text inside a painted area, which seems contrary to the intent documented in
  `IntersectsTarget`'s comment.
- `IsSafelyEditableSpan` requires `TextRenderingMode == 0`, so the text-edit path
  rejects invisible text. That's the correct conservative default in general, but it
  also means OCR'd "searchable scans" (an invisible `Tr 3` layer over a page image) —
  a very common redaction input — can't go through text editing at all. An explicit
  opt-in for that case (the caller knowing the layer is an OCR layer) might be worth
  considering.

Happy to contribute failing test fixtures for any of the above if that's useful.
