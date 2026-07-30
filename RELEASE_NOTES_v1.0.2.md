# v1.0.2

Four fixes on top of `v1.0.1`, all in the editing surface people touch most: text editing, highlighting, and redaction. Each one shipped with a test that was watched failing without the fix, and the whole test suite was overhauled alongside them to prove behaviour rather than appearance.

---

## Highlights

### Editing text no longer damages what is behind or around it
Editing text on a real-world page had three distinct failures, all fixed together:

- **Right-click ▸ Edit text ▸ Apply did nothing.** The menu opened a correctly filled-in panel and then silently discarded the edit — the pending region was cleared the instant the panel opened. It had been a complete no-op, with no error and no log entry. *(#85)*
- **Editing text over an image punched a black box through the image.** Text editing reused the redaction machinery, which blacks out any image under the region. A letterhead, watermark, or scanned page behind the words was destroyed by editing them. The image is now left untouched.
- **On a scanned (OCR'd) page, the old words stayed visible under the new ones.** There the visible words are pixels in the page image and the real text is an invisible layer over them; editing now erases those pixels too — filled with the surrounding paper colour, not black.
- **Replacement text longer than the original was silently truncated** — "HELLO" became "WORL". Longer replacements now lay out in full.

*(#89)*

### Edited text keeps its original size
Replacing text re-stamped it smaller every time, because the size was measured from the glyph's bounding box rather than the type size — 7–21% too small per edit, compounding across repeated edits (24pt Courier fell below 12pt after three passes). Size is now recovered correctly, and the font face and style fall back to the run being replaced instead of resetting to Helvetica. When a font genuinely can't be reproduced, the substitution is reported rather than done silently.

*(#29)*

### Highlight by sweeping across text, with a box option
Highlighting is now a text sweep — press, move, release across the words — marking exactly the characters swept, the way every reader does it, instead of dragging a rectangle over whole lines. A **Sweep / Draw a box** chooser is available for when a box is what you actually want (over a table or figure), and scanned pages with no selectable text fall back to the box automatically.

*(#23)*

---

## Performance

- **Redacting over a large image is ~3× faster.** The scrubber was encoding each image to PNG, decoding it straight back, and reading it a pixel at a time — millions of calls on a large scan. It now reads the pixels directly. This also resolves an intermittent CI failure where redaction over a decompression-bomb image ran close to the fuzz suite's time budget. *(#87)*

---

## Testing

The end-to-end suite was overhauled so a passing test means the operation actually worked, not just that the UI looked right — the failure mode behind several of the bugs above, which had shipped behind green tests. Assertions now read content and pixels back out of the document (the moved text's old spot returns to background colour and the new spot holds the text; an edited run keeps its font and size; a redacted phrase is gone from the extracted text, not merely covered). Every assertion was verified by breaking the behaviour it covers and watching it go red.

---

## Known issues

Surfaced by the test overhaul, not yet fixed:

- Replacement or added text longer than its box is clipped and lost (e.g. a long replacement, or a long added caption).
- Rewriting a page's content stream can split surviving text into single glyphs (`summary` → `s u m m a r y`), affecting copy/paste and search in other readers.
- Redaction on a page rotated with `/Rotate 90` paints the box but does not remove the text beneath it — the words stay selectable under an opaque rectangle. (Redaction on unrotated pages, and search-and-mark redaction, remove text correctly.)

Carried over from earlier releases:

- Find & replace stamps replacements at the correct position but appends them at the end of the content stream.
- Form JavaScript beyond the supported grammar (loops, conditionals, `submitForm`, `app.launchURL`) still runs only in a full reader; the viewer says so rather than failing silently.
