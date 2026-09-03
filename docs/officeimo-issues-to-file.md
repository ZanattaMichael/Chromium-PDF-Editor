# Issues to submit to EvotecIT/OfficeIMO

Found while evaluating `OfficeIMO.Pdf` 3.3.0 as an iText replacement (see
[`docs/backend-portability.md`](backend-portability.md)). Drafted, not filed — this
session has read-only access to that repository.

File at [github.com/EvotecIT/OfficeIMO/issues/new](https://github.com/EvotecIT/OfficeIMO/issues).
Submit **in this order**: #1 is a correctness bug worth reporting on its own merits,
#2 decides whether our migration is possible at all, #3–#7 are papercuts that can go in
any order (or be skipped if you'd rather not open seven at once).

All findings are from reading source at commit `aba60b7b` and from static analysis of
PDF bytes. **Nothing here was compiled or executed** — there's no .NET SDK in the
environment this was written in. Each draft says so; please keep that caveat in when
filing, it's the honest framing and it costs nothing.

| # | Title | Type | Priority |
|---|---|---|---|
| 1 | Redaction applier computes text bounds without font width providers | Bug | **P0** — possible under-redaction |
| 2 | Sub-text-object redaction granularity, or public content-stream primitives | Enhancement | **P1** — blocks our migration |
| 3 | Text editing rejects invisible text, so OCR'd scans can't be edited | Enhancement | P2 |
| 4 | Blend mode on `Stamp.Content` canvas fills | Question | P3 |
| 5 | Per-field JavaScript activation on non-button AcroForm fields | Question | P3 |
| 6 | Independently selectable outward-action kinds when stripping active content | Question | P3 |
| 7 | Combined "hidden data" inspect + one-call sanitize | Enhancement | P3 |

---

## Issue 1 — Redaction applier computes text bounds without font width providers

**Labels:** bug

**Title:** `Redaction: text object bounds computed with a flat 0.5em width estimate, ignoring font metrics`

**Body:**

While evaluating `OfficeIMO.Pdf` for a redaction tool I think I've found a correctness
issue in how `PdfRedactionApplier` computes the bounds it uses to decide what a
redaction rectangle intersects. Flagging it because the failure direction appears to be
*retaining* text inside a painted redaction area, which runs against the intent
documented in that same file.

**What I'm seeing**

`TextContentParser.Parse` takes a `sumWidth1000ForFont` callback and uses it to
accumulate glyph advances into each span's width
(`TextContentParser.cs:877-878`: `double w1000 = sumWidth1000ForFont(font, g);` →
`advGlyph = ((w1000 / 1000.0) * size + charSpacing + ...) * hScale`).

There are two callers, and they supply very different width functions.

`PdfReadPage.cs:666` resolves real per-font metrics, falling back to a flat estimate
only when a font resource can't be found:

```csharp
double SumWidth1000(string fontRes, byte[] bytes) =>
    widthProviders.TryGetValue(fontRes, out var wp) ? wp(bytes) : (bytes?.Length ?? 0) * 500.0;
```

`PdfRedactionApplier.TextScrubbing.cs:524` uses the flat estimate unconditionally — no
width providers are passed in at all:

```csharp
double SumWidth1000(string fontResource, byte[] bytes) =>
    bytes is null ? 0D : bytes.Length * 500D;
```

So every glyph is assumed to be exactly half an em wide during redaction, regardless of
the actual font.

**Why I think it matters**

Those bounds feed `AddSpanBounds` → `BuildRedactionTextObject`'s union box →
`IntersectsTarget`, which decides whether a text object is removed. When real text is
wider than the estimate — capitals (`W` is 944/1000 in Helvetica, `M` 833), bold and
many display faces, monospace at 0.6em — the computed box is narrower than the text
actually painted on the page. A redaction rectangle placed over the right-hand end of
such a line can then fail to intersect the *estimated* box even though it visually
covers the text, leaving the text object in place. The mark is painted, the text stays
extractable underneath.

That's the opposite of the guarantee the code seems to intend a few lines further down
in the same file:

```csharp
// An area redaction is a confidentiality boundary. If a text object cannot be
// located, retaining it could leave extractable text inside the painted area.
```

There's a secondary effect too: `PdfRedactionPlanner` derives its matches from
`document.TextBlocks` (the logical read model, i.e. real metrics), while the applier
uses the estimate. So `Redactions.Plan(...)` and `Redactions.Apply(...)` can disagree
about which content a given rectangle covers, which is awkward for anyone using `Plan`
as a reviewable preview or as an audit record of what was removed.

**Expected**

The applier resolves the same per-font width providers the read model uses, so
redaction geometry matches both the rendered page and the plan preview.

**Repro sketch**

I have not been able to compile a repro — there's no .NET SDK available in the
environment I'm working in, so this is from reading the source. What I'd expect to
demonstrate it:

1. Generate a PDF with a line of capitals in Helvetica (e.g. `WWWWW MMMMM WWWWW`) at a
   known position and font size.
2. Compute where that line actually ends (real advance widths) versus where a
   0.5-em-per-byte estimate puts it — the estimate should fall well short.
3. `Redactions.Apply` a rectangle covering the last word, in the gap between the two.
4. Extract text from the output and check whether the covered word survives.

Happy to contribute that as a test fixture if it's a useful shape, and equally happy to
be told I've misread the call graph.

---

## Issue 2 — Sub-text-object redaction granularity, or public content-stream primitives

**Labels:** enhancement

**Title:** `Redaction removes whole BT..ET text objects; allow sub-object granularity or expose content-stream primitives`

**Body:**

First: thank you for `OfficeIMO.Pdf` — the redaction subsystem is clearly built with
care, the fail-toward-removal posture in `IntersectsTarget` is the right call for a
confidentiality boundary, and `Redactions.Verify` is a feature most PDF libraries don't
offer at all.

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
the whole line. Measured on real files — including Word-produced PDFs in your own
`OfficeIMO.Pdf.Tests/Pdf/ReferenceBaselines/` — that's ~73–145 text objects per content
stream, with the largest holding 89–105 characters across a single baseline.

`PdfTextEditor.RemoveTextPreservingUnmatchedSpans` compensates by diffing which
non-targeted spans disappeared and re-stamping them, which is a clever recovery. But the
re-stamp resolves style through `ResolveStandardFont(...)`, so text originally set in an
embedded font returns as its closest standard-14 approximation (correctly reported via
`BuildSubstitutionWarnings`). For a redaction tool the visible result is that redacting
one word changes the typeface of the rest of the line.

**Ask — either of these would unblock this use case, whichever fits your design:**

1. **Sub-text-object redaction granularity.** When a rectangle partially intersects a
   text object, rewrite the show-text operators so glyphs outside the rectangle survive
   in place, replacing removed glyphs with equivalent-width `TJ` displacements so the
   surviving text keeps its spacing and doesn't reflow. Could be opt-in through
   `PdfRedactionApplyOptions` (e.g. `TextGranularity = TextObject | Span | Glyph`) so the
   current conservative default is preserved for callers who prefer it.
2. **Or make the content-stream primitives public**, so callers can implement that
   themselves on top of your parser: `PdfContentStreamInterpreter`, `TextContentParser`,
   and a supported way to write a rewritten content stream back. These already exist and
   are used internally by exactly this code path — they're `internal`, not absent.
   `PdfReadPage.GetTextSpans()` being public already gets us halfway; the missing half is
   writing.

Option 2 would suit us fine and is presumably much less work for you — we'd carry the
glyph-splitting logic ourselves rather than asking you to own it.

Caveat: this is from reading source, not from running it — no .NET SDK in my current
environment. Happy to be corrected if there's an option I've missed.

---

## Issue 3 — Text editing rejects invisible text, so OCR'd scans can't be edited

**Labels:** enhancement

**Title:** `Text editing fails closed on invisible text (Tr 3), blocking OCR'd searchable scans`

**Body:**

`PdfTextEditor.IsSafelyEditableSpan` requires, among other things,
`span.TextRenderingMode == 0`:

```csharp
private static bool IsSafelyEditableSpan(PdfTextSpan span) =>
    span.IsVisible &&
    !span.ClipPath.HasValue &&
    span.TextRenderingMode == 0 &&
    (!span.Color.HasValue || span.Color.Value.A == byte.MaxValue) &&
    span.CanRestamp &&
    !string.IsNullOrEmpty(span.Text);
```

…and the editor throws `NotSupportedException("The selected region contains invisible or
clipped text whose rendering state cannot be recreated safely.")` otherwise.

As a conservative default that's very reasonable — recreating arbitrary invisible or
clipped rendering state is genuinely unsafe. But it also rules out a common and
well-understood case: an OCR'd "searchable scan", which is a page image with an
invisible text layer painted in rendering mode 3. That layer is invisible *by
construction*, and a caller who produced it (or who has detected it) knows exactly what
it is. Today those documents can't go through text editing at all.

**Ask:** an explicit opt-in for this case — something like
`PdfTextEditOptions.AllowInvisibleTextEditing`, or a narrower
`AllowTextRenderingMode3`, letting a caller who understands the document take
responsibility. I notice `RemoveTextPreservingUnmatchedSpans` already carries an
`allowInvisibleTargetRemoval` parameter internally, so the concept seems to exist
already — the ask is essentially to surface it.

For context on why this matters for redaction specifically: with a searchable scan, the
words a user sees are pixels in the page image and the only real text is the invisible
OCR layer. Removing just that layer leaves the words visibly in place, so a correct
redaction has to take both — which means the text side has to be reachable in the first
place.

From source reading rather than execution (no .NET SDK to hand), so apologies if
there's already a supported route here.

---

## Issue 4 — Blend mode on `Stamp.Content` canvas fills

**Labels:** question, enhancement

**Title:** `Is blend mode (e.g. Multiply) settable on Stamp.Content canvas fills?`

**Body:**

Evaluating `OfficeIMO.Pdf` as a replacement for an engine that paints text highlights as
a rectangle with a `Multiply` blend mode, so the highlight colour tints the paper while
the (usually dark) glyphs underneath stay legible — a plain alpha-blended fill washes the
text out instead.

`Stamp.Content((canvas, page) => ...)` looks like the right primitive (README: "Text,
rich tables, images, shapes, drawings, clipping, and effects are supported"). I can see
`SetBlendMode` used internally (e.g. `PdfStamper.Pages.cs`), but I couldn't find a
blend-mode setter on the public canvas surface in the README or package docs.

Is there a supported way to set the blend mode for a fill made through the
`Stamp.Content` canvas, or another route to a markup-highlight effect (painted under
existing text, blended, without using an annotation)? If not, would a canvas method for
it be in scope — something like
`canvas.WithBlendMode(PdfBlendMode.Multiply, draw => draw.Rectangle(...).Fill())`?

---

## Issue 5 — Per-field JavaScript activation on non-button AcroForm fields

**Labels:** question, enhancement

**Title:** `Setting and reading per-field JavaScript activation on non-button form fields`

**Body:**

Evaluating `OfficeIMO.Pdf`'s form APIs as a replacement for a tool that lets a user
attach a JavaScript snippet to any inserted form field (text, checkbox, combo, radio
group, push button) so it runs when the field is activated in Acrobat/Chrome — and later
lists that script back for display and editing.

The convention we currently follow: a push button's script goes on its widget's `/A`
(activation) entry; every other field type's goes on the widget's `/AA` dictionary's
`/U` (mouse-up) entry. Radio groups get it on each option's widget.

The README shows `JavaScript` as a `PdfFormFieldCreateOptions` property, demonstrated
only on a `PushButton`:

```csharp
.Create(new PdfFormFieldCreateOptions {
    Name = "calculate",
    Kind = PdfFormFieldCreationKind.PushButton,
    ...
    JavaScript = "this.getField('total').value = 42;"
})
```

Two questions:

1. Does `JavaScript` write to `/AA /U` when `Kind` is
   `Text`/`CheckBox`/`Combo`/`List`/`RadioGroup`, rather than only to a button's `/A`?
2. Is there a way to **read back** an existing field's activation script — an equivalent
   of "the JS attached to this field's widget", checking `/A` and `/AA /U` as
   appropriate for the field type?

If neither exists today, would they be in scope? Happy to describe the exact
widget-type → action-slot mapping we're trying to match.

---

## Issue 6 — Independently selectable outward-action kinds when stripping active content

**Labels:** question, enhancement

**Title:** `Can active-content sanitization select individual outward-action kinds?`

**Body:**

Evaluating `OfficeIMO.Pdf`'s active-content sanitizer as a replacement for a tool that
strips **JavaScript** and **outward-reaching actions** independently, where
"outward-reaching" is itself broken into distinct subtypes: `URI` (open a URL), `Launch`
(open a file), `SubmitForm`, `GoToR` (jump to a remote file), and `ImportData`. Our scan
reports counts per bucket and the strip call takes independent flags, so a user can keep
their hyperlinks while removing embedded scripts, or vice versa.

The README's "Sanitize active content" section describes "policy-driven full rewrites
remove or quarantine embedded payloads, unsafe actions and URI targets, and rich-media
content", which reads like a single "unsafe actions" bucket rather than the five-subtype
breakdown above.

Is that granularity available — can a caller strip JavaScript without touching
`Launch`/`SubmitForm`, or strip only `URI` actions while leaving `GoToR`/`ImportData`
alone, and get a scan result broken out the same way? If the policy is currently
coarser, would finer-grained action-kind selection (and a matching per-kind diagnostic
count) be a reasonable addition?

---

## Issue 7 — Combined "hidden data" inspect + one-call sanitize

**Labels:** enhancement

**Title:** `A single "what would sanitizing remove" report and one-call sanitize`

**Body:**

Evaluating `OfficeIMO.Pdf` as a replacement for a "sanitize before sharing" feature
that, in one pass, both **previews** and **removes** everything a user typically doesn't
intend to share in a PDF but which never shows on the rendered page:

- document metadata (Author/Title/Subject/Keywords/Creator plus the XMP packet — but
  *not* Producer/CreationDate/ModDate/Trapped, which are tool-authored rather than
  user-authored)
- embedded file attachments (name-tree `EmbeddedFiles`, catalog `/AF`, and per-page
  `FileAttachment` annotations)
- JavaScript and outward-reaching actions (see the previous issue)
- comment/markup annotations, keeping `Link`/`Widget` (structural, not hidden info)
- the bookmark/outline tree
- optional-content (layer) definitions

The preview is a single typed report — one count per category — so a UI can say "this
will remove: 3 metadata fields, 1 attachment, 2 scripts, 5 comments, 8 bookmarks, 1
hidden layer" before the user commits, and the removal call takes the same category
flags so they can opt out per category.

Every ingredient exists in `OfficeIMO.Pdf` individually (metadata update, `Attachments`,
`Annotations`, `Bookmarks`, catalog optional content, the active-content sanitizer).
This is asking whether a **combined** inspect+strip entry point — or a documented recipe
composing the pieces with one combined report — is in scope, since folding six
subsystems into one user-facing "sanitize" action seems like something worth having a
single supported route for.

Happy to share our current shape (a report with one count per category, and an options
type with one bool per category) as a concrete reference if useful for scoping.
