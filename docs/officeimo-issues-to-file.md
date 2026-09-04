# Issues to submit to EvotecIT/OfficeIMO

> **Status: 6 of 7 drafts are obsolete — do not file them.**
>
> Between the review that produced this list and now, OfficeIMO's `master` picked up
> ~33 commits that independently fix or address six of the seven items. Filing them
> would be reporting work that's already done. Only **Issue 5** below is still worth
> submitting.
>
> Nothing here was ever filed, and there's no evidence these fixes relate to this
> analysis — they're the maintainer's own work landing on a repo with very high commit
> velocity. Verified at `origin/master` (`819a0516`); the release these were written
> against, `OfficeIMO-v20260902190744` (= commit `bd9e881f` = NuGet **3.3.0**),
> contains none of them.

## Current state of each draft

| # | Original title | Status on `master` | Evidence |
|---|---|---|---|
| 1 | Redaction bounds computed without font width providers | ✅ **Fixed** | `SumWidth1000` now resolves `fontWidthProviders` from `ResourceResolver.GetFontWidthProviders(...)`, threaded through `CollectTextObjects` → `BuildRedactionTextObject` → `ParseTextSpans` |
| 2 | Sub-text-object redaction granularity | ✅ **Fixed** | New `PdfContentStreamTextRewriter.cs` (610 lines), commit `6a384161` "Preserve unaffected PDF glyphs during redaction" |
| 3 | Text editing rejects invisible text (OCR'd scans) | ✅ **Addressed** | `IsSafelyEditableSpan(span, allowTextRenderingMode3)` opt-in; commit `e5eee161` "Support opt-in PDF OCR text operations" |
| 4 | Blend mode on `Stamp.Content` canvas | ✅ **Addressed** | Commit `d88ae3b3` "Add scoped PDF canvas blend modes" — `PdfPageCanvas.cs` + `PdfDocumentCanvasTests.cs` |
| 5 | Per-field JavaScript on non-button form fields | ❌ **Not addressed** | No matching commit — **still worth filing** |
| 6 | Selectable outward-action kinds when sanitizing | ✅ **Addressed** | Commit `522dc06a` "Add typed PDF action sanitization" — new `PdfSanitizationActionCounts.cs` |
| 7 | Combined "hidden data" inspect + sanitize | ✅ **Addressed** | Commit `00e109ff` "Add combined PDF before-sharing sanitization" — new `PdfSanitizationCategoryCounts.cs` |

**Important caveat:** all of the above is on `master` and **unreleased**. The latest
published `OfficeIMO.Pdf` on NuGet is still **3.3.0**, which has none of it. Don't plan
around these fixes until they ship — and validate them against our own fixtures when
they do, rather than trusting the commit messages (or this table).

Draft #2's *alternative* ask — making `TextContentParser` / `PdfContentStreamInterpreter`
public — was **not** done; they remain `internal`. That's moot for us, since they
implemented the glyph-granularity option directly, which is the better outcome.

---

## Issue 5 — Per-field JavaScript activation on non-button AcroForm fields

**The only draft still worth submitting.**

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

*(From reading source rather than running it — no .NET SDK in the environment this was
written in — so apologies if there's already a supported route.)*

---

## Appendix: the superseded drafts

The full text of drafts 1, 2, 3, 4, 6 and 7 is preserved in this file's git history
(see the commit that added them, prior to this revision) should any of them need
reviving — for example if a shipped release turns out not to cover the case, or if
validation against our fixtures shows a fix is incomplete.
