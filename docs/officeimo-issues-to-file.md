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
